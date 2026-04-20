namespace AristaMcp.Core.Chunking;

// Section-boundary-aware splitter. Each section maps to one or more ChunkDrafts:
//   • sections <= MaxTokens → single chunk, merged with tiny neighbours if < MinTokens
//   • sections > MaxTokens → word-wise sliding window of TargetTokens with OverlapTokens
// Token counts are approximated from whitespace-split word counts × 1.3 (BERT-ish ratio).
// Exact tokenization happens at embed time; this heuristic just decides split points.
public sealed class SectionAwareChunker(ChunkingOptions opt) : IChunker
{
    private const float WordsToTokens = 1.3f;

    private readonly ChunkingOptions _opt = opt ?? throw new ArgumentNullException(nameof(opt));

    public IReadOnlyList<ChunkDraft> Chunk(string documentTitle, IReadOnlyList<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(documentTitle);
        ArgumentNullException.ThrowIfNull(sections);

        var drafts = new List<ChunkDraft>();
        foreach (var section in sections)
        {
            var body = (section.Content ?? "").Trim();
            if (body.Length == 0)
            {
                continue;
            }

            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var approxTokens = ApproxTokens(words.Length);

            if (approxTokens <= _opt.MaxTokens)
            {
                drafts.Add(BuildDraft(documentTitle, section, body, approxTokens));
            }
            else
            {
                SplitOversized(documentTitle, section, words, drafts);
            }
        }

        return MergeTinyNeighbours(drafts);
    }

    private void SplitOversized(string documentTitle, Section section, string[] words, List<ChunkDraft> drafts)
    {
        var targetWords = (int)(_opt.TargetTokens / WordsToTokens);
        var overlapWords = (int)(_opt.OverlapTokens / WordsToTokens);
        var step = Math.Max(1, targetWords - overlapWords);

        for (var start = 0; start < words.Length; start += step)
        {
            var end = Math.Min(words.Length, start + targetWords);
            var slice = string.Join(' ', words, start, end - start);
            var tokens = ApproxTokens(end - start);
            drafts.Add(BuildDraft(documentTitle, section, slice, tokens));

            if (end == words.Length)
            {
                break;
            }
        }
    }

    private static ChunkDraft BuildDraft(string documentTitle, Section section, string body, int tokens)
    {
        var prefixed = $"{documentTitle} > {section.Title}\n\n{body}";
        return new ChunkDraft
        {
            Content = prefixed,
            RawContent = body,
            SectionTitle = section.Title,
            SectionLevel = section.Level,
            PageStart = section.PageStart,
            PageEnd = section.PageEnd,
            TokenCount = tokens,
        };
    }

    // Merges a chunk < MinTokens into its predecessor when both belong to the same section
    // or to siblings under the same parent (level 1 boundary). Keeps chunks standalone
    // otherwise — a 30-token chunk that stands alone is still useful for retrieval.
    private List<ChunkDraft> MergeTinyNeighbours(List<ChunkDraft> drafts)
    {
        if (drafts.Count <= 1)
        {
            return drafts;
        }

        var merged = new List<ChunkDraft>(drafts.Count) { drafts[0] };

        for (var i = 1; i < drafts.Count; i++)
        {
            var cur = drafts[i];
            var prev = merged[^1];

            var sameSection = string.Equals(cur.SectionTitle, prev.SectionTitle, StringComparison.Ordinal);
            if (cur.TokenCount < _opt.MinTokens && sameSection)
            {
                var combinedBody = $"{prev.RawContent}\n\n{cur.RawContent}";
                var combinedTokens = prev.TokenCount + cur.TokenCount;
                var combinedContent = prev.Content + "\n\n" + cur.RawContent;
                merged[^1] = prev with
                {
                    Content = combinedContent,
                    RawContent = combinedBody,
                    TokenCount = combinedTokens,
                };
            }
            else
            {
                merged.Add(cur);
            }
        }

        return merged;
    }

    private static int ApproxTokens(int words) => (int)Math.Ceiling(words * WordsToTokens);
}
