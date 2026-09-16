# ERCTelemetry installer build - one command:  powershell -File installer\build-installer.ps1
#
# Produces the files to upload to the fixed path on the website server:
#   installer\release\ERCTelemetry-Setup-<version>.exe (published file - version in the name)
#   installer\release\ERCTelemetry-Setup-<version>.exe.sha256
#   installer\release\ERCTelemetry.version.json        (in-app update manifest; its fileName
#                                                       field points at the versioned file)
#   installer\release\ERCTelemetry.files.json         (delta-update file manifest: every file
#                                                       with path/size/sha256 - the app downloads
#                                                       only changed files)
#   installer\release\UPDATELOG.md                     (user-visible update log)
#
# From 0.1.0 on there is NO stable-name copy (ERCTelemetry-Setup.exe) anymore: the versioned
# file IS the published one, every release begins officially with 0.1.0 and the fixed
# website download link points at the versioned file. Old stable-name files are not touched
# or deleted by this script — remove them on the server manually if leftovers exist.
#
# The app installs self-contained (no .NET preinstall needed) into the user folder, creates
# the firewall rule on first start and registers a proper uninstaller. Updates are
# Discord-style deltas (only changed files, no UAC) via ERCTelemetry.files.json; the full
# Setup.exe remains the fallback. The installer ships UPDATELOG.md inside the install
# directory (stage\*) so the app can show it after an update; settings + history in
# %LOCALAPPDATA%\ERCTelemetry survive an update.

param(
    # Used by the VS publish profile WebServer.pubxml: the publish output is already
    # staged, so the script skips its own clean+publish and continues with ISCC.
    [switch]$SkipPublish,

    # Beta release: uploads into a "beta" subfolder of the server target instead of the
    # main download folder — the main-channel update manifest is NOT touched. Beta users
    # download the versioned Setup file from /downloads/beta/ manually (a lower version
    # than the stable one is never offered in-app to stable users, which is exactly the
    # point of a beta channel).
    [switch]$Beta,

    # Local build without a server: skips the upload + post-upload verification entirely
    # and leaves the release in installer\release. Useful for testing the pipeline.
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot   # repo/installer - all paths script-relative

# --- locate the Inno Setup compiler ------------------------------------------
$iscc = $env:ISCC
if (-not $iscc -or -not (Test-Path $iscc)) { $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe). Install Inno Setup 6 or set ISCC=<path to ISCC.exe>." }

# --- clean stage + release (with -SkipPublish the stage comes from the VS publish) ---
$stage   = Join-Path $PSScriptRoot 'stage'
$release = Join-Path $PSScriptRoot 'release'
if (-not $SkipPublish) {
    foreach ($d in $stage, $release) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force -Confirm:$false }
        New-Item -ItemType Directory -Force $d | Out-Null
    }
}

# --- publish self-contained win-x64 into stage --------------------------------
if ($SkipPublish) {
    if (-not (Test-Path "$stage\ERCTelemetry.exe")) { throw "-SkipPublish: stage has no ERCTelemetry.exe - run the VS publish (WebServer profile) first" }
} else {
    Write-Host '==> dotnet publish (self-contained win-x64, ~1-2 min)'
    dotnet publish "$PSScriptRoot\..\src\ERCTelemetry.App\ERCTelemetry.App.csproj" -c Release -p:PublishProfile=WebRelease
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path "$stage\ERCTelemetry.exe")) { throw "publish output missing: $stage\ERCTelemetry.exe" }
}

# --- version: from the staged exe, so installer version == app version --------
$vi = (Get-Item "$stage\ERCTelemetry.exe").VersionInfo
$ver = if ($vi.ProductVersion) { ($vi.ProductVersion -split '\+')[0] } else { $vi.FileVersion }
if (-not $ver) { throw "could not read version from $stage\ERCTelemetry.exe" }

