#!/usr/bin/env pwsh
# PVS-Studio scan runner (C#). Iterates each src/*.csproj (PVS-Studio_Cmd does not
# accept the .slnx format), merges the .plog outputs, and renders a clickable HTML
# report into pvs-report/. Mirrors the sibling nutanix-mcp setup.
#
# Prerequisites (Windows host — Linux/CI uses `pvs-studio-dotnet`, see the
# `pvs-studio` job in .github/workflows/ci.yml):
#   - PVS-Studio installed at C:\Program Files (x86)\PVS-Studio.
#   - PVS license configured (PVS-Studio_Cmd credentials -u <user> -n <key>).
#
# Usage:
#   pwsh scripts/pvs-studio-scan.ps1
#   pwsh scripts/pvs-studio-scan.ps1 -Configuration Debug

param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "pvs-results.plog",
    [string]$ReportDir = "pvs-report"
)

$ErrorActionPreference = 'Stop'

$pvsDir = "C:\Program Files (x86)\PVS-Studio"
$pvsCmd = Join-Path $pvsDir "PVS-Studio_Cmd.exe"
$htmlGen = Join-Path $pvsDir "HtmlGenerator.exe"
if (-not (Test-Path $pvsCmd)) {
    throw "PVS-Studio not found at $pvsCmd. Install it from https://pvs-studio.com/."
}

# Build first so PVS sees consistent obj/ artifacts. Use the slnx for the build
# itself — only the analyzer pass needs csproj-by-csproj.
Write-Host "[pvs] building arista-mcp.slnx ($Configuration)"
dotnet build arista-mcp.slnx --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with code $LASTEXITCODE"
}

$projects = Get-ChildItem -Path src -Recurse -Filter "AristaMcp.*.csproj"
$plogs = @()
foreach ($proj in $projects) {
    $partial = "$($proj.BaseName).plog"
    Write-Host "[pvs] analyzing $($proj.Name)"
    & $pvsCmd -t $proj.FullName -c $Configuration -o $partial
    # Exit code is a bit mask: 256 = "issues found" (success-with-findings),
    # 1024 = "license expires soon". Anything else is a real error.
    $allowed = 0 -bor 256 -bor 1024
    $unexpected = $LASTEXITCODE -band (-bnot $allowed)
    if ($unexpected -ne 0) {
        throw "PVS-Studio_Cmd exited with code $LASTEXITCODE on $($proj.Name)"
    }
    $plogs += $partial
}

# Concatenate the per-project plogs into a single report (line-delimited format;
# HtmlGenerator collapses dupes).
Write-Host "[pvs] merging $($plogs.Count) plog files into $OutputPath"
Get-Content -LiteralPath $plogs | Set-Content -LiteralPath $OutputPath -Encoding utf8

Write-Host "[pvs] rendering HTML report to $ReportDir"
if (Test-Path $ReportDir) { Remove-Item -Recurse -Force $ReportDir }
& $htmlGen -t fullhtml -o $ReportDir -r (Get-Location).Path $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "HtmlGenerator exited with code $LASTEXITCODE"
}

Write-Host "[pvs] done. open $ReportDir/index.html for findings."
