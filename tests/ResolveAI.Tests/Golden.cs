using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.Tests;

/// <summary>Loads the files recorded from the Node implementation and compares against them, by the same rules as tools/golden/capture.mts.</summary>
public static partial class Golden
{
    private static readonly HashSet<string> VolatileKeys = ["duration_ms", "modified", "size_bytes", "characters", "cache_read_input_tokens"];

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}")]
    private static partial Regex RandomUuid();

    [GeneratedRegex(@"modified [0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z")]
    private static partial Regex Modified();

    [GeneratedRegex(@"\([0-9][0-9,]* bytes")]
    private static partial Regex Bytes();

    [GeneratedRegex(@"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?Z")]
    private static partial Regex IsoTime();

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.json");

    public static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path(name)))!;

    public static string NormaliseText(string s)
    {
        s = RandomUuid().Replace(s, "<uuid>");
        s = Modified().Replace(s, "modified <mtime>");
        s = Bytes().Replace(s, "(<n> bytes");
        return IsoTime().Replace(s, m => Js.TryParseDate(m.Value, out var t) && (t - Started).Duration() < TimeSpan.FromHours(6) ? "<now>" : m.Value);
    }

    public static JsonNode? Normalise(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, VolatileKeys.Contains(kv.Key) ? JsonValue.Create("<volatile>") : Normalise(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalise).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(NormaliseText(v.GetValue<string>())),
        _ => node?.DeepClone(),
    };

    public static JsonNode ToNode<T>(T value) => JsonNode.Parse(Json.Serialize(value))!;

    /// <summary>Fails with the path of the first difference, so a mismatch in a large document is easy to find.</summary>
    public static void AssertSame(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected)
        {
            case JsonObject e:
                var a = actual as JsonObject ?? throw new Xunit.Sdk.XunitException($"{path}: expected an object, got {actual?.ToJsonString() ?? "null"}");
                var missing = e.Select(kv => kv.Key).Except(a.Select(kv => kv.Key)).ToList();
                var extra = a.Select(kv => kv.Key).Except(e.Select(kv => kv.Key)).ToList();
                if (missing.Count + extra.Count > 0) throw new Xunit.Sdk.XunitException($"{path}: missing keys [{string.Join(", ", missing)}], extra keys [{string.Join(", ", extra)}]");
                foreach (var kv in e) AssertSame(kv.Value, a[kv.Key], $"{path}.{kv.Key}");
                break;
            case JsonArray e:
                var b = actual as JsonArray ?? throw new Xunit.Sdk.XunitException($"{path}: expected an array, got {actual?.ToJsonString() ?? "null"}");
                if (e.Count != b.Count) throw new Xunit.Sdk.XunitException($"{path}: expected {e.Count} items, got {b.Count}. Actual: {Short(b)}");
                for (var i = 0; i < e.Count; i++) AssertSame(e[i], b[i], $"{path}[{i}]");
                break;
            case null:
                if (actual is not null) throw new Xunit.Sdk.XunitException($"{path}: expected null, got {Short(actual)}");
                break;
            default:
                if (actual is null) throw new Xunit.Sdk.XunitException($"{path}: expected {Short(expected)}, got null");
                if (!SameValue(expected, actual)) throw new Xunit.Sdk.XunitException($"{path}:\n  expected {Short(expected)}\n  actual   {Short(actual)}");
                break;
        }
    }

    private static bool SameValue(JsonNode e, JsonNode a)
    {
        var ek = e.GetValueKind();
        if (ek == JsonValueKind.Number && a.GetValueKind() == JsonValueKind.Number) return e.GetValue<double>() == a.GetValue<double>();
        return JsonNode.DeepEquals(e, a);
    }

    private static string Short(JsonNode n)
    {
        var s = n.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}
