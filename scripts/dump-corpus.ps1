#!/usr/bin/env pwsh
# Dump the ingested arista corpus from a running Postgres container and
# upload it as an asset to a GitHub release.
#
# Idempotent: if the release already has an asset with a matching SHA-256
# the upload is skipped. `--clobber` replaces an existing asset cleanly.
#
# Usage:
#   pwsh scripts/dump-corpus.ps1 -Tag v0.3.0
#   pwsh scripts/dump-corpus.ps1 -Tag v0.3.0 -Container my-pg -NoUpload

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Tag,

    [string]$Container = 'arista-mcp-postgres',
    [string]$Database = 'arista',
    [string]$User = 'arista',
    [string]$Runtime = 'podman',
    [string]$Output = '',
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command $Runtime -ErrorAction SilentlyContinue)) {
    throw "'$Runtime' not found in PATH — pass -Runtime docker (or install podman)."
}

# Verify the container is reachable. `container exists` is the podman-only
# subcommand; `ps --filter name=...` works for both podman and docker.
$existing = & $Runtime ps --filter "name=^$Container$" --quiet 2>$null
if (-not $existing) {
    throw "container '$Container' not running — start it before dumping."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Output) {
    $Output = Join-Path $repoRoot 'publish'
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$dumpName = "arista-corpus-$Tag.dump"
$dump = Join-Path $Output $dumpName
$sha  = "$dump.sha256"

Write-Host "[dump] running pg_dump inside $Container"
Write-Host "[dump] db=$Database user=$User -> $dump"

# Use $env:PGPASSWORD from the caller's environment; fall back to user name.
$pgPassword = $env:PGPASSWORD
if (-not $pgPassword) {
    $pgPassword = $User
}

# Stream stdout from the container straight into the host file so we
# never duplicate the dump on the container's writable layer.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName  = $Runtime
$psi.Arguments = "exec -e PGPASSWORD=$pgPassword -i $Container pg_dump -Fc -Z 6 -U $User -d $Database"
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
$fs = [System.IO.File]::Create($dump)
try {
    $proc.StandardOutput.BaseStream.CopyTo($fs)
} finally {
    $fs.Dispose()
    $proc.WaitForExit()
}
if ($proc.ExitCode -ne 0) {
    throw "pg_dump exited with $($proc.ExitCode)"
}

# Local SHA-256.
$hash = (Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $dumpName" | Set-Content -LiteralPath $sha -NoNewline:$false

$bytes = (Get-Item -LiteralPath $dump).Length
Write-Host "[dump] wrote $dump ($bytes bytes)"
Write-Host "[dump] sha256:$hash"

if ($NoUpload) {
    Write-Host '[dump] -NoUpload — done.'
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "'gh' CLI not found — install or pass -NoUpload."
}

# Idempotency: skip upload if the release already has an asset with the
# same digest. GitHub stamps `digest = sha256:<hex>` once post-processing
# finishes; the field may be empty during the brief upload window.
$expected = "sha256:$hash"
$existingDigest = ''
try {
    $existingDigest = (& gh release view $Tag -R dantte-lp/arista-mcp `
        --json assets --jq ".assets[] | select(.name == `"$dumpName`") | .digest" 2>$null)
} catch {
    $existingDigest = ''
}
if ($existingDigest -eq $expected) {
    Write-Host "[dump] release asset already at $expected — skipping upload."
    return
}

Write-Host "[dump] uploading to release $Tag"
& gh release upload $Tag $dump $sha --clobber -R dantte-lp/arista-mcp
if ($LASTEXITCODE -ne 0) {
    throw "gh release upload failed (exit $LASTEXITCODE)"
}

Write-Host '[dump] done.'
