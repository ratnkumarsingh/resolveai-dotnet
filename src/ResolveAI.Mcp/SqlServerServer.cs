using System.Data;
using Microsoft.Data.SqlClient;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

/// <summary>Runs one fixed statement with named parameters. Tests swap in a fake.</summary>
public delegate Task<List<Dictionary<string, object?>>> SqlQuery(string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);

public static class SqlServerConnector
{
    private const int CommandTimeoutSeconds = 8;

    /// <summary>Wraps a statement so it runs as the read-only login and always reverts, even when it fails.</summary>
    public static string AsLogin(string login, string text) =>
        $"EXECUTE AS LOGIN = N'{login}';\nBEGIN TRY\n;{text.Trim()};\nEND TRY\nBEGIN CATCH\n  REVERT;\n  THROW;\nEND CATCH;\nREVERT;";

    public static string ConnectionString(SqlServerConfig cfg, Func<string, string?> env)
    {
        // A SQL login comes only from the environment, never from the config file. ApplicationIntent=ReadOnly is set too,
        // as a hint; the reader login's permissions are what actually keep it read-only.
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = cfg.Port is { } port ? $"{cfg.Server},{port}" : cfg.Server,
            InitialCatalog = "master",
            TrustServerCertificate = cfg.TrustServerCertificate,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = 5,
            MaxPoolSize = 2,
            ApplicationName = "ResolveAI",
        };
        if (cfg.Driver == "msnodesqlv8")
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            var user = env("MSSQL_USER");
            var password = env("MSSQL_PASSWORD");
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) throw new ToolError("Set MSSQL_USER and MSSQL_PASSWORD in .env for the SQL Server reader login.");
            builder.UserID = user;
            builder.Password = password;
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
        }
        return builder.ConnectionString;
    }

    public static SqlQuery Connect(SqlServerConfig cfg, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return async (text, parameters, ct) =>
        {
            var connectionString = ConnectionString(cfg, env);
            await using var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (SqlException err)
            {
                throw new ToolError($"Could not connect to SQL Server {cfg.Server}: {err.Message}");
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrEmpty(cfg.ExecuteAs) ? text : AsLogin(cfg.ExecuteAs, text);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
            {
                var p = cmd.Parameters.AddWithValue("@" + name, value);
                if (value is DateTimeOffset or DateTime) p.SqlDbType = SqlDbType.DateTime2;
            }

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var read = false;
            do
            {
                if (read || reader.FieldCount == 0) continue;
                read = true;
                while (await reader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount);
                    for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                // Keep reading to the end: an error thrown after the rows only surfaces on the following results.
            } while (await reader.NextResultAsync(ct));
            return rows;
        };
    }
}

public static class SqlServerServer
{
    private static object? Plain(object? v) => v switch
    {
        DateTime d => Js.Iso(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        DateTimeOffset d => Js.Iso(d),
        decimal m => (double)m,
        Guid g => g.ToString(),
        _ => v,
    };

    /// <summary>Values become plain JSON types, with dates as ISO strings.</summary>
    private static List<Dictionary<string, object?>> Clean(IEnumerable<Dictionary<string, object?>> rows) =>
        rows.Select(r => r.ToDictionary(kv => kv.Key, kv => Plain(kv.Value))).ToList();

    private static double Number(object? v) => v is null ? 0 : Convert.ToDouble(v);

    /// <summary>"Permission denied" from SQL Server, reported so the agent knows the check is unavailable, not empty.</summary>
    private static async Task<(List<Dictionary<string, object?>> Rows, string? Error)> Attempt(SqlQuery q, string text, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        try
        {
            return (Clean(await q(text, parameters, ct)), null);
        }
        catch (Exception err) when (err is not ToolError and not OperationCanceledException)
        {
            return ([], err.Message);
        }
    }

    private static Dictionary<string, object> Range(TimeWindow w) => new() { ["from"] = w.From.UtcDateTime, ["to"] = w.To.UtcDateTime };

