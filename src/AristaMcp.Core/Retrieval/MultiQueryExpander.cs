using System.Text;
using System.Text.RegularExpressions;

namespace AristaMcp.Core.Retrieval;

// Rule-based multi-query expansion for the dense path. Three transforms,
// each of which produces 0 or 1 extra variant:
//
//   R1 — acronym contraction. QueryExpander injects annotations of the
//        shape `ACRONYM (Full Phrase)`. Stripping every `(...)` block
//        yields a leaner variant that's closer to user-typed intent and
//        helps when chunks reference the bare acronym (release notes,
//        config snippets) rather than the documented full form.
//
//   R2 — question-prefix strip. "How do I configure MLAG peer-link" →
//        "configure MLAG peer-link", or further "MLAG peer-link" once the
//        leading verb is also stripped. Documents lead with noun phrases
//        ("MLAG peer-link configuration"), not interrogative phrasing,
//        so question-form queries miss those chunks in dense retrieval.
//
//   R3 — none (reserved). Product-slug injection from the v0.3 plan
//        needs a product hint that QueryExpander doesn't currently
//        produce; deferred until QueryExpander grows that resolution.
//
// All transforms preserve the canonical expanded query as variant[0].
// Output list is de-duplicated by ordinal string equality.
public sealed partial class MultiQueryExpander : IMultiQueryExpander
{
    // Strip everything in parens — including spaces immediately around
    // the closing paren, which would otherwise leave double spaces.
    [GeneratedRegex(@"\s*\([^)]*\)", RegexOptions.None)]
    private static partial Regex AnnotationParenRegex();

    // Match repeated whitespace for post-strip clean-up.
    [GeneratedRegex(@"\s{2,}", RegexOptions.None)]
    private static partial Regex CollapseWhitespaceRegex();

    // Lower-cased prefixes commonly leading user queries that carry no
    // retrieval signal. Order matters — longer prefixes first so
    // "how do i " strips before the more permissive "how ".
    private static readonly string[] QuestionPrefixes =
    [
        "how do i ",
        "how do you ",
        "how to ",
        "how can i ",
        "how can you ",
        "what is the ",
        "what are the ",
        "what is ",
        "what are ",
        "what does ",
        "what do ",
        "where is ",
        "where are ",
        "show me how to ",
        "show me ",
        "tell me about ",
        "tell me how to ",
        "explain how to ",
        "explain ",
        "describe ",
        // Imperative verbs at the head of a query — also low-signal.
        "configure ",
        "setup ",
        "set up ",
        "enable ",
    ];

    public IReadOnlyList<string> Expand(string expandedQuery)
    {
        ArgumentNullException.ThrowIfNull(expandedQuery);

        // Build with a small builder list; final dedupe at the end
        // preserves the order variants were generated.
        var variants = new List<string>(4) { expandedQuery };

        // R1 — acronym contraction.
        var contracted = ContractAnnotations(expandedQuery);
        AppendIfNew(variants, contracted);

        // R2 — question-prefix strip, applied iteratively so a chain like
        // "how do I configure ..." → "configure ..." → "..." emits the
        // useful intermediate AND the final noun-phrase variant.
        var basis = string.IsNullOrEmpty(contracted) ? expandedQuery : contracted;
        var current = basis;
        for (var pass = 0; pass < 3; pass++)
        {
            var stripped = StripQuestionPrefix(current);
            if (string.Equals(stripped, current, StringComparison.Ordinal))
            {
                break;
            }
            AppendIfNew(variants, stripped);
            current = stripped;
        }

        return [.. variants.Distinct(StringComparer.Ordinal)];
    }

    private static string ContractAnnotations(string query)
    {
        if (query.IndexOf('(') < 0)
        {
            return query;
        }

        var stripped = AnnotationParenRegex().Replace(query, string.Empty);
        stripped = CollapseWhitespaceRegex().Replace(stripped, " ").Trim();
        return stripped;
    }

    private static string StripQuestionPrefix(string query)
    {
        var lowered = query.AsSpan().TrimStart();
        var trimmedHeadOffset = query.Length - lowered.Length;
        var headLower = lowered.ToString().ToLowerInvariant();

        var matched = QuestionPrefixes.FirstOrDefault(
            p => headLower.StartsWith(p, StringComparison.Ordinal));
        if (matched is null)
        {
            return query;
        }

        var afterPrefix = trimmedHeadOffset + matched.Length;
        if (afterPrefix >= query.Length)
        {
            return query;
        }

        var tail = query[afterPrefix..].Trim();
        return tail.Length >= 3 ? tail : query;
    }

    private static void AppendIfNew(List<string> variants, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (variants.Any(existing => string.Equals(existing, candidate, StringComparison.Ordinal)))
        {
            return;
        }
        variants.Add(candidate);
    }
}
