# Build arista-mcp with Microsoft.ML.OnnxRuntime.Gpu instead of the CPU
# package. One-shot use: a full-corpus `arista-mcp ingest` is 5-10× faster on
# GPU than CPU. Production `arista-mcp serve` should stay on CPU — a single
# query embedding is sub-200 ms there and the VRAM saved goes to whatever
# else runs on the machine.
#
# Usage:
#
#   pwsh scripts/build-gpu.ps1              # Debug build
#   pwsh scripts/build-gpu.ps1 -Release     # Release
#   pwsh scripts/build-gpu.ps1 -Clean       # nuke obj/bin first
#
# Then:
#
#   $env:ARISTA_MCP__Gpu = "true"
#   dotnet run --project src/AristaMcp.Cli --no-build -- ingest --force ...
#
# After the ingest finishes, a plain `dotnet build` restores the CPU flavour.

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$cfg = if ($Release) { 'Release' } else { 'Debug' }

Push-Location (Join-Path $PSScriptRoot '..')
try {
    if ($Clean) {
        Write-Host "▸ clean bin/obj" -ForegroundColor Cyan
        # Removing obj/ is essential — stale NuGet resolution caches a CPU
        # asset graph that won't match the GPU PackageReference otherwise.
        Get-ChildItem -Recurse -Directory -Include 'bin', 'obj' |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    }

    Write-Host "▸ dotnet build -c $cfg -p:UseGpuOnnx=true" -ForegroundColor Cyan
    dotnet build -c $cfg -p:UseGpuOnnx=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit $LASTEXITCODE" }

    Write-Host ""
    Write-Host "✓ GPU build ready. Next:" -ForegroundColor Green
    Write-Host '    $env:ARISTA_MCP__Gpu = "true"' -ForegroundColor Gray
    Write-Host "    dotnet run --project src/AristaMcp.Cli --no-build -c $cfg -- ingest --force ..." -ForegroundColor Gray
    Write-Host ""
    Write-Host "To restore the CPU flavour:" -ForegroundColor Yellow
    Write-Host "    pwsh scripts/build-gpu.ps1 -Clean   # optional" -ForegroundColor Gray
    Write-Host "    dotnet build -c $cfg --nologo" -ForegroundColor Gray
}
finally {
    Pop-Location
}
