static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var first = FeedReader.ParseLoot("You have obtained [Black Stone] x 2.")!;
var next = FeedReader.ParseLoot("You have obtained [Black Stone] x 2.\nYou produced [Beer] x 1,000.")!;
Require(first is { Length: 1 } && first[0].Quantity == 2, "First row failed");
Require(next is { Length: 2 } && next[1].Quantity == 1000 && next[1].Kind == "Crafted", "Second row failed");
Require(FeedReader.ParseLoot("Someone has obtained [Black Stone] x 2") is null, "Other-player notification accepted");
Require(FeedReader.ParseLoot("You obtained [Black Stone] x 2.5") is null, "Malformed quantity accepted");
Require(FeedReader.ParseLoot("You obtained [Black Stone] x 0") is null, "Zero quantity accepted");
Require(FeedReader.NewRows(first, next) is { Length: 1 }, "Expected one additional row");
Require(FeedReader.NewRows(next, next) is { Length: 0 }, "Unchanged feed duplicated loot");
Require(FeedReader.NewRows([first[0], first[0]], [first[0], first[0], next[1]]) is null, "Ambiguous overlap accepted");
Require(FeedReader.ParseGuild("Guild Mission\n1,200 / 2,000") is { Current: 1200, Target: 2000 }, "Guild counter rejected");
Require(FeedReader.ParseGuild("Daily Quest\n1 / 2") is null, "Unscoped quest accepted");
Require(FeedReader.ParseGuild("Guild Mission\n3 / 2") is null, "Invalid guild counter accepted");

var folder = Path.Combine(Path.GetTempPath(), "bdo-companion-replay-" + Guid.NewGuid().ToString("N"));
string sessionId;
try
{
    using (var ledger = new SessionLedger(folder))
    {
        var session = ledger.Apply("start", null, 0)!;
        sessionId = session.Id!;
        ledger.Observe(session.Generation, first, 1);
        ledger.Observe(session.Generation, next, 2);
        ledger.Observe(session.Generation, next, 2);
        Require(ledger.History(sessionId).Items is [{ Item: "Beer", Quantity: 1000 }], "Ledger duplicated rows or counted baseline");
        var paused = ledger.Apply("pause", sessionId, session.Generation)!;
        var resumed = ledger.Apply("resume", sessionId, paused.Generation)!;
        ledger.Observe(session.Generation, first, 3);
        ledger.Observe(resumed.Generation, next, 4);
        Require(ledger.History(sessionId).Sessions is [{ Events: 1 }], "Pause/resume accepted stale observation");
        Require(ledger.Apply("finish", sessionId, resumed.Generation)?.Status == "idle", "Finish failed");
        Require(ledger.Apply("start", null, 0) is null, "Stale start accepted");
    }

    using var reopened = new SessionLedger(folder);
    Require(reopened.History(sessionId).Sessions is [{ Events: 1, EndedAt: not null }], "Session did not survive reopen");
    Require(reopened.History(sessionId).Items is [{ Item: "Beer", Quantity: 1000 }], "Loot did not survive reopen");
}
finally
{
    if (Directory.Exists(folder))
    {
        foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}

Console.WriteLine("Replay and disposable SQLite ledger checks passed.");
