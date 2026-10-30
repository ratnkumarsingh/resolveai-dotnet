using System.Text.RegularExpressions;

namespace ResolveAI.App.Pipeline;

/// <summary>
/// Redacts secrets and obvious personal identifiers before text is sent to the model.
/// Raw rows are never modified; this runs when the evidence packet is built.
/// </summary>
public static class Sanitizer
{
    // ECMAScript mode keeps \w, \d, \s and \b ASCII-only, exactly as these patterns were written and tested.
    private const RegexOptions Ecma = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    private const RegexOptions EcmaI = Ecma | RegexOptions.IgnoreCase;

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // Credentials embedded in URLs: scheme://user:pass@host
        (new Regex(@"(\b[a-z][a-z0-9+.-]*:\/\/)[^\s/@]+@", EcmaI), "$1[REDACTED]@"),
        // Authorization headers
        (new Regex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", Ecma), "$1 [REDACTED]"),
        // Provider-style API keys
        (new Regex(@"\b(?:sk|pk|rk)_(?:live|test)_[A-Za-z0-9]{8,}", Ecma), "[REDACTED_KEY]"),
        (new Regex(@"\bsk-ant-[A-Za-z0-9_-]{8,}", Ecma), "[REDACTED_KEY]"),
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", Ecma), "[REDACTED_KEY]"),
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Ecma), "[REDACTED_KEY]"),
        // key=value / key: value secrets
        (new Regex(@"\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s,;&@]+)", EcmaI), "$1$2[REDACTED]"),
        // Email addresses
        (new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", Ecma), "[EMAIL]"),
        // Card-like numbers (13–19 digits, optional separators)
        (new Regex(@"\b(?:\d[ -]?){12,18}\d\b", Ecma), "[CARD]"),
    ];

    public static (string Text, int Redactions) Sanitize(string input)
    {
        var text = input;
        var redactions = 0;
        foreach (var (pattern, replacement) in Rules)
        {
            text = pattern.Replace(text, m =>
            {
                redactions++;
                return m.Result(replacement);
            });
        }
        return (text, redactions);
    }
}
