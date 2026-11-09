using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<CompanionApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

internal sealed class CompanionApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var port = 7292;
            if (desktop.Args is ["--port", var value] && int.TryParse(value, out var chosen) && chosen is > 0 and <= 65535)
                port = chosen;
            desktop.MainWindow = new CompanionWindow(port);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class CompanionWindow : Window
{
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock status = new() { Text = "Connecting to tracker..." };
    private readonly TextBlock craft = new() { Text = "Waiting for a durability reading.", FontSize = 19 };
    private readonly TextBlock marketStatus = new() { Text = "Look up an exact item name or ID in imported quotes." };
    private readonly TextBox item = new() { PlaceholderText = "Item name or ID" };
    private readonly ComboBox region = new() { ItemsSource = new[] { "NA", "EU" }, SelectedIndex = 0 };
    private readonly ListBox quotes = new() { MinHeight = 120 };
    private readonly TextBlock guild = new() { Text = "Guild capture disabled." };
    private readonly TextBlock sessionStatus = new() { Text = "No active loot session." };
    private readonly ListBox history = new() { MinHeight = 120 };
    private readonly StackPanel sessionControls = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button start = new() { Content = "Start" };
    private readonly Button pause = new() { Content = "Pause" };
    private readonly Button resume = new() { Content = "Resume" };
    private readonly Button finish = new() { Content = "Finish" };
    private string? instance;
    private Session? session;
    private bool commanding;
    private bool refreshing;

    public CompanionWindow(int port)
    {
        Title = "BDO Companion";
        Width = 630;
        Height = 690;
        MinWidth = 410;
        MinHeight = 330;
        client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(3)
        };

        var planner = new Button { Content = "Open browser planner" };
        planner.Click += (_, _) =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(new Uri(client.BaseAddress!, "planner#craft").AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                status.Text = "Could not open the browser.";
            }
        };

        var lookup = new Button { Content = "Look up quote" };
        lookup.Click += async (_, _) => await LookupQuote(lookup);
        sessionControls.Children.Add(start);
        sessionControls.Children.Add(pause);
        sessionControls.Children.Add(resume);
        sessionControls.Children.Add(finish);
        start.Click += async (_, _) => await SessionAction("start");
        pause.Click += async (_, _) => await SessionAction("pause");
        resume.Click += async (_, _) => await SessionAction("resume");
        finish.Click += async (_, _) => await SessionAction("finish");
        SetControls();
        Content = new ScrollViewer { Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "BDO Companion", FontSize = 27 },
                status, craft, guild, planner,
                new Separator(),
                new TextBlock { Text = "Loot sessions", FontSize = 19 },
                sessionStatus, sessionControls, history,
                new Separator(),
                new TextBlock { Text = "Recipe market quotes", FontSize = 19 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { region, item, lookup } },
                marketStatus, quotes
            }
        } };

        poll.Tick += async (_, _) => await Refresh();
        Opened += (_, _) => poll.Start();
        Closed += (_, _) =>
        {
            poll.Stop();
            lifetime.Cancel();
            client.Dispose();
            lifetime.Dispose();
        };
    }

    private async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var cancellation = lifetime.Token;
            var health = await client.GetFromJsonAsync("health", DesktopJson.Default.Health, cancellation);
            if (cancellation.IsCancellationRequested) return;
            var reading = await client.GetFromJsonAsync("durability", DesktopJson.Default.Reading, cancellation);
            status.Text = health is null ? "Tracker unavailable." :
                $"Tracker {health.AppVersion}" + (health.Sample ? " · Sample mode" : "");
            craft.Text = reading is { Signal: > 0 }
                ? $"Last verified durability: {reading.Current:N0} / {reading.Maximum:N0} · {reading.SecondsPerCraft:g} s per craft"
                : "Waiting for a durability reading.";
            guild.Text = reading?.GuildStatus switch
            {
                { Enabled: false } or null => "Guild capture disabled.",
                { Current: null } => "Waiting for a guild reading.",
                { Available: true } value => $"Guild progress: {value.Current:N0} / {value.Target:N0}",
                var value => $"Guild last seen: {value.Current:N0} / {value.Target:N0}"
            };
            instance = health?.InstanceId;
            session = await client.GetFromJsonAsync("companion/session", DesktopJson.Default.Session, cancellation);
            var recent = await client.GetFromJsonAsync("companion/history", DesktopJson.Default.History, cancellation);
            sessionStatus.Text = session switch
            {
                { Enabled: false } => "Start the tracker with a loot region to record.",
                { Status: "running", Gaps: > 0 } value => $"Loot session running · {value.Gaps:N0} unreadable or ambiguous intervals.",
                { Status: "running" } => "Loot session running.",
                { Status: "paused" } => "Loot session paused.",
                _ => "No active loot session."
            };
            history.ItemsSource = recent?.Sessions.Select(row =>
                $"{DateTimeOffset.FromUnixTimeSeconds((long)row.StartedAt).ToLocalTime():g}  ·  {row.Events:N0} events  ·  {row.Quantity:N0} items").ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            status.Text = "Tracker disconnected. Start the tracker to reconnect.";
            craft.Text = "Durability unavailable.";
            guild.Text = "Guild status unavailable.";
            instance = null;
            session = null;
        }
        finally
        {
            refreshing = false;
            SetControls();
        }
    }

    private void SetControls()
    {
        var enabled = !commanding && instance is not null && session is { Enabled: true };
        start.IsEnabled = enabled && session?.Status == "idle";
        pause.IsEnabled = enabled && session?.Status == "running";
        resume.IsEnabled = enabled && session?.Status == "paused";
        finish.IsEnabled = enabled && session?.Id is not null;
    }

    private async Task SessionAction(string action)
    {
        if (commanding || instance is null || session is null) return;
        commanding = true;
        SetControls();
        try
        {
            var command = new SessionCommand(instance, session.Id, action, session.Generation);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(command, DesktopJson.Default.SessionCommand);
            using var message = new HttpRequestMessage(HttpMethod.Post, "companion/session");
            message.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = new("application/json");
            using var response = await client.SendAsync(message, lifetime.Token);
            response.EnsureSuccessStatusCode();
            await Refresh();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            sessionStatus.Text = "Session command unavailable. Start the tracker with a loot region.";
        }
        finally
        {
            commanding = false;
            SetControls();
        }
    }

    private async Task LookupQuote(Button button)
    {
        if (item.Text?.Trim() is not { Length: > 0 and <= 100 } name)
        {
            marketStatus.Text = "Enter an item name or ID.";
            return;
        }
        button.IsEnabled = false;
        try
        {
            var cancellation = lifetime.Token;
            var request = int.TryParse(name, out var id)
                ? new Lookup((string)region.SelectedItem!, [id], null)
                : new Lookup((string)region.SelectedItem!, null, [name]);
            using var message = new HttpRequestMessage(HttpMethod.Post, "craft-quotes");
            message.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, DesktopJson.Default.Lookup);
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = new("application/json");
            using var response = await client.SendAsync(message, cancellation);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(DesktopJson.Default.QuoteList, cancellation);
            quotes.ItemsSource = result?.Items.Select(quote => $"{quote.Name}  ·  {quote.Price:N0} silver").ToArray();
            marketStatus.Text = result?.Items.Length > 0 ? "Imported browser market quotes." : "No matching quote in the tracker cache.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            marketStatus.Text = "Quote lookup failed. Check the tracker connection and item ID.";
            quotes.ItemsSource = null;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}

internal sealed record Health(string AppVersion, bool Sample, string InstanceId);
internal sealed record Reading(int Current, int Maximum, double SecondsPerCraft, int Signal, GuildStatus? GuildStatus);
internal sealed record GuildStatus(bool Enabled, bool Available, int? Current, int? Target);
internal sealed record Session(string? Id, string Status, long Generation, bool Enabled, int Gaps);
internal sealed record SessionCommand(string InstanceId, string? SessionId, string Action, long Generation);
internal sealed record History(SessionRow[] Sessions);
internal sealed record SessionRow(double StartedAt, long Events, long Quantity);
internal sealed record Lookup(string Region, int[]? Ids, string[]? Names);
internal sealed record QuoteList(Quote[] Items);
internal sealed record Quote(string Name, long Price);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(Reading))]
[JsonSerializable(typeof(Lookup))]
[JsonSerializable(typeof(QuoteList))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SessionCommand))]
[JsonSerializable(typeof(History))]
internal partial class DesktopJson : JsonSerializerContext;
