using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

var builder = WebApplication.CreateSlimBuilder(args);
using var versionResource = typeof(TrackerBridge).Assembly.GetManifestResourceStream("Companion.VERSION")
    ?? throw new InvalidOperationException("The build is missing its version.");
using var versionReader = new StreamReader(versionResource);
var version = versionReader.ReadToEnd().Trim();
var port = builder.Configuration.GetValue("port", 7292);
var planner = builder.Configuration["planner"] is { } path ? Path.GetFullPath(path) :
    Path.Combine(AppContext.BaseDirectory, "bdo-market-intelligence.html");
var sample = builder.Configuration.GetValue("sample", false);
var bridge = new TrackerBridge(sample);
var market = new MarketBook();
if (builder.Configuration["durability-region"] is { } region)
{
    if (sample || !OperatingSystem.IsWindows())
        throw new InvalidOperationException("Live capture requires Windows and cannot run in sample mode.");
    var area = CropArea.Parse(region);
    var worker = Path.GetFullPath(builder.Configuration["ocr-worker"] ??
        Path.Combine(AppContext.BaseDirectory, "vision-windows-ocr.exe"));
    if (!File.Exists(worker))
        throw new FileNotFoundException("Build the Windows OCR worker or specify --ocr-worker.", worker);
    var pace = builder.Configuration.GetValue("seconds-per-craft", 0d);
    if (!double.IsFinite(pace) || pace <= 0 || pace > 3600)
        throw new ArgumentException("Set --seconds-per-craft to a number between 0 and 3600.");
    builder.Services.AddHostedService(services => new DurabilityTracker(
        area, worker, pace, bridge, services.GetRequiredService<ILogger<DurabilityTracker>>()));
}
var authority = $"127.0.0.1:{port}";
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false;
    server.Listen(IPAddress.Loopback, port);
    server.Limits.MaxRequestBodySize = 250_000;
    server.Limits.MaxConcurrentConnections = 16;
});
var app = builder.Build();
if (OperatingSystem.IsWindows() && builder.Configuration.GetValue("tray", !sample))
    app.Lifetime.ApplicationStarted.Register(() => TrackerTray.Start(app.Lifetime, app.Logger, port));

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!string.Equals(context.Request.Host.Value, authority, StringComparison.Ordinal))
    {
        context.Response.StatusCode = 400;
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Json(
    new BridgeHealth(true, "bdo-vision", version, 13, bridge.InstanceId,
        bridge.Reading.Signal, bridge.LastAcknowledgedSignal, port, Environment.ProcessId,
        File.Exists(planner), sample), BridgeJson.Default.BridgeHealth));
app.MapGet("/durability", () => Results.Json(bridge.Reading, BridgeJson.Default.DurabilityReading));
IResult OpenPlanner() => File.Exists(planner)
    ? Results.File(planner, "text/html; charset=utf-8")
    : Error("Start Companion with --planner and the rc.29 planner HTML path.", 404);
app.MapGet("/", OpenPlanner);
app.MapGet("/planner", OpenPlanner);
app.MapGet("/prices", () => Results.Json(market.Snapshot(), BridgeJson.Default.PriceSnapshot));
var origin = $"http://{authority}";
MapJsonPost("/ack", 4096, BridgeJson.Default.Acknowledgment, acknowledgment =>
{
    if (acknowledgment is null || !bridge.TryAcknowledge(acknowledgment))
        return Error("Unknown event", 409);
    return Results.Json(new AcknowledgmentResult(true, acknowledgment.Signal),
        BridgeJson.Default.AcknowledgmentResult);
});
MapJsonPost("/market-prices", 250_000, BridgeJson.Default.MarketImport, import =>
    market.TryImport(import, out var count)
        ? Results.Json(new MarketImportResult(true, count), BridgeJson.Default.MarketImportResult)
        : Error("Invalid market quotes or quote limit reached", 400));
MapJsonPost("/craft-quotes", 16384, BridgeJson.Default.RecipeLookup, lookup =>
    market.Lookup(lookup) is { } quotes
        ? Results.Json(quotes, BridgeJson.Default.MarketQuotes)
        : Error("Invalid recipe lookup", 400));
