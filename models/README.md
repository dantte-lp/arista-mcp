# models/

Local ONNX model cache (git-ignored). Fetch with:

    pwsh scripts/fetch-models.ps1

Expected layout after fetch:

    models/
      embedder/
        model.onnx        (~436 MB, fp32 — snowflake-arctic-embed-m-v1.5)
        vocab.txt         (~232 KB, WordPiece)

Integration tests (OnnxEmbedderTests, end-to-end ingest) skip if
`models/embedder/model.onnx` is absent.

To use the fp16 variant instead (~218 MB, small quality hit):

    pwsh scripts/fetch-models.ps1
    # then swap the URL in fetch-models.ps1 to /onnx/model_fp16.onnx

For the eventual reranker (Sprint 3), drop `bge-reranker-base/model.onnx +
vocab.txt` under `models/reranker/`.
