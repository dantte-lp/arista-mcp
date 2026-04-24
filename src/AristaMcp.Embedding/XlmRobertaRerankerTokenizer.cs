using System.IO;
using Microsoft.ML.Tokenizers;

namespace AristaMcp.Embedding;

// XLM-RoBERTa / bge-reranker-* tokenizer. Loads `sentencepiece.bpe.model` via
// Microsoft.ML.Tokenizers.SentencePieceTokenizer and remaps native SP ids onto
// HuggingFace's XLM-R id space (fairseq alignment):
//
//   SP id  | HF id | token
//   -------|-------|--------
//     0    |  3    | <unk>      (`<unk>` is sp 0, but HF puts unk at 3)
//     1    |  -    | <s>        (sp has <s> at 1, HF uses 0 via specials table)
//     2    |  -    | </s>       (sp has </s> at 2, HF uses 2 via specials table)
//    >=3   | sp+1  | real piece (fairseq_offset = 1)
//
// For ordinary text encoding, `sp_model.PieceToId` returns 0 for unknown and
// >=3 for real pieces — sp ids 1/2 only appear for the literal `<s>` / `</s>`
// strings, which HF short-circuits through a specials dict. So the uniform
// remap `sp == 0 ? UnkTokenId : sp + 1` matches HF `_convert_token_to_id`
// output byte-for-byte on real user input.
public sealed class XlmRobertaRerankerTokenizer
{
    // HF XLM-R special-token ids (fairseq_tokens_to_ids). These are fixed by
    // the model family, not by the SPM file — don't try to derive them from
    // SpmTokenizer.BeginningOfSentenceId etc. (which would return the SP ids
    // 1/2, not the HF ids 0/2).
    public const int BosTokenId = 0;   // <s>
    public const int PadTokenId = 1;   // <pad>
    public const int EosTokenId = 2;   // </s>
    public const int UnkTokenId = 3;   // <unk>

    private const int FairseqOffset = 1;

    private readonly SentencePieceTokenizer _tok;

    public XlmRobertaRerankerTokenizer(string spmPath)
    {
        using var fs = File.OpenRead(spmPath);
        _tok = SentencePieceTokenizer.Create(
            fs,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            specialTokens: new Dictionary<string, int>(0, StringComparer.Ordinal));
    }

    // Encodes text as raw (no BOS/EOS). Caller assembles the pair sequence
    // `<s> query </s></s> doc </s>` itself.
    public int[] EncodeBare(string text)
    {
        var spIds = _tok.EncodeToIds(
            text,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            considerPreTokenization: true,
            considerNormalization: true);

        var result = new int[spIds.Count];
        for (var i = 0; i < spIds.Count; i++)
        {
            var sp = spIds[i];
            result[i] = sp == 0 ? UnkTokenId : sp + FairseqOffset;
        }

        return result;
    }
}
