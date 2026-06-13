using System;

namespace CelMap.Core;

/// <summary>
/// Centralizes string normalization algorithms used for header matching and synonyms comparisons.
/// </summary>
public static class HeaderNormalizer
{
    /// <summary>
    /// Token-preserving form for fuzzy scoring and concept checks: lower-cased, newlines folded to
    /// spaces, trimmed — but internal spaces kept so TokenSetRatio can match on shared words.
    /// </summary>
    public static string NormalizeLoose(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim().ToLowerInvariant();

    /// <summary>
    /// Whitespace-INSENSITIVE form for the exact check: lower-cased, ALL whitespace
    /// removed, so "Member ID", "MemberID" and "member  id" collapse to one token. Punctuation
    /// kept as-is (aliases handle e.g. "D.O.B.").
    /// </summary>
    public static string NormalizeTight(string s)
    {
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c))
                buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer[..n]);
    }
}
