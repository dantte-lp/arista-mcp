namespace AristaMcp.Core.Chunking;

public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(string documentTitle, IReadOnlyList<Section> sections);
}
