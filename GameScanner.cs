using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class GameScanner(
    CaptureRegions regions, string executable, double secondsPerCraft, TrackerBridge bridge,
    SessionLedger ledger, ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var tool = regions.Durability is { } toolArea ? new ScreenCrop(toolArea) : null;
        using var loot = regions.Loot is { } lootArea ? new ScreenCrop(lootArea) : null;
        using var guild = regions.Guild is { } guildArea ? new ScreenCrop(guildArea) : null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var worker = new WindowsOcr(executable);
        (int Current, int Maximum)? previousTool = null;
        QuestCounter? previousGuild = null;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var usedWorker = false;

                if (tool is not null)
                {
                    var observation = await Read(tool);
                    if (tool.Moved) previousTool = null;
                    var reading = ParseDurability(observation.Text);
                    if (reading is { } confirmed && confirmed == previousTool)
                        bridge.UpdateDurability(confirmed.Current, confirmed.Maximum, secondsPerCraft, observation.At);
                    previousTool = reading;
                }

                if (loot is not null && ledger.State is { Status: "running" } session)
                {
                    var observation = await Read(loot);
                    if (loot.Moved) ledger.Observe(session.Generation, null, observation.At);
                    ledger.Observe(session.Generation, FeedReader.ParseLoot(observation.Text), observation.At);
                }

                if (guild is not null)
                {
                    var observation = await Read(guild);
                    if (guild.Moved) previousGuild = null;
                    var reading = FeedReader.ParseGuild(observation.Text);
                    if (reading is not null && reading == previousGuild)
                        bridge.UpdateGuild(reading, observation.At);
                    else
                        bridge.HideGuild();
                    previousGuild = reading;
                }

                if (!usedWorker) worker.Stop();

                async Task<(string? Text, double At)> Read(ScreenCrop crop)
                {
                    var frame = crop.Read();
                    if (frame is null) return (null, 0);
                    usedWorker = true;
                    var at = SessionLedger.Timestamp();
                    var text = await worker.Read(frame, stoppingToken);
                    return crop.IsCurrent() ? (text, at) : (null, at);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested &&
                exception is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Game scanning paused: {Reason}", exception.Message);
                previousTool = null;
                previousGuild = null;
                ledger.Observe(ledger.State.Generation, null, 0);
                bridge.HideGuild();
                worker.Stop();
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    internal static (int Current, int Maximum)? ParseDurability(string? text)
    {
        var match = DurabilityPattern().Match(text?.Trim() ?? "");
        if (!match.Success) return null;
        var current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var maximum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return maximum > 0 && current <= maximum ? (current, maximum) : null;
    }

    [GeneratedRegex(@"\A(?:Durability\s*:?\s*)?([0-9]{1,6})\s*/\s*([0-9]{1,6})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurabilityPattern();
}

internal sealed record CaptureRegions(CropArea? Durability, CropArea? Loot, CropArea? Guild);

internal sealed class WindowsOcr(string executable) : IDisposable
{
    private readonly byte[] header = new byte[4];
    private Process? process;

    public async Task<string?> Read(byte[] frame, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        if (process is null)
        {
            process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }) ?? throw new IOException("Could not start Windows OCR.");
            using var ready = JsonDocument.Parse(await ReadReply(token));
            if (!ready.RootElement.TryGetProperty("ready", out var value) || value.ValueKind != JsonValueKind.True)
                throw new IOException("Windows English OCR is unavailable.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
        var input = process.StandardInput.BaseStream;
        await input.WriteAsync(header, token);
        await input.WriteAsync(frame, token);
        await input.FlushAsync(token);
        using var reply = JsonDocument.Parse(await ReadReply(token));
        return reply.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() : null;
    }

    private async Task<string> ReadReply(CancellationToken token) =>
        await process!.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Windows OCR stopped.");

    public void Stop()
    {
        if (process is null) return;
        using var child = process;
        process = null;
        try
        {
            if (!child.HasExited) child.Kill();
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => Stop();
}