# --- update log -----------------------------------------------------------------
# Mandatory per docs/RELEASE.md: every release needs an UPDATELOG.md entry for the exact
# version. The file goes into the stage (ships inside the install directory so the app can
# show it after an update) and into release\ (uploaded to the server for the Update-Log
# button URL). Checked before ISCC so a missing entry fails the build early.
$updatelog = Join-Path $PSScriptRoot '..\UPDATELOG.md'
if (-not (Test-Path $updatelog)) { throw "UPDATELOG.md missing at repo root - every release needs an update log entry (docs/RELEASE.md)" }
if ((Get-Content $updatelog -Raw) -notmatch ("##\s+" + [regex]::Escape($ver) + "\b")) {
    throw "UPDATELOG.md has no entry for version $ver - add one before publishing (docs/RELEASE.md)"
}
Copy-Item $updatelog (Join-Path $stage 'UPDATELOG.md') -Force

# --- Inno Setup compile --------------------------------------------------------
# Start-Process isolation: PowerShell 5.1 promotes stderr lines of native commands to
# errors under $ErrorActionPreference='Stop' when stderr is piped (e.g. by MSBuild Exec),
# which kills the script mid-run. Capturing ISCC output to files avoids that entirely.
Write-Host "==> ISCC (AppVersion=$ver)"
$isccOut = Join-Path $env:TEMP 'erc-iscc-out.txt'
$isccErr = Join-Path $env:TEMP 'erc-iscc-err.txt'
$isccProc = Start-Process -FilePath $iscc -ArgumentList "/DAppVersion=$ver", 'ERCTelemetry.iss' `
    -WorkingDirectory $PSScriptRoot -Wait -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $isccOut -RedirectStandardError $isccErr
$errLines = @(Get-Content $isccErr -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
Get-Content $isccOut -ErrorAction SilentlyContinue | Select-Object -Last 4 | ForEach-Object { Write-Host "    $_" }
if ($isccProc.ExitCode -ne 0 -or $errLines) {
    $errLines | ForEach-Object { Write-Host "    ISCC: $_" }
    throw "ISCC failed (exit $($isccProc.ExitCode))"
}

# --- checksums -------------------------------------------------------------------
# The versioned file is the published one — no stable-name copy anymore (from 0.1.0 on
# every release is official, see header comment). Gets a .sha256 sidecar.
$versioned = Join-Path $release "ERCTelemetry-Setup-$ver.exe"
$hash = (Get-FileHash $versioned -Algorithm SHA256).Hash
Set-Content -Encoding ascii -Path "$versioned.sha256" -Value $hash

# --- update manifest for the in-app update check --------------------------------
# The app fetches this file, compares versions against its own and verifies the
# downloaded Setup.exe against the sha256 field before launching it. Pure ASCII
# content, so -Encoding ascii (PS 5.1 utf8 would add a BOM).
$manifest = [ordered]@{
    version      = $ver
    publishedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    sha256       = (Get-Content "$versioned.sha256").Trim()
    fileName     = (Split-Path $versioned -Leaf)
}
Set-Content -Encoding ascii -Path (Join-Path $release 'ERCTelemetry.version.json') -Value ($manifest | ConvertTo-Json)

# --- file manifest for the delta update -------------------------------------------
# ERCTelemetry.files.json lists every file of the release (relative path, size, sha256).
# The app compares its local install against it and downloads only changed files into a
# staging folder (Discord-style delta, no UAC). Paths use forward slashes (the app converts
# to platform separators) and are relative to the install root. Pure ASCII content.
$files = Get-ChildItem -Path $stage -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length).TrimStart('\') -replace '\\', '/'
    [ordered]@{
        path   = $rel
        size   = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}
$fileManifest = [ordered]@{ files = $files }
Set-Content -Encoding ascii -Path (Join-Path $release 'ERCTelemetry.files.json') -Value ($fileManifest | ConvertTo-Json -Depth 3)
Write-Host ("==> ERCTelemetry.files.json: {0} files" -f $files.Count)

# Update log also into release\ — uploaded to the server next to the manifest so the
# app's "Update-Log" button URL (…/UPDATELOG.md) works.
Copy-Item $updatelog (Join-Path $release 'UPDATELOG.md') -Force

Write-Host ''
Write-Host '==> Release ready (upload these to the fixed server path):'
foreach ($f in $versioned) {
    $size = '{0:N1} MB' -f ((Get-Item $f).Length / 1MB)
    $hash = (Get-Content "$f.sha256").Trim()
    '{0,-44} {1,9}  sha256={2}' -f (Split-Path $f -Leaf), $size, $hash.Substring(0, 16) + '...'
}
foreach ($f in 'ERCTelemetry.version.json', 'ERCTelemetry.files.json', 'UPDATELOG.md') {
    $p = Join-Path $release $f
    if (Test-Path $p) { '{0,-44} {1,9}' -f $f, ('{0:N0} KB' -f ((Get-Item $p).Length / 1KB)) }
}

# --- keep only the newest 3 versioned Setup files in release\ ---------------------
# Old versioned files are never deleted from the server by this script (the server keeps
# history), but locally release\ should not grow unboundedly. Newest = newest build time;
# releases are built in version order in practice.
$oldSetups = Get-ChildItem -Path $release -Filter 'ERCTelemetry-Setup-*.exe' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip 3
foreach ($old in $oldSetups) {
    Remove-Item $old.FullName -Force
    $oldSha = "$($old.FullName).sha256"
    if (Test-Path $oldSha) { Remove-Item $oldSha -Force }
    Write-Host "==> removed old release: $($old.Name)"
}

# --- upload to the website server (Windows share) ------------------------------
# server-target.txt: one line = target folder on the server share, e.g.  \\server\wwwroot\download
# (created empty; filled in when the web path exists). Missing/empty file = skip upload.
# -SkipUpload skips this whole block (local build without a server).
if (-not $SkipUpload) {
    $targetFile = Join-Path $PSScriptRoot 'server-target.txt'
    if (Test-Path $targetFile) {
        $target = (Get-Content $targetFile | Where-Object { $_.Trim() } | Select-Object -First 1)
        if ($target) {
            $target = $target.Trim()
            if ($Beta) { $target = Join-Path $target 'beta' }
            if (-not (Test-Path $target -PathType Container)) {
                if ($Beta) { New-Item -ItemType Directory -Force $target | Out-Null }
                else { throw "server target folder not reachable: $target - check VPN/share/drive letter" }
            }
            Write-Host "==> uploading to $target$(if ($Beta) { ' (beta channel)' })"
            Copy-Item $versioned, "$versioned.sha256",
                (Join-Path $release 'ERCTelemetry.version.json'),
                (Join-Path $release 'ERCTelemetry.files.json'),
                (Join-Path $release 'UPDATELOG.md') $target -Force
            Write-Host ("==> uploaded: ERCTelemetry-Setup-{0}.exe + .sha256 + ERCTelemetry.version.json + ERCTelemetry.files.json + UPDATELOG.md" -f $ver)

            # --- post-upload verification -------------------------------------------
            # Re-read the uploaded manifest from the server and check version + sha256
            # against the local build (automates what RELEASE.md used to demand manually).
            $uploaded = Get-Content (Join-Path $target 'ERCTelemetry.version.json') -Raw | ConvertFrom-Json
            if ($uploaded.version -ne $ver) {
                throw "upload verification failed: server manifest version '$($uploaded.version)' != local '$ver'"
            }
            if ($uploaded.sha256 -ne $hash) {
                throw "upload verification failed: server manifest sha256 '$($uploaded.sha256)' != local '$hash'"
            }
            Write-Host '==> upload verified: server manifest version + sha256 match the local build'
        } else {
            Write-Host '==> server-target.txt is empty - release stays local in installer\release'
        }
    } else {
        Write-Host '==> no server-target.txt - release stays local in installer\release'
    }
} else {
    Write-Host '==> -SkipUpload: release stays local in installer\release'
}
