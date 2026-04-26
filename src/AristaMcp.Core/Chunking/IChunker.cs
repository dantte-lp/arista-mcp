namespace AristaMcp.Core.Chunking;

public interface IChunker
{
    ChunkSet Chunk(string documentTitle, IReadOnlyList<Section> sections);
}
