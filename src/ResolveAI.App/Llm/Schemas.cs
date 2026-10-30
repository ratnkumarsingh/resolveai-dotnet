using System.Text.Json;
using ResolveAI.Contracts;

namespace ResolveAI.App.Llm;

/// <summary>
/// The structured-output schemas sent to Claude, kept verbatim from the Node implementation (tests compare them). The API
/// doesn't enforce every rule, such as confidence values, so replies are also checked here before they are used.
/// </summary>
public static class Schemas
{
    public const string InvestigationJson = """
        {"$defs":{"__schema0":{"type":"string"}},"type":"object","properties":{"summary":{"type":"string"},"insufficient_evidence":{"type":"boolean"},"hypotheses":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"explanation":{"type":"string"},"confidence":{"type":"string","description":"{enum: [\"low\",\"medium\",\"high\"]}"},"evidence_ids":{"type":"array","description":"IDs copied exactly from the evidence packet","items":{"$ref":"#/$defs/__schema0"}},"counter_evidence":{"type":"array","items":{"type":"string"}},"unknowns":{"type":"array","items":{"type":"string"}},"recommended_checks":{"type":"array","items":{"type":"object","properties":{"step":{"type":"string","description":"A plain-language, read-only check an engineer can perform"},"why":{"type":"string","description":"Why this check discriminates between hypotheses"},"expected_signal":{"type":"string","description":"What the engineer should see if the hypothesis is right"}},"additionalProperties":false,"required":["step","why","expected_signal"]}}},"additionalProperties":false,"required":["title","explanation","confidence","evidence_ids","counter_evidence","unknowns","recommended_checks"]}},"similar_incidents":{"type":"array","items":{"type":"object","properties":{"incident_id":{"type":"string","description":"A hist: ID copied from the candidate list"},"similarity_reason":{"type":"string"}},"additionalProperties":false,"required":["incident_id","similarity_reason"]}},"limitations":{"type":"array","items":{"type":"string"}}},"additionalProperties":false,"required":["summary","insufficient_evidence","hypotheses","similar_incidents","limitations"],"description":"{$schema: \"https://json-schema.org/draft/2020-12/schema\"}"}
        """;

    public const string RcaJson = """
        {"type":"object","properties":{"impact":{"type":"string"},"timeline":{"type":"array","items":{"type":"object","properties":{"timestamp":{"type":"string","description":"ISO-8601"},"event":{"type":"string"},"evidence_ids":{"type":"array","items":{"type":"string"}}},"additionalProperties":false,"required":["timestamp","event","evidence_ids"]}},"root_cause":{"type":"string","description":"Suspected root cause, phrased as unconfirmed unless feedback verified it"},"contributing_factors":{"type":"array","items":{"type":"string"}},"resolution_steps":{"type":"array","items":{"type":"string"}},"prevention_actions":{"type":"array","items":{"type":"string"}}},"additionalProperties":false,"required":["impact","timeline","root_cause","contributing_factors","resolution_steps","prevention_actions"],"description":"{$schema: \"https://json-schema.org/draft/2020-12/schema\"}"}
        """;

    public static InvestigationOutput ParseInvestigation(string text)
    {
        var output = Parse<InvestigationOutput>(text, InvestigationJson);
        if (output.Hypotheses.Any(h => h.Confidence is not ("low" or "medium" or "high"))) throw new JsonException("confidence must be low, medium or high.");
        return output;
    }

    public static RcaOutput ParseRca(string text) => Parse<RcaOutput>(text, RcaJson);

    private static T Parse<T>(string text, string schemaJson)
    {
        using var reply = JsonDocument.Parse(text);
        using var schema = JsonDocument.Parse(schemaJson);
        Check(reply.RootElement, schema.RootElement, schema.RootElement, "$");
        return JsonSerializer.Deserialize<T>(text, Json.Options) ?? throw new JsonException("Empty reply.");
    }

    /// <summary>Checks the parts of JSON schema these two schemas use: types, required properties, array items and $ref.</summary>
    private static void Check(JsonElement value, JsonElement schema, JsonElement root, string path)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            schema = root.GetProperty("$defs").GetProperty(reference.GetString()!["#/$defs/".Length..]);
        }
        var type = schema.GetProperty("type").GetString();
        var ok = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false,
        };
        if (!ok) throw new JsonException($"{path} must be a {type}.");

        if (type == "array")
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) Check(item, schema.GetProperty("items"), root, $"{path}[{i++}]");
        }
        else if (type == "object")
        {
            foreach (var name in schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out var field)) throw new JsonException($"{path}.{name} is missing.");
                Check(field, schema.GetProperty("properties").GetProperty(name), root, $"{path}.{name}");
            }
        }
    }
}
