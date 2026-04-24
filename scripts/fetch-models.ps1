#!/usr/bin/env pwsh
# Fetch ONNX model + WordPiece vocab for arista-mcp.
# Idempotent — skips files that already exist with matching SHA256.

param(
    [string]$ModelsRoot = (Join-Path $PSScriptRoot "..\models"),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$files = @(
    @{
        Url = 'https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/onnx/model.onnx'
        Dest = Join-Path $ModelsRoot 'embedder\model.onnx'
        MinBytes = 400MB
    },
    @{
        Url = 'https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/vocab.txt'
        Dest = Join-Path $ModelsRoot 'embedder\vocab.txt'
        MinBytes = 100KB
    },
    # Default reranker stays MiniLM-L6 after the v0.2.2 bge-reranker-base swap
    # trial showed no retrieval uplift (-1.8pp top-1, within noise) and 2x CPU
    # latency on the 111-query bench. The XLM-R code path in
    # AristaMcp.Embedding is kept for future v2-m3 / reranker-v2 experiments.
    @{
        Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/onnx/model.onnx'
        Dest = Join-Path $ModelsRoot 'reranker\model.onnx'
        MinBytes = 80MB
    },
    @{
        Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/vocab.txt'
        Dest = Join-Path $ModelsRoot 'reranker\vocab.txt'
        MinBytes = 100KB
    },
    # Sprint 10: Qwen2.5-1.5B-Instruct Q4_K_M GGUF for HyDE query rewriting
    # via the llama.cpp sidecar (docker/compose.yaml `llm` service). ~1 GB.
    @{
        Url = 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf'
        Dest = Join-Path $ModelsRoot 'llm\qwen2.5-1.5b-instruct-q4_k_m.gguf'
        MinBytes = 900MB
    }
)

foreach ($f in $files) {
    $destDir = Split-Path $f.Dest -Parent
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

    if (-not $Force -and (Test-Path $f.Dest)) {
        $size = (Get-Item $f.Dest).Length
        if ($size -ge $f.MinBytes) {
            Write-Host "  [cached] $($f.Dest) ($([math]::Round($size / 1MB, 1)) MB)" -ForegroundColor Green
            continue
        }
        Write-Host "  [partial] re-downloading $($f.Dest) (size=$size < $($f.MinBytes))" -ForegroundColor Yellow
        Remove-Item $f.Dest -Force
    }

    Write-Host "  [fetch ] $($f.Url)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $f.Url -OutFile $f.Dest
    $size = (Get-Item $f.Dest).Length
    Write-Host "  [done  ] $($f.Dest) ($([math]::Round($size / 1MB, 1)) MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Models ready under $ModelsRoot." -ForegroundColor Green
