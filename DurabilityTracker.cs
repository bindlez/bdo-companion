using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class DurabilityTracker(CropArea area, string executable, double secondsPerCraft,
    TrackerBridge bridge, ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var crop = new ScreenCrop(area);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Process? worker = null;
        (int Current, int Maximum)? previous = null;
        var header = new byte[4];

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var frame = crop.Read();
                if (frame is null)
                {
                    previous = null;
                    StopWorker(ref worker);
                    continue;
                }

                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
                    if (worker is null)
                    {
                        worker = Process.Start(new ProcessStartInfo(executable)
                        {
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }) ?? throw new IOException("Could not start Windows OCR.");
                        using var ready = JsonDocument.Parse(await ReadReply(worker, deadline.Token));
                        if (!ready.RootElement.TryGetProperty("ready", out var value) || value.ValueKind != JsonValueKind.True)
                            throw new IOException("Windows English OCR is unavailable.");
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
                    var input = worker.StandardInput.BaseStream;
                    await input.WriteAsync(header, deadline.Token);
                    await input.WriteAsync(frame, deadline.Token);
                    await input.FlushAsync(deadline.Token);
                    using var reply = JsonDocument.Parse(await ReadReply(worker, deadline.Token));
                    var reading = crop.IsCurrent() && reply.RootElement.TryGetProperty("text", out var text)
                        ? ParseReading(text.GetString()) : null;
                    if (reading is { } confirmed && confirmed == previous)
                        bridge.UpdateDurability(confirmed.Current, confirmed.Maximum, secondsPerCraft, observedAt);
                    previous = reading;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested &&
                    exception is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning("Durability OCR paused: {Reason}", exception.Message);
                    previous = null;
                    StopWorker(ref worker);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }
        finally
        {
            StopWorker(ref worker);
        }
    }

    internal static (int Current, int Maximum)? ParseReading(string? text)
    {
        var match = DurabilityPattern().Match(text?.Trim() ?? "");
        if (!match.Success) return null;
        var current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var maximum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return maximum > 0 && current <= maximum ? (current, maximum) : null;
    }

    [GeneratedRegex(@"\A(?:Durability\s*:?\s*)?([0-9]{1,6})\s*/\s*([0-9]{1,6})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurabilityPattern();

    private static async Task<string> ReadReply(Process worker, CancellationToken cancellationToken) =>
        await worker.StandardOutput.ReadLineAsync(cancellationToken) ?? throw new IOException("Windows OCR stopped.");

    private static void StopWorker(ref Process? worker)
    {
        if (worker is null) return;
        using var process = worker;
        worker = null;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) { }
    }
}
