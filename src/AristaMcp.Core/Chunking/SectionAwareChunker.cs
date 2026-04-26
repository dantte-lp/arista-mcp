namespace AristaMcp.Core.Chunking;

// Section-boundary-aware splitter producing parent + leaf drafts (Sprint 15).
//
// For each input section, the chunker emits:
//
//   - exactly one PARENT draft holding the full section body, truncated
//     to ParentHeadTokens plus tail when the section blows past
//     ParentMaxTokens. Parent rows are never embedded; they exist solely
//     as hydration material for the cross-encoder reranker which scores
//     query/parent pairs at retrieval time.
//
//   - one or more LEAF drafts using the legacy splitting rules. When the
//     section is at or below MaxTokens we emit a single leaf, with the
//     tiny-neighbour merger collapsing fragments shorter than MinTokens
//     into the predecessor. When the section exceeds MaxTokens we run
//     a word-wise sliding window of TargetTokens with OverlapTokens; the
//     merger does not span this boundary. Each leaf carries ParentIndex
//     pointing into the parents collection.
//
// Token counts are approximated from whitespace-split word counts × 1.3
// (BERT-ish ratio). Exact tokenisation happens at embed time.
public sealed class SectionAwareChunker(ChunkingOptions opt) : IChunker
{
    private const float WordsToTokens = 1.3f;

    private readonly ChunkingOptions _opt = opt ?? throw new ArgumentNullException(nameof(opt));

    public ChunkSet Chunk(string documentTitle, IReadOnlyList<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(documentTitle);
        ArgumentNullException.ThrowIfNull(sections);

        var parents = new List<ChunkDraft>();
        var leaves = new List<ChunkDraft>();

        foreach (var section in sections)
        {
            var body = (section.Content ?? "").Trim();
            if (body.Length == 0)
            {
                continue;
            }

            // 1. Parent — full section body, truncated head+tail when over budget.
            var parentBody = TruncateForParent(body);
            var parentTokens = ApproxTokens(parentBody.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            var parentIndex = parents.Count;
            parents.Add(BuildDraft(documentTitle, section, parentBody, parentTokens, ChunkKind.Parent, parentIndex: null));

            // 2. Leaves — same logic as the pre-Sprint-15 chunker, with the
            //    parent index threaded through so the ingest step can patch
            //    parent_chunk_id to a real DB id after parents land.
            var sectionLeavesStart = leaves.Count;
            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var approxTokens = ApproxTokens(words.Length);

            if (approxTokens <= _opt.MaxTokens)
            {
                leaves.Add(BuildDraft(documentTitle, section, body, approxTokens, ChunkKind.Leaf, parentIndex));
            }
            else
            {
                SplitOversized(documentTitle, section, words, leaves, parentIndex);
            }

            // Tiny-neighbour merge runs per-section so it never merges across
            // parent boundaries.
            MergeTinyNeighboursInPlace(leaves, sectionLeavesStart);
        }

        return new ChunkSet(parents, leaves);
    }

    private string TruncateForParent(string body)
    {
        var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var approxTokens = ApproxTokens(words.Length);
        if (approxTokens <= _opt.ParentMaxTokens)
        {
            return body;
        }

        var headWords = (int)(_opt.ParentHeadTokens / WordsToTokens);
        var tailWords = (int)(_opt.ParentTailTokens / WordsToTokens);

        if (headWords + tailWords >= words.Length)
        {
            // Section just barely over the budget — keep the whole thing
            // rather than over-engineer the truncation.
            return body;
        }

        var head = string.Join(' ', words, 0, headWords);
        var tail = string.Join(' ', words, words.Length - tailWords, tailWords);
        return $"{head}\n\n[…section continues…]\n\n{tail}";
    }

    private void SplitOversized(
        string documentTitle, Section section, string[] words,
        List<ChunkDraft> leaves, int parentIndex)
    {
        var targetWords = (int)(_opt.TargetTokens / WordsToTokens);
        var overlapWords = (int)(_opt.OverlapTokens / WordsToTokens);
        var step = Math.Max(1, targetWords - overlapWords);

        for (var start = 0; start < words.Length; start += step)
        {
            var end = Math.Min(words.Length, start + targetWords);
            var slice = string.Join(' ', words, start, end - start);
            var tokens = ApproxTokens(end - start);
            leaves.Add(BuildDraft(documentTitle, section, slice, tokens, ChunkKind.Leaf, parentIndex));

            if (end == words.Length)
            {
                break;
            }
        }
    }

    private static ChunkDraft BuildDraft(
        string documentTitle, Section section, string body, int tokens,
        ChunkKind kind, int? parentIndex)
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
            Kind = kind,
            ParentIndex = parentIndex,
        };
    }

    // Tiny-neighbour merge confined to a single section's leaves. Operates
    // in-place on the slice [start, leaves.Count) so the merger never
    // crosses parent boundaries.
    private void MergeTinyNeighboursInPlace(List<ChunkDraft> leaves, int start)
    {
        if (leaves.Count - start <= 1)
        {
            return;
        }

        var merged = new List<ChunkDraft>(leaves.Count - start) { leaves[start] };

        for (var i = start + 1; i < leaves.Count; i++)
        {
            var cur = leaves[i];
            var prev = merged[^1];

            var sameSection = string.Equals(cur.SectionTitle, prev.SectionTitle, StringComparison.Ordinal);
            if (cur.TokenCount < _opt.MinTokens && sameSection)
            {
                merged[^1] = prev with
                {
                    Content = prev.Content + "\n\n" + cur.RawContent,
                    RawContent = $"{prev.RawContent}\n\n{cur.RawContent}",
                    TokenCount = prev.TokenCount + cur.TokenCount,
                };
            }
            else
            {
                merged.Add(cur);
            }
        }

        leaves.RemoveRange(start, leaves.Count - start);
        leaves.AddRange(merged);
    }

    private static int ApproxTokens(int words) => (int)Math.Ceiling(words * WordsToTokens);
}
