namespace AristaMcp.Core.Chunking;

// Sprint 15: parent-child chunking. Leaf = the 512-token slice indexed by
// the embedder for dense retrieval; Parent = the full surrounding section
// passed to the cross-encoder reranker for context-rich tie-breaking.
// Parents are never embedded — they exist only as hydration material.
public enum ChunkKind
{
    Leaf,
    Parent,
}
