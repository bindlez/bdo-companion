using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);
using var versionResource = typeof(TrackerBridge).Assembly.GetManifestResourceStream("Companion.VERSION")
    ?? throw new InvalidOperationException("The build is missing its version.");
using var versionReader = new StreamReader(versionResource);
var version = versionReader.ReadToEnd().Trim();
var port = builder.Configuration.GetValue("port", 7292);
var planner = builder.Configuration["planner"] is { } path ? Path.GetFullPath(path) : null;
var sample = builder.Configuration.GetValue("sample", false);
var bridge = new TrackerBridge(sample);
var authority = $"127.0.0.1:{port}";
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false;
    server.Listen(IPAddress.Loopback, port);
    server.Limits.MaxRequestBodySize = 4096;
    server.Limits.MaxConcurrentConnections = 16;
});
var app = builder.Build();

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
app.MapPost("/ack", async (HttpContext context) =>
{
    var result = await Acknowledge(context, bridge, $"http://{authority}");
    await result.ExecuteAsync(context);
});
app.Run();

static async Task<IResult> Acknowledge(HttpContext context, TrackerBridge bridge, string origin)
{
    var request = context.Request;
    if (!string.Equals(request.Headers.Origin.ToString(), origin, StringComparison.Ordinal))
        return Error("Origin not allowed", 403);
    if (!request.HasJsonContentType())
        return Error("JSON required", 415);
    if (request.ContentLength is not (> 0 and <= 4096) ||
        request.Headers.ContainsKey("Transfer-Encoding") ||
        request.Headers.ContainsKey("Content-Encoding"))
        return Error("Invalid request body framing", 400);

    Acknowledgment? acknowledgment;
    try
    {
        acknowledgment = await JsonSerializer.DeserializeAsync(
            request.Body, BridgeJson.Default.Acknowledgment, context.RequestAborted);
    }
    catch (JsonException)
    {
        return Error("Invalid acknowledgment", 400);
    }

    if (acknowledgment is null || !bridge.TryAcknowledge(acknowledgment))
        return Error("Unknown event", 409);
    return Results.Json(new AcknowledgmentResult(true, acknowledgment.Signal),
        BridgeJson.Default.AcknowledgmentResult);
}

static IResult Error(string message, int status) =>
    Results.Json(new BridgeError(message), BridgeJson.Default.BridgeError, statusCode: status);

sealed class TrackerBridge
{
    private int lastAcknowledgedSignal;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public DurabilityReading Reading { get; }
    public int LastAcknowledgedSignal => Volatile.Read(ref lastAcknowledgedSignal);

    public TrackerBridge(bool sample)
    {
        var timestamp = sample ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d : 0;
        Reading = new DurabilityReading(sample ? 45 : 0, sample ? 60 : 0,
            sample ? 2 : 0, sample ? 1 : 0, timestamp, timestamp, InstanceId,
            sample ? "sample" : "", 13);
    }

    public bool TryAcknowledge(Acknowledgment acknowledgment)
    {
        if (acknowledgment.InstanceId != InstanceId ||
            acknowledgment.Signal <= 0 || acknowledgment.Signal != Reading.Signal)
            return false;
        Interlocked.Exchange(ref lastAcknowledgedSignal, acknowledgment.Signal);
        return true;
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
partial class BridgeJson : JsonSerializerContext;
