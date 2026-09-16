# Applies a staged delta update (Discord-style): waits for the app to exit, copies the
# staged files over the install directory, deletes files that are no longer part of the
# release, restarts the app and writes apply-result.json. Runs hidden, no UAC - the install
# directory is user-writable. PowerShell 5.1-compatible (no ??, no ternary, ASCII only).
#
# The app copies this script to %LOCALAPPDATA%\ERCTelemetry\update\ before launching it, so
# it is never locked by the files it replaces.

$ErrorActionPreference = 'Stop'
$updateRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$applyPath = Join-Path $updateRoot 'apply.json'
$resultPath = Join-Path $updateRoot 'apply-result.json'

function Write-Result {
    param([bool]$Ok, [string]$ErrorText)
    $result = @{ ok = $Ok }
    if ($ErrorText) { $result.error = $ErrorText }
    $result | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8
}

try {
    if (-not (Test-Path $applyPath)) {
        throw "apply.json fehlt: $applyPath"
    }

    $apply = Get-Content -Path $applyPath -Raw | ConvertFrom-Json
    $staging = $apply.staging
    $target = $apply.target
    $appExe = $apply.appExe

    # Wait for the app to exit (it shuts down right after launching this script).
    $appName = [System.IO.Path]::GetFileNameWithoutExtension($appExe)
    $deadline = (Get-Date).AddSeconds(60)
    $appExited = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name $appName -ErrorAction SilentlyContinue)) {
            $appExited = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $appExited) {
        throw "Die App wurde nicht rechtzeitig beendet - Update abgebrochen."
    }

    # Copy staged files over the install directory (unchanged files are not staged).
    foreach ($file in $apply.files) {
        $rel = $file.path -replace '/', '\'
        $src = Join-Path $staging $rel
        if (-not (Test-Path $src)) { continue }
        $dst = Join-Path $target $rel
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item -Path $src -Destination $dst -Force
    }

    # Delete files in the install dir that are no longer part of the release. The safety
    # list keeps installer artifacts (unins000.*) and the shipped update log, which the
    # publish output does not list.
    $keep = @{}
    foreach ($file in $apply.files) {
        $keep[($file.path -replace '/', '\')] = $true
    }
    $safety = @('unins000.exe', 'unins000.dat', 'unins000.msg', 'UPDATELOG.md')
    Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($target.Length).TrimStart('\')
        if ($keep.ContainsKey($rel)) { return }
        if ($safety -contains $_.Name) { return }
        Remove-Item -Path $_.FullName -Force
    }

    # Restart the app (runs as the user, no UAC).
    Start-Process -FilePath $appExe

    Write-Result $true $null
}
catch {
    Write-Result $false $_.Exception.Message
    exit 1
}
