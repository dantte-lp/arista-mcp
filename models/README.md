# models/

Local ONNX model cache (git-ignored). Fetch with:

    pwsh scripts/fetch-models.ps1

Expected layout after fetch:

    models/
      embedder/
        model.onnx        (~436 MB, fp32 — snowflake-arctic-embed-m-v1.5)
        vocab.txt         (~232 KB, WordPiece)
      reranker/
        model.onnx        (~91 MB — cross-encoder/ms-marco-MiniLM-L6-v2)
        vocab.txt         (~232 KB, WordPiece — same bert-base-uncased tokens)

Integration tests (OnnxEmbedderTests, OnnxRerankerTests, end-to-end ingest)
skip if the corresponding model file is absent.

To use the fp16 embedder variant instead (~218 MB, small quality hit) swap
the URL in `fetch-models.ps1` to `/onnx/model_fp16.onnx`.
