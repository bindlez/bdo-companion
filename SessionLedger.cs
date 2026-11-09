using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal sealed partial class SessionLedger : IDisposable
{
    private readonly object gate = new();
    private readonly FileStream? profileLock;
    private nint database;
    private SessionState state = new(null, "idle", 0, false, 0);
    private LootRow[]? baseline;

    public SessionLedger(string? profile)
    {
        if (profile is null) return;
        Directory.CreateDirectory(profile);
        profileLock = new FileStream(Path.Combine(profile, "tracker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Check(Open(Path.Combine(profile, "sessions.sqlite"), out database, 6, 0));
            Check(Exec(database, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;" +
                "CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, started REAL NOT NULL, ended REAL);" +
                "CREATE TABLE IF NOT EXISTS loot(id INTEGER PRIMARY KEY, session TEXT NOT NULL REFERENCES sessions(id), observed REAL NOT NULL, item TEXT NOT NULL, quantity INTEGER NOT NULL, kind TEXT NOT NULL);" +
                "CREATE INDEX IF NOT EXISTS loot_session ON loot(session,id);" +
                "UPDATE sessions SET ended=COALESCE((SELECT MAX(observed) FROM loot WHERE session=sessions.id),started) WHERE ended IS NULL;",
                0, 0, 0));
            state = state with { Enabled = true };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Enabled => database != 0;
    public SessionState State { get { lock (gate) return state; } }

    public SessionState? Apply(string? action, string? expectedSession, long expectedGeneration)
    {
        lock (gate)
        {
            if (!Enabled || state.Id != expectedSession || state.Generation != expectedGeneration) return null;
            var now = Timestamp();
            var generation = state.Generation + 1;
            switch (action)
            {
                case "start" when state.Id is null:
                    var id = Guid.NewGuid().ToString("N");
                    Execute($"INSERT INTO sessions(id,started) VALUES({Text(id)},{Number(now)});");
                    state = new SessionState(id, "running", generation, true, 0);
                    break;
                case "pause" when state.Status == "running":
                    state = state with { Status = "paused", Generation = generation };
                    break;
                case "resume" when state.Status == "paused":
                    state = state with { Status = "running", Generation = generation };
                    break;
                case "finish" when state.Id is { } current:
                    Execute($"UPDATE sessions SET ended={Number(now)} WHERE id={Text(current)};");
                    state = new SessionState(null, "idle", generation, true, 0);
                    break;
                default:
                    return null;
            }
            baseline = null;
            return state;
        }
    }

    public void Observe(long generation, LootRow[]? rows, double observedAt)
    {
        lock (gate)
        {
            if (state is not { Id: { } id, Status: "running" } || state.Generation != generation) return;
            var additions = rows is not null && baseline is not null ? FeedReader.NewRows(baseline, rows) : null;
            if (baseline is not null && additions is null)
                state = state with { Gaps = state.Gaps + 1 };
            if (additions is { Length: > 0 })
            {
                var statements = new StringBuilder("BEGIN IMMEDIATE;");
                foreach (var row in additions)
                    statements.Append(CultureInfo.InvariantCulture,
                        $"INSERT INTO loot(session,observed,item,quantity,kind) VALUES({Text(id)},{Number(observedAt)},{Text(row.Item)},{row.Quantity},{Text(row.Kind)});");
                statements.Append("COMMIT;");
                try { Execute(statements.ToString()); }
                catch
                {
                    Exec(database, "ROLLBACK;", 0, 0, 0);
                    throw;
                }
            }
            baseline = rows;
        }
    }

    public SessionHistory History(string? id)
    {
        lock (gate)
        {
            if (!Enabled) return new SessionHistory([], [], id);
            var sessions = Query("SELECT s.id,s.started,s.ended,COUNT(l.id),COALESCE(SUM(l.quantity),0) " +
                "FROM (SELECT id,started,ended FROM sessions ORDER BY started DESC LIMIT 50) s " +
                "LEFT JOIN loot l ON l.session=s.id GROUP BY s.id ORDER BY s.started DESC;",
                stmt => new SessionSummary(ReadText(stmt, 0), ColumnDouble(stmt, 1),
                    ColumnType(stmt, 2) == 5 ? null : ColumnDouble(stmt, 2), ColumnInt64(stmt, 3), ColumnInt64(stmt, 4)));
            var items = id is null ? [] : Query(
                $"SELECT item,SUM(quantity),kind FROM loot WHERE session={Text(id)} GROUP BY item,kind ORDER BY SUM(quantity) DESC,item LIMIT 100;",
                stmt => new LootTotal(ReadText(stmt, 0), ColumnInt64(stmt, 1), ReadText(stmt, 2)));
            return new SessionHistory(sessions, items, id);
        }
    }

    private T[] Query<T>(string sql, Func<nint, T> read)
    {
        Check(Prepare(database, sql, -1, out var statement, 0));
        try
        {
            var rows = new List<T>();
            int result;
            while ((result = Step(statement)) == 100) rows.Add(read(statement));
            if (result != 101) Check(result);
            return rows.ToArray();
        }
        finally { FinalizeStatement(statement); }
    }

    private void Execute(string sql) => Check(Exec(database, sql, 0, 0, 0));
    private void Check(int result)
    {
        if (result != 0) throw new IOException($"SQLite error {result}: {Marshal.PtrToStringUTF8(ErrorMessage(database))}");
    }
    private static string Text(string value) => "CAST(X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "' AS TEXT)";
    private static string ReadText(nint statement, int column) => Marshal.PtrToStringUTF8(ColumnText(statement, column)) ?? "";
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static double Timestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    public void Dispose()
    {
        lock (gate)
        {
            if (database != 0) Close(database);
            database = 0;
            profileLock?.Dispose();
        }
    }

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, out nint db, int flags, nint vfs);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(nint db, string sql, nint callback, nint arg, nint error);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Prepare(nint db, string sql, int bytes, out nint statement, nint tail);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_step")] private static partial int Step(nint statement);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")] private static partial int FinalizeStatement(nint statement);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type")] private static partial int ColumnType(nint statement, int column);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_double")] private static partial double ColumnDouble(nint statement, int column);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64")] private static partial long ColumnInt64(nint statement, int column);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")] private static partial nint ColumnText(nint statement, int column);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg")] private static partial nint ErrorMessage(nint db);
    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2")] private static partial int Close(nint db);
}

internal sealed record SessionState(string? Id, string Status, long Generation, bool Enabled, int Gaps);
internal sealed record SessionCommand(string? InstanceId, string? SessionId, string? Action, long? Generation);
internal sealed record SessionSummary(string Id, double StartedAt, double? EndedAt, long Events, long Quantity);
internal sealed record LootTotal(string Item, long Quantity, string Kind);
internal sealed record SessionHistory(SessionSummary[] Sessions, LootTotal[] Items, string? SessionId);
