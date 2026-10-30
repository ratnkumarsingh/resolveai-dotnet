using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResolveAI.Contracts;

public static class Json
{
    /// <summary>
    /// snake_case, with relaxed escaping so text lengths match what the size budgets were tuned for:
    /// the default encoder would turn "…", "×" or "→" into six-character escapes.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new LooseObjectConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>JSON.stringify(value, null, 1): the formatting the model has always been sent.</summary>
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true, IndentSize = 1, NewLine = "\n" };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializePretty<T>(T value) => JsonSerializer.Serialize(value, Pretty);

    /// <summary>Parses a stored JSON column, returning the fallback when it is missing or malformed.</summary>
    public static T Parse<T>(string? value, T fallback)
    {
        if (value is null) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(value, Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Reads untyped values (tool rows, tool arguments, trace details) as plain .NET values rather than JsonElement,
    /// so code can treat them like the dynamic objects they are.
    /// </summary>
    private sealed class LooseObjectConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String: return reader.GetString();
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.Null: return null;
                case JsonTokenType.Number:
                    // The cast matters: without it the conditional would widen every whole number to double.
                    return reader.TryGetInt64(out var whole) ? (object)whole : reader.GetDouble();
                case JsonTokenType.StartArray:
                    var list = new List<object?>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) list.Add(Read(ref reader, typeof(object), options));
                    return list;
                case JsonTokenType.StartObject:
                    var map = new Dictionary<string, object?>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var key = reader.GetString()!;
                        reader.Read();
                        map[key] = Read(ref reader, typeof(object), options);
                    }
                    return map;
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}

/// <summary>JavaScript behaviours the rest of the code relies on, kept in one place so they stay exact.</summary>
public static class Js
{
    /// <summary>Date.prototype.toISOString: always three decimals and Z. Stored times are compared as strings.</summary>
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string IsoNow() => Iso(DateTimeOffset.UtcNow);

    /// <summary>ISO time without milliseconds, the form used for evidence windows.</summary>
    public static string IsoSeconds(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static bool TryParseDate(string? s, out DateTimeOffset value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    public static DateTimeOffset ParseDate(string s) =>
        TryParseDate(s, out var value) ? value : throw new FormatException($"Not a date: {s}");

    /// <summary>String(number): whole numbers without a decimal point, otherwise shortest round-trip.</summary>
    public static string Num(double n) => n.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>String(value) for the plain values that appear in tool arguments and rows.</summary>
    public static string Str(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => Num(d),
        float f => Num(f),
        decimal m => Num((double)m),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Math.round: halves go up, including for negative numbers.</summary>
    public static double Round(double n) => Math.Floor(n + 0.5);

    /// <summary>A string slice by UTF-16 units, like String.prototype.slice for a non-negative range.</summary>
    public static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
