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
    private bool refreshing;

    public CompanionWindow(int port)
    {
        Title = "BDO Companion";
        Width = 630;
        Height = 460;
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
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "BDO Companion", FontSize = 27 },
                status, craft, planner,
                new Separator(),
                new TextBlock { Text = "Recipe market quotes", FontSize = 19 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { region, item, lookup } },
                marketStatus, quotes
            }
        };

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
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            status.Text = "Tracker disconnected. Start the tracker to reconnect.";
            craft.Text = "Durability unavailable.";
        }
        finally
        {
            refreshing = false;
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

internal sealed record Health(string AppVersion, bool Sample);
internal sealed record Reading(int Current, int Maximum, double SecondsPerCraft, int Signal);
internal sealed record Lookup(string Region, int[]? Ids, string[]? Names);
internal sealed record QuoteList(Quote[] Items);
internal sealed record Quote(string Name, long Price);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(Reading))]
[JsonSerializable(typeof(Lookup))]
[JsonSerializable(typeof(QuoteList))]
internal partial class DesktopJson : JsonSerializerContext;