app.Run();

void MapJsonPost<T>(string route, int limit, JsonTypeInfo<T> schema, Func<T?, IResult> handle)
{
    app.MapPost(route, async (HttpContext context) =>
    {
        var result = await ReadJsonPost(context, origin, limit, schema, handle);
        await result.ExecuteAsync(context);
    });
}

static async Task<IResult> ReadJsonPost<T>(HttpContext context, string origin, int limit,
    JsonTypeInfo<T> schema, Func<T?, IResult> handle)
{
    var request = context.Request;
    if (!string.Equals(request.Headers.Origin.ToString(), origin, StringComparison.Ordinal))
        return Error("Origin not allowed", 403);
    if (!request.HasJsonContentType())
        return Error("JSON required", 415);
    if (request.ContentLength is not > 0 || request.ContentLength > limit ||
        request.Headers.ContainsKey("Transfer-Encoding") ||
        request.Headers.ContainsKey("Content-Encoding"))
        return Error("Invalid request body framing", 400);

    try
    {
        var value = await JsonSerializer.DeserializeAsync(request.Body, schema, context.RequestAborted);
        return handle(value);
    }
    catch (JsonException)
    {
        return Error("Invalid JSON", 400);
    }
}

static IResult Error(string message, int status) =>
    Results.Json(new BridgeError(message), BridgeJson.Default.BridgeError, statusCode: status);

sealed class TrackerBridge
{
    private readonly object gate = new();
    private int lastAcknowledgedSignal;
    private DurabilityReading reading;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public DurabilityReading Reading => Volatile.Read(ref reading);
    public int LastAcknowledgedSignal => Volatile.Read(ref lastAcknowledgedSignal);

    public TrackerBridge(bool sample)
    {
        var timestamp = sample ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d : 0;
        reading = new DurabilityReading(sample ? 45 : 0, sample ? 60 : 3000,
            sample ? 2 : 0, sample ? 1 : 0, timestamp, timestamp, InstanceId,
            sample ? "sample" : "", 13);
    }

    public void UpdateDurability(int current, int maximum, double pace, double observedAt)
    {
        lock (gate)
        {
            if (reading.Current == current && reading.Maximum == maximum && reading.SecondsPerCraft == pace)
                return;
            var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            Volatile.Write(ref reading, new DurabilityReading(current, maximum, pace,
                reading.Signal + 1, sentAt, observedAt, InstanceId, "live", 13));
        }
    }

    public bool TryAcknowledge(Acknowledgment acknowledgment)
    {
        lock (gate)
        {
            if (acknowledgment.InstanceId != InstanceId ||
                acknowledgment.Signal <= 0 || acknowledgment.Signal > reading.Signal)
                return false;
            Volatile.Write(ref lastAcknowledgedSignal, Math.Max(lastAcknowledgedSignal, acknowledgment.Signal));
            return true;
        }
    }
}

sealed record DurabilityReading(int Current, int Maximum, double SecondsPerCraft, int Signal,
    double SentAt, double ObservedAt, string InstanceId, string SessionId, int BridgeVersion);
sealed record BridgeHealth(bool Ok, string AppId, string AppVersion, int BridgeVersion,
    string InstanceId, int Signal, int LastAckSignal, int Port, int Pid, bool PlannerExists, bool Sample);
sealed record Acknowledgment(string InstanceId, int Signal);
sealed record AcknowledgmentResult(bool Ok, int AcknowledgedSignal);
sealed record BridgeError(string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DurabilityReading))]
[JsonSerializable(typeof(BridgeHealth))]
[JsonSerializable(typeof(Acknowledgment))]
[JsonSerializable(typeof(AcknowledgmentResult))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(MarketImport))]
[JsonSerializable(typeof(MarketImportResult))]
[JsonSerializable(typeof(RecipeLookup))]
[JsonSerializable(typeof(MarketQuotes))]
[JsonSerializable(typeof(PriceSnapshot))]
partial class BridgeJson : JsonSerializerContext;