    public static ToolServer Build(McpConfig cfg, SqlQuery? query = null)
    {
        query ??= SqlServerConnector.Connect(cfg.Sqlserver);
        var q = query;
        var databases = cfg.Services.Values.Select(s => s.Database).Where(d => !string.IsNullOrEmpty(d)).Cast<string>().Distinct().ToList();

        string PickDb(string? db)
        {
            var name = db ?? databases.FirstOrDefault();
            if (name is null) throw new ToolError("No databases are configured for any service.");
            if (!databases.Contains(name)) throw new ToolError($"Database \"{name}\" is not in the allow-list ({string.Join(", ", databases)}).");
            return name;
        }

        var source = $"sqlserver/{cfg.Sqlserver.Server}";
        var databaseField = Field.Str("database", $"One of: {(databases.Count > 0 ? string.Join(", ", databases) : "(none configured)")}", required: false);

        return new ToolServer("resolveai-sqlserver",
        [
            new ToolDef("db_health",
                "Point-in-time SQL Server health: sessions and blocked requests per database, the top waits since the server started, and memory/CPU.",
                [],
                async (_, ct) =>
                {
                    var sessionsTask = q(SqlText.SessionsByDatabase, null, ct);
                    var waitsTask = q(SqlText.TopWaits, null, ct);
                    var infoTask = q(SqlText.SysInfo, null, ct);
                    await Task.WhenAll(sessionsTask, waitsTask, infoTask);
                    var (sessions, waits) = (sessionsTask.Result, waitsTask.Result);
                    var i = Clean(infoTask.Result).FirstOrDefault() ?? [];
                    var rows = new List<Dictionary<string, object?>> { Row.With("section", "server", i) };
                    rows.AddRange(Clean(sessions).Select(s => Row.With("section", "sessions", s)));
                    rows.AddRange(Clean(waits).Select(w => Row.With("section", "top_wait_since_start", w)));
                    var total = sessions.Sum(s => Number(s.GetValueOrDefault("sessions")));
                    var blocked = sessions.Sum(s => Number(s.GetValueOrDefault("blocked_requests")));
                    var top = waits.FirstOrDefault()?.GetValueOrDefault("wait_type") ?? "none";
                    return ToolResult.Ok($"SQL Server {i.GetValueOrDefault("version") ?? ""} at {Js.IsoNow()}: {Js.Num(total)} user session(s), {Js.Num(blocked)} blocked request(s); top wait since start: {top}.", rows, source);
                }),

            new ToolDef("db_blocking",
                "Point-in-time blocking chains: which sessions are waiting on which, for how long, on what resource, and what the head blocker last ran.",
                [],
                async (_, ct) =>
                {
                    var blockedTask = q(SqlText.Blocked, null, ct);
                    var headsTask = q(SqlText.HeadBlockers, null, ct);
                    await Task.WhenAll(blockedTask, headsTask);
                    var (blocked, heads) = (blockedTask.Result, headsTask.Result);
                    var rows = Clean(heads).Select(h => Row.With("role", "head_blocker", h)).Concat(Clean(blocked).Select(b => Row.With("role", "blocked", b))).ToList();
                    var longest = blocked.Aggregate(0d, (m, b) => Math.Max(m, Number(b.GetValueOrDefault("wait_ms"))));
                    return ToolResult.Ok(blocked.Count > 0
                        ? $"{blocked.Count} blocked request(s) behind {heads.Count} head blocker(s) (session {string.Join(", ", heads.Select(h => Js.Str(h.GetValueOrDefault("session_id"))))}); longest wait {Js.Num(Math.Round(longest / 1000, MidpointRounding.AwayFromZero))}s."
                        : $"No blocking right now ({Js.IsoNow()}).", rows, source);
                }),

            new ToolDef("db_long_queries",
                "Requests that have been running for at least min_seconds right now, longest first, with their query text.",
                [Field.Int("min_seconds", 1, 3600, 30)],
                async (args, ct) =>
                {
                    var minSeconds = args.Int("min_seconds");
                    var rows = Clean(await q(SqlText.LongRequests, new Dictionary<string, object> { ["min_ms"] = minSeconds * 1000 }, ct));
                    return ToolResult.Ok(rows.Count > 0
                        ? $"{rows.Count} request(s) running {minSeconds}s or longer; longest {Js.Str(rows[0].GetValueOrDefault("elapsed_s"))}s."
                        : $"No requests running {minSeconds}s or longer.", rows, source);
                }),

            new ToolDef("db_deadlocks",
                "Deadlocks recorded by the system_health session between from and to: each process involved, its lock and query, and which one was the victim.",
                [Field.Time("from"), Field.Time("to", required: false)],
                async (args, ct) =>
                {
                    var w = TimeWindow.Parse(args.Str("from"), args.OptStr("to"));
                    var (rows, error) = await Attempt(q, SqlText.Deadlocks, Range(w), ct);
                    if (error is not null) throw new ToolError($"Cannot read deadlock history: {error}");
                    var events = rows.Select(x => Js.Str(x.GetValueOrDefault("time"))).Distinct().Count();
                    return ToolResult.Ok(events > 0 ? $"{events} deadlock(s) between {Js.Iso(w.From)} and {Js.Iso(w.To)}." : "No deadlocks recorded in that window.", rows, source);
                }),

            new ToolDef("db_errors",
                "SQL Server errors between from and to: severe errors from system_health, plus severity 11+ errors (lock timeouts, conversion failures) " +
                "if the optional resolveai_errors session is running. Optionally filter by a phrase in the message.",
                [Field.Time("from"), Field.Time("to", required: false), Field.Str("phrase", required: false, max: 200)],
                async (args, ct) =>
                {
                    var w = TimeWindow.Parse(args.Str("from"), args.OptStr("to"));
                    var healthTask = Attempt(q, SqlText.SystemHealthErrors, Range(w), ct);
                    var sessionTask = Attempt(q, SqlText.ResolveaiSessionErrors, Range(w), ct);
                    await Task.WhenAll(healthTask, sessionTask);
                    var (health, session) = (healthTask.Result, sessionTask.Result);
                    if (health.Error is not null && session.Error is not null) throw new ToolError($"Cannot read SQL Server error history: {health.Error}");
                    var needle = args.OptStr("phrase")?.ToLowerInvariant();
                    var rows = health.Rows.Concat(session.Rows)
                        .Where(r => needle is null || (r.GetValueOrDefault("message") as string ?? "").ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                        .OrderByDescending(r => Js.Str(r.GetValueOrDefault("time")), StringComparer.Ordinal)
                        .ToList();
                    var top = rows.GroupBy(r => Js.Str(r.GetValueOrDefault("error_number")))
                        .OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}");
                    return ToolResult.Ok(rows.Count > 0 ? $"{rows.Count} SQL Server error(s) in the window (most common: {string.Join(", ", top)})." : "No SQL Server errors recorded in that window.", rows, source);
                }),

            new ToolDef("db_space",
                "Data and log file sizes, used space and growth for one database, plus its state and why its log cannot be reused.",
                [databaseField],
                async (args, ct) =>
                {
                    var name = PickDb(args.OptStr("database"));
                    var quoted = $"[{name.Replace("]", "]]")}]";
                    var stateTask = q(SqlText.DatabaseState, new Dictionary<string, object> { ["db"] = name }, ct);
                    var filesTask = q($"EXEC {quoted}.sys.sp_executesql @stmt", new Dictionary<string, object> { ["stmt"] = SqlText.FileSpace }, ct);
                    await Task.WhenAll(stateTask, filesTask);
                    var (state, files) = (stateTask.Result, filesTask.Result);
                    if (state.Count == 0) throw new ToolError($"Database {name} does not exist on {cfg.Sqlserver.Server}.");
                    var s = state[0];
                    var rows = new List<Dictionary<string, object?>> { Row.With("section", "database", Clean(state)[0]) };
                    rows.AddRange(Clean(files).Select(f => Row.With("section", "file", f)));
                    var full = files.Count(f => f.GetValueOrDefault("max_mb") is not null && Number(f.GetValueOrDefault("used_mb")) >= 0.9 * Number(f.GetValueOrDefault("max_mb")));
                    return ToolResult.Ok($"{name} is {s.GetValueOrDefault("state_desc")}; log reuse wait: {s.GetValueOrDefault("log_reuse_wait_desc")}; {files.Count} file(s){(full > 0 ? $", {full} near max size" : "")}.", rows, source);
                }),

            new ToolDef("db_failed_jobs",
                "SQL Agent job steps that failed between from and to.",
                [Field.Time("from"), Field.Time("to", required: false)],
                async (args, ct) =>
                {
                    var w = TimeWindow.Parse(args.Str("from"), args.OptStr("to"));
                    var status = await Attempt(q, SqlText.AgentStatus, null, ct);
                    var agent = status.Rows.FirstOrDefault()?.GetValueOrDefault("status_desc");
                    if (agent is null) return ToolResult.Ok("SQL Agent is not available on this server.", [], source);
                    var (rows, error) = await Attempt(q, SqlText.FailedJobs, Range(w), ct);
                    if (error is not null) throw new ToolError($"Cannot read SQL Agent job history: {error}");
                    return ToolResult.Ok($"{rows.Count} failed job step(s) in the window; SQL Agent is {Js.Str(agent).ToLowerInvariant()}.", rows, source);
                }),
        ]);
    }
}
