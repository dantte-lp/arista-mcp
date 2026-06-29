namespace AristaMcp.Core.Settings;

public static class RerankerFamilyDetector
{
    // Looks at the on-disk reranker assets and picks the tokenizer family.
    // The caller is responsible for passing the EFFECTIVE reranker directory —
    // typically `ModelPaths.RerankerDir(settings)` so the
    // `ARISTA_MCP__RerankerDir` override is honoured.
    public static RerankerTokenizerFamily Detect(string rerankerDir)
    {
        if (!File.Exists(ModelPaths.RerankerModel(rerankerDir)))
        {
            return RerankerTokenizerFamily.Missing;
        }

        if (File.Exists(ModelPaths.RerankerSpm(rerankerDir)))
        {
            return RerankerTokenizerFamily.XlmRobertaSentencePiece;
        }

        if (File.Exists(ModelPaths.RerankerVocab(rerankerDir)))
        {
            return RerankerTokenizerFamily.BertWordPiece;
        }

        return RerankerTokenizerFamily.Missing;
    }
}
