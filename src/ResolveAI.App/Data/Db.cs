using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ResolveAI.App.Data;

/// <summary>
/// The SQLite database. Each operation opens its own pooled connection, because investigations run in the background
/// alongside requests. An in-memory database is shared between connections and kept alive by one held open here.
/// </summary>
public sealed class Db : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>Opens (or creates) the database and applies pending migrations. Use ":memory:" for tests.</summary>
    public Db(string path)
    {
        var builder = new SqliteConnectionStringBuilder { ForeignKeys = true, DefaultTimeout = 30 };
        if (path == ":memory:")
        {
            builder.DataSource = $"resolveai-{Guid.NewGuid():N}";
            builder.Mode = SqliteOpenMode.Memory;
            builder.Cache = SqliteCacheMode.Shared;
            _connectionString = builder.ToString();
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is not null) Directory.CreateDirectory(folder);
            builder.DataSource = path;
            _connectionString = builder.ToString();
            using var conn = Open();
            conn.Execute("PRAGMA journal_mode = WAL;");
        }
        Migrate();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Open();
        conn.Execute("CREATE TABLE IF NOT EXISTS _migrations (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");
        var applied = conn.Query<string>("SELECT name FROM _migrations").ToHashSet();
        foreach (var (name, sql) in Migrations())
        {
            if (applied.Contains(name)) continue;
            using var tx = conn.BeginTransaction();
            conn.Execute(sql, transaction: tx);
            conn.Execute("INSERT INTO _migrations (name, applied_at) VALUES (@name, @at)", new { name, at = Contracts.Js.IsoNow() }, tx);
            tx.Commit();
        }
    }

    /// <summary>Migrations are embedded under their original filenames, which is how _migrations records them.</summary>
    private static IEnumerable<(string Name, string Sql)> Migrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string marker = ".Data.Migrations.";
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.Contains(marker) && r.EndsWith(".sql")).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            yield return (resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..], reader.ReadToEnd());
        }
    }

    public List<T> All<T>(string sql, object? args = null)
    {
        using var conn = Open();
        return conn.Query<T>(sql, args).AsList();
    }

    public T? Get<T>(string sql, object? args = null)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<T>(sql, args);
    }

    public int Run(string sql, object? args = null)
    {
        using var conn = Open();
        return conn.Execute(sql, args);
    }

    /// <summary>Runs work inside a transaction; rolls back on throw.</summary>
    public void Tx(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        work(conn, tx);
        tx.Commit();
    }

    public void Dispose() => _keepAlive?.Dispose();
}
