using System.IO;
using Microsoft.ML.Tokenizers;

namespace AristaMcp.Embedding;

// Wraps Microsoft.ML.Tokenizers.BertTokenizer (WordPiece) with a batch API that pads to
// the longest sequence in the batch and produces an attention mask. BertTokenizer in
// 2.0.0 only exposes per-string EncodeToIds and cannot load tokenizer.json, so we pass
// a vocab.txt (WordPiece). Callers drive batching + padding themselves.
public sealed class BertWordPieceTokenizer
{
    private readonly BertTokenizer _tok;

    public int PaddingTokenId => _tok.PaddingTokenId;

    public int ClassificationTokenId => _tok.ClassificationTokenId;

    public int SeparatorTokenId => _tok.SeparatorTokenId;

    public BertWordPieceTokenizer(string vocabPath)
    {
        using var fs = File.OpenRead(vocabPath);
        _tok = BertTokenizer.Create(fs, new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
        });
    }

    // Encodes a batch of strings to (inputIds, attentionMask, seqLen). Arrays are
    // flattened [batch * seqLen] — downstream ONNX inputs want this exact layout.
    public (long[] InputIds, long[] AttentionMask, int SeqLen) EncodeBatch(
        IReadOnlyList<string> texts,
        int maxSeqLen)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var encoded = new List<int[]>(texts.Count);
        var maxLen = 0;

        foreach (var t in texts)
        {
            var ids = _tok.EncodeToIds(t, addSpecialTokens: true, considerPreTokenization: true).ToArray();
            if (ids.Length > maxSeqLen)
            {
                ids = ids[..maxSeqLen];
            }

            encoded.Add(ids);
            maxLen = Math.Max(maxLen, ids.Length);
        }

        var seqLen = Math.Max(maxLen, 1);
        var input = new long[texts.Count * seqLen];
        var mask = new long[texts.Count * seqLen];

        for (var b = 0; b < texts.Count; b++)
        {
            var ids = encoded[b];
            var rowStart = b * seqLen;
            for (var i = 0; i < seqLen; i++)
            {
                if (i < ids.Length)
                {
                    input[rowStart + i] = ids[i];
                    mask[rowStart + i] = 1;
                }
                else
                {
                    input[rowStart + i] = _tok.PaddingTokenId;
                    mask[rowStart + i] = 0;
                }
            }
        }

        return (input, mask, seqLen);
    }
}
