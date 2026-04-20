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
    @{
        Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/onnx/model.onnx'
        Dest = Join-Path $ModelsRoot 'reranker\model.onnx'
        MinBytes = 80MB
    },
    @{
        Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/vocab.txt'
        Dest = Join-Path $ModelsRoot 'reranker\vocab.txt'
        MinBytes = 100KB
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
