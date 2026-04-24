namespace AristaMcp.Core.Settings;

public static class RerankerFamilyDetector
{
    public static RerankerTokenizerFamily Detect(string modelsDir)
    {
        if (!File.Exists(ModelPaths.RerankerModel(modelsDir)))
        {
            return RerankerTokenizerFamily.Missing;
        }

        if (File.Exists(ModelPaths.RerankerSpm(modelsDir)))
        {
            return RerankerTokenizerFamily.XlmRobertaSentencePiece;
        }

        if (File.Exists(ModelPaths.RerankerVocab(modelsDir)))
        {
            return RerankerTokenizerFamily.BertWordPiece;
        }

        return RerankerTokenizerFamily.Missing;
    }
}
