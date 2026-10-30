using System.Security.Cryptography;
using System.Text;

namespace ResolveAI.Contracts;

/// <summary>
/// Every record handed to the model carries a typed ID so the server can verify a citation and link it back to its source table.
/// </summary>
public static class EvidenceIds
{
    public static readonly IReadOnlyList<string> Kinds = ["log", "dep", "met", "doc", "hist", "tl", "mcp"];

    public static string Id(string kind, string id) => $"{kind}:{id}";

    public static (string Kind, string Id)? Parse(string reference)
    {
        var i = reference.IndexOf(':');
        if (i <= 0) return null;
        var kind = reference[..i];
        return Kinds.Contains(kind) ? (kind, reference[(i + 1)..]) : null;
    }

    /// <summary>IDs derived from stable names, so recorded fixtures can cite them and resets produce identical data.</summary>
    public static string Stable(string name)
    {
        var h = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"resolveai:{name}")));
        return $"{h[..8]}-{h[8..12]}-5{h[13..16]}-a{h[17..20]}-{h[20..32]}";
    }
}
