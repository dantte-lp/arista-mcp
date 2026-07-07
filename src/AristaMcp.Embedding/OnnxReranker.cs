using AristaMcp.Core.Retrieval;
using Microsoft.ML.OnnxRuntime;

namespace AristaMcp.Embedding;

// Cross-encoder reranker against cross-encoder/ms-marco-MiniLM-L6-v2 (or any BERT-based
// cross-encoder exported with the same input names). Takes (query, candidate) pairs as
// a single [CLS] query [SEP] doc [SEP] tokenized sequence, returns a single logit per
// pair — higher = more relevant. We return the logit directly (HybridRetriever sorts
// descending); applying sigmoid is unnecessary for ranking.
public sealed class OnnxReranker : IReranker
{
    private readonly InferenceSession _session;
    private readonly BertWordPieceTokenizer _tok;
    private readonly RerankerOptions _opt;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private bool _disposed;

    public OnnxReranker(RerankerOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        _opt = opt;

        _tok = new BertWordPieceTokenizer(opt.VocabPath);
        _clsId = _tok.ClassificationTokenId;
        _sepId = _tok.SeparatorTokenId;
        _padId = _tok.PaddingTokenId;

        using var so = new SessionOptions
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
        // Cap the query so the pair [CLS] q [SEP] d [SEP] can never exceed MaxSequenceLength, even for
        // a pathologically long query; reserve at least half the budget for the document.
        var queryBudget = Math.Max(1, (_opt.MaxSequenceLength - 3) / 2);
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
        // Reserve space for [CLS] + [SEP] + [SEP] = 3 special tokens.
        var specialTokens = 3;
        var docBudget = Math.Max(1, maxSeq - queryIds.Length - specialTokens);

        // Encode each candidate doc; build per-sample token sequences.
        var encoded = new List<int[]>(batch.Count);
        var maxLen = 0;
        foreach (var cand in batch)
        {
            var docIds = _tok.EncodeBare(cand.Text);
            if (docIds.Length > docBudget)
            {
                docIds = docIds[..docBudget];
            }

            var seq = new int[1 + queryIds.Length + 1 + docIds.Length + 1];
            var idx = 0;
            seq[idx++] = _clsId;
            Array.Copy(queryIds, 0, seq, idx, queryIds.Length);
            idx += queryIds.Length;
            seq[idx++] = _sepId;
            Array.Copy(docIds, 0, seq, idx, docIds.Length);
            idx += docIds.Length;
            seq[idx] = _sepId;

            encoded.Add(seq);
            maxLen = Math.Max(maxLen, seq.Length);
        }

        var seqLen = Math.Max(maxLen, 1);
        var input = new long[batch.Count * seqLen];
        var mask = new long[batch.Count * seqLen];
        // BERT pair-encoding convention: segment 0 for [CLS] + query + first [SEP],
        // segment 1 for doc + second [SEP]. Padding keeps segment 0.
        var tokenTypes = new long[batch.Count * seqLen];
        // +2 = [CLS] at position 0 and the first [SEP] after the query.
        var segmentSwitch = queryIds.Length + 2;

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
                    tokenTypes[rowStart + i] = i < segmentSwitch ? 0 : 1;
                }
                else
                {
                    input[rowStart + i] = _padId;
                    mask[rowStart + i] = 0;
                    tokenTypes[rowStart + i] = 0;
                }
            }
        }

        long[] shape = [batch.Count, seqLen];
        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var tttVal = OrtValue.CreateTensorValueFromMemory(tokenTypes, shape);

        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
            ["token_type_ids"] = tttVal,
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
