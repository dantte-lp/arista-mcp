using AristaMcp.Core.Retrieval;
using Microsoft.ML.OnnxRuntime;

namespace AristaMcp.Embedding;

// Cross-encoder reranker against BAAI/bge-reranker-base (or any XLM-RoBERTa-
// based cross-encoder exported with the HF default input names). Same shape
// contract as OnnxReranker: takes (query, candidate) pairs, returns one logit
// per pair, higher = more relevant, no sigmoid needed for ranking.
//
// Differences from the BERT path in OnnxReranker:
//   - SentencePiece tokenisation with fairseq-offset id remap
//     (XlmRobertaRerankerTokenizer).
//   - Pair encoding `<s> query </s></s> doc </s>` per HF XLM-R convention
//     (4 special tokens, double `</s>` between segments).
//   - Feeds only `input_ids` + `attention_mask` — no `token_type_ids`. The
//     RoBERTa family doesn't use segment embeddings; BGE's ONNX export
//     reflects this (model has exactly 2 inputs).
public sealed class XlmRobertaOnnxReranker : IReranker
{
    private readonly InferenceSession _session;
    private readonly XlmRobertaRerankerTokenizer _tok;
    private readonly RerankerOptions _opt;
    private bool _disposed;

    public XlmRobertaOnnxReranker(RerankerOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        _opt = opt;

        _tok = new XlmRobertaRerankerTokenizer(opt.VocabPath);

        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
        };
        if (opt.Gpu)
        {
            so.AppendExecutionProvider_CUDA();
        }

        _session = new InferenceSession(opt.ModelPath, so);
    }

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return [];
        }

        var queryIds = _tok.EncodeBare(query);
        // Cap the query so the pair <s> q </s></s> d </s> can never exceed MaxSequenceLength, even for
        // a pathologically long query; reserve at least half the budget for the document.
        var queryBudget = Math.Max(1, (_opt.MaxSequenceLength - 4) / 2);
        if (queryIds.Length > queryBudget)
        {
            queryIds = queryIds[..queryBudget];
        }

        var results = new RerankResult[candidates.Count];

        for (var start = 0; start < candidates.Count; start += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + _opt.BatchSize, candidates.Count);
            var slice = candidates.Skip(start).Take(end - start).ToList();
            var scores = await Task.Run(() => ScoreBatch(queryIds, slice), ct).ConfigureAwait(false);
            for (var i = 0; i < scores.Length; i++)
            {
                results[start + i] = new RerankResult(slice[i].ChunkId, scores[i]);
            }
        }

        return results;
    }

    private float[] ScoreBatch(int[] queryIds, List<RerankCandidate> batch)
    {
        var maxSeq = _opt.MaxSequenceLength;
        // XLM-R pair: <s> q </s></s> d </s> — 4 special tokens total.
        var specialTokens = 4;
        var docBudget = Math.Max(1, maxSeq - queryIds.Length - specialTokens);

        var encoded = new List<int[]>(batch.Count);
        var maxLen = 0;
        foreach (var cand in batch)
        {
            var docIds = _tok.EncodeBare(cand.Text);
            if (docIds.Length > docBudget)
            {
                docIds = docIds[..docBudget];
            }

            var seq = new int[1 + queryIds.Length + 2 + docIds.Length + 1];
            var idx = 0;
            seq[idx++] = XlmRobertaRerankerTokenizer.BosTokenId;      // <s>
            Array.Copy(queryIds, 0, seq, idx, queryIds.Length);
            idx += queryIds.Length;
            seq[idx++] = XlmRobertaRerankerTokenizer.EosTokenId;      // </s>
            seq[idx++] = XlmRobertaRerankerTokenizer.EosTokenId;      // </s> (segment separator)
            Array.Copy(docIds, 0, seq, idx, docIds.Length);
            idx += docIds.Length;
            seq[idx] = XlmRobertaRerankerTokenizer.EosTokenId;        // </s>

            encoded.Add(seq);
            maxLen = Math.Max(maxLen, seq.Length);
        }

        var seqLen = Math.Max(maxLen, 1);
        var input = new long[batch.Count * seqLen];
        var mask = new long[batch.Count * seqLen];

        for (var b = 0; b < batch.Count; b++)
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
                    input[rowStart + i] = XlmRobertaRerankerTokenizer.PadTokenId;
                    mask[rowStart + i] = 0;
                }
            }
        }

        long[] shape = [batch.Count, seqLen];
        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);

        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
        };

        using var runOpts = new RunOptions();
        using var results = _session.Run(runOpts, feeds, ["logits"]);
        var logits = results[0].GetTensorDataAsSpan<float>();

        // Contract: output is [B, 1] — exactly one score per pair. A model with a multi-logit
        // classification head ([B, C>1]) would otherwise be read column-major and score every pair
        // wrong *silently*; fail loudly instead.
        if (logits.Length != batch.Count)
        {
            throw new InvalidOperationException(
                $"reranker produced {logits.Length} logits for {batch.Count} pairs; expected a single " +
                "[B,1] score per pair (the model has an incompatible classification head).");
        }

        // Output shape [B, 1] — one raw score per pair. Higher = more relevant.
        var scores = new float[batch.Count];
        for (var b = 0; b < batch.Count; b++)
        {
            scores[b] = logits[b];
        }

        return scores;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}
