using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace AristaMcp.Embedding;

// Runs snowflake-arctic-embed-m-v1.5 (BERT-base arch) via ONNX Runtime.
// Inputs: input_ids, attention_mask, token_type_ids (all int64 [B, L]).
// Output: last_hidden_state [B, L, 768] — mean-pool with attention mask then L2-normalize.
public sealed class OnnxEmbedder : IEmbedder
{
    private const int HiddenSize = 768;

    private readonly InferenceSession _session;
    private readonly BertWordPieceTokenizer _tok;
    private readonly EmbeddingOptions _opt;
    private bool _disposed;

    public int Dimension => HiddenSize;

    public OnnxEmbedder(EmbeddingOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);

        _opt = opt;
        _tok = new BertWordPieceTokenizer(opt.VocabPath);

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

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        var prepped = isQuery
            ? texts.Select(t => _opt.QueryPrefix + t).ToList()
            : [.. texts];

        var results = new float[texts.Count][];

        for (var start = 0; start < prepped.Count; start += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + _opt.BatchSize, prepped.Count);
            var batch = prepped.GetRange(start, end - start);

            var embedded = await Task.Run(() => EmbedBatch(batch), ct).ConfigureAwait(false);
            for (var i = 0; i < embedded.Length; i++)
            {
                results[start + i] = embedded[i];
            }
        }

        return results;
    }

    private float[][] EmbedBatch(List<string> batch)
    {
        var (input, mask, seqLen) = _tok.EncodeBatch(batch, _opt.MaxSequenceLength);
        long[] shape = [batch.Count, seqLen];

        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);

        // Arctic Embed's ONNX export exposes input_ids + attention_mask as inputs and
        // token_embeddings + sentence_embedding ([B, 768]) as outputs. The pre-pooled
        // sentence_embedding matches the Transformers default mean-pool + L2-norm.
        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
        };

        using var runOpts = new RunOptions();
        using var results = _session.Run(runOpts, feeds, ["sentence_embedding"]);
        var sentence = results[0].GetTensorDataAsSpan<float>();

        var outputs = new float[batch.Count][];
        for (var b = 0; b < batch.Count; b++)
        {
            outputs[b] = ExtractAndNormalize(sentence, b);
        }

        return outputs;
    }

    // Defensive re-normalize — the export should already emit unit vectors, but this
    // protects retrieval quality if a future export forgets the normalize layer.
    // TensorPrimitives.Norm + Divide are SIMD-vectorized on .NET 10.
    private static float[] ExtractAndNormalize(ReadOnlySpan<float> sentence, int batchIdx)
    {
        var vec = new float[HiddenSize];
        sentence.Slice(batchIdx * HiddenSize, HiddenSize).CopyTo(vec);

        var norm = TensorPrimitives.Norm<float>(vec);
        if (norm > 1e-9f)
        {
            TensorPrimitives.Divide(vec, norm, vec);
        }

        return vec;
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
