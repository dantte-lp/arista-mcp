namespace AristaMcp.Core.Chunking;

// Result of a section-aware chunk pass. Parents come without
// ParentIndex; each leaf carries ParentIndex pointing into Parents,
// resolved to a real FK at insert time once parents have been written
// and DB ids are known.
public sealed record ChunkSet(
    IReadOnlyList<ChunkDraft> Parents,
    IReadOnlyList<ChunkDraft> Leaves);
