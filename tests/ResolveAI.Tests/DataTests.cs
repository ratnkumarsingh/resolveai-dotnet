using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using ResolveAI.App.Data;
using ResolveAI.Contracts;

namespace ResolveAI.Tests;

public class DataTests
{
    private static JsonArray Dump(Db db, string table)
    {
        using var conn = db.Open();
        using var reader = conn.ExecuteReader($"SELECT * FROM {table} ORDER BY rowid");
        var rows = new JsonArray();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    long n => JsonValue.Create(n),
                    double d => JsonValue.Create(d),
                    string s => JsonValue.Create(s),
                    var other => throw new InvalidOperationException($"Unexpected column type {other.GetType()}"),
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("log_events")]
    [InlineData("metric_snapshots")]
    [InlineData("timeline_events")]
    [InlineData("deployments")]
    [InlineData("knowledge_documents")]
    [InlineData("knowledge_fts")]
    [InlineData("historical_incidents")]
    [InlineData("historical_fts")]
    public void Seed_matches_the_Node_seed_row_for_row(string table)
    {
        using var db = new Db(":memory:");
        Seed.Demo(db);
        Golden.AssertSame(Golden.Load("seed")[table], Golden.Normalise(Dump(db, table)), table);
    }

    [Fact]
    public void Stable_ids_match_the_Node_algorithm()
    {
        // Taken from the seed: recorded fixtures cite these exact IDs.
        var first = Golden.Load("seed")["incidents"]!.AsArray().First(i => i!["key"]!.GetValue<string>() == "INC-1042")!;
        Assert.Equal(first["id"]!.GetValue<string>(), EvidenceIds.Stable("incident:INC-1042"));
    }

    [Fact]
    public void Migrations_keep_their_filenames_so_an_existing_database_is_not_migrated_twice()
    {
        using var db = new Db(":memory:");
        Assert.Equal(["001_init.sql", "002_trace_review.sql", "003_tool_calls.sql"], db.All<string>("SELECT name FROM _migrations ORDER BY name"));
    }

    [Fact]
    public void Foreign_keys_are_enforced_and_log_events_are_immutable()
    {
        using var db = new Db(":memory:");
        Seed.Demo(db);
        Assert.ThrowsAny<Exception>(() => db.Run("INSERT INTO hypotheses (id, investigation_id, rank, title, explanation, confidence, score) VALUES ('h', 'missing', 1, 't', 'e', 'low', 0)"));
        Assert.ThrowsAny<Exception>(() => db.Run("UPDATE log_events SET message = 'tampered'"));
    }

    [Fact]
    public void Dates_format_like_toISOString()
    {
        Assert.Equal("2026-10-27T10:00:00.000Z", Js.Iso(Js.ParseDate("2026-10-27T10:00:00Z")));
        Assert.Equal("2026-10-27T10:00:00Z", Js.IsoSeconds(Js.ParseDate("2026-10-27T10:00:00.000Z")));
        Assert.False(Js.TryParseDate("not a date", out _));
    }

    [Fact]
    public void Json_does_not_escape_text_the_size_budget_counts()
    {
        Assert.Equal("{\"impact\":\"a → b … ×2 <x> & 'y'\"}", Json.Serialize(new Dictionary<string, string> { ["impact"] = "a → b … ×2 <x> & 'y'" }));
    }

    [Fact]
    public void Numbers_print_like_JavaScript()
    {
        Assert.Equal("0.2", Js.Num(0.2));
        Assert.Equal("28", Js.Num(28));
        Assert.Equal("18000", Js.Num(18000));
    }
}

/// <summary>Opt-in: point RESOLVEAI_REAL_DB_COPY at a copy of a real database. Never point it at the original, since opening it can apply migrations.</summary>
public class RealDatabaseCopyTests
{
    [Fact]
    public void A_copy_of_an_existing_database_opens_and_reads()
    {
        var path = Environment.GetEnvironmentVariable("RESOLVEAI_REAL_DB_COPY");
        if (string.IsNullOrEmpty(path)) return;
        using var db = new Db(path);
        Assert.Equal(6, db.Get<int>("SELECT COUNT(*) FROM incidents"));
        Assert.NotEmpty(db.All<string>("SELECT id FROM investigations"));
        Assert.Equal(3, db.Get<int>("SELECT COUNT(*) FROM _migrations"));
    }
}
