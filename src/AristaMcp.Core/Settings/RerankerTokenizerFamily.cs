namespace AristaMcp.Core.Settings;

// Tokenizer family detected at model-load time. `models/reranker/` is probed
// for `sentencepiece.bpe.model` first (XLM-R / bge-reranker), then `vocab.txt`
// (BERT WordPiece / MiniLM). Callers fall back to NoopReranker when neither
// is present.
public enum RerankerTokenizerFamily
{
    Missing,
    BertWordPiece,
    XlmRobertaSentencePiece,
}
