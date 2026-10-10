# Builds the portable Windows release: a self-contained win-x64 publish zipped as
#   artifacts/release/GmailWatcher-<version>-win-x64.zip  (+ .sha256)
# The .NET runtime is included, so the zip runs on Windows 10 1809+ x64 without installing .NET.
#
# Reproducible: locked restore, deterministic compilation with CI path mapping, no debug symbols, and a zip whose
# entries are sorted and stamped with the commit time. The same commit gives the same SHA-256 from any checkout
# path. (Avalonia's XAML compiler rewrites GmailWatcher.dll after the C# compiler and records the absolute PDB and
# .axaml paths, which path mapping does not cover; leaving symbols out removes them.)
#
#   pwsh scripts/publish-windows.ps1              # restore, test, publish, zip
#   pwsh scripts/publish-windows.ps1 -SkipTests   # when the tests already ran for this commit
#
# Signing is not done here: signing keys never belong in this repository (see README, "Releases").
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Output = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$project = 'src/GmailWatcher.Desktop/GmailWatcher.Desktop.csproj'
$runtime = 'win-x64'

$commit = (git rev-parse HEAD).Trim()
# Untracked files count too: the SDK globs would compile a stray *.cs or *.axaml under src/ into the release.
$dirty = [bool](git status --porcelain)
if ($dirty) {
    Write-Warning 'The working tree has uncommitted or untracked files: this build is not reproducible from the commit and must not be released.'
}
# Zip timestamps come from the commit so they do not depend on when or where the build ran.
$stamp = [DateTimeOffset]::FromUnixTimeSeconds([long](git log -1 --format=%ct HEAD)).UtcDateTime

$version = (dotnet msbuild $project -getProperty:Version).Trim()
if (-not $version) { throw 'Could not read the version from the project.' }
$name = "GmailWatcher-$version-$runtime"
$publishDir = Join-Path $root "artifacts/publish/$runtime"
$outDir = Join-Path $root $Output
$zip = Join-Path $outDir "$name.zip"

Invoke-Checked 'Restore (locked)' { dotnet restore GmailWatcher.slnx --locked-mode }
if (-not $SkipTests) {
    Invoke-Checked 'Build' { dotnet build GmailWatcher.slnx -c Release --no-restore }
    Invoke-Checked 'Test' { dotnet test --solution GmailWatcher.slnx -c Release --no-build }
}

# Start from empty output folders so files from an earlier publish cannot leak into the zip.
foreach ($dir in $publishDir, (Join-Path $root "src/GmailWatcher.Desktop/obj/Release/net10.0/$runtime")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
Invoke-Checked 'Publish' {
    dotnet publish $project -c Release -r $runtime --self-contained -p:RestoreLockedMode=true `
        -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -o $publishDir
}
if (-not (Test-Path (Join-Path $publishDir 'GmailWatcher.exe'))) { throw 'The publish did not produce GmailWatcher.exe.' }

# The license and the third-party notices travel with the program.
foreach ($notice in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    if (-not (Test-Path (Join-Path $root $notice))) { throw "$notice is missing from the repository root." }
    Copy-Item (Join-Path $root $notice) (Join-Path $publishDir $notice)
}

# Marks the folder as a release: only such a copy may replace itself with a newer release (in-app updates).
# "files" lists everything the release ships (ordinal order), so removing a release deletes exactly these files and
# never anything else that happens to share the folder.
$shipped = [string[]](Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') }) + 'release.json'
[Array]::Sort($shipped, [StringComparer]::Ordinal)
$manifest = [ordered]@{ version = $version; commit = $commit; runtime = $runtime; files = $shipped }
Set-Content -Path (Join-Path $publishDir 'release.json') -Value ($manifest | ConvertTo-Json -Compress) -Encoding utf8 -NoNewline

Write-Host '==> Zip' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression
# The runtime pack's own .pdb files are not shipped either.
$files = @(Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [pscustomobject]@{ File = $_; Entry = 'GmailWatcher/' + [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') } })
# Ordinal order, so the entry order cannot depend on the culture or ICU version.
$entries = [string[]]($files | ForEach-Object Entry)
$items = [object[]]$files
[Array]::Sort($entries, $items, [StringComparer]::Ordinal)
$files = $items
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files) {
            $entry = $archive.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $source = $item.File.OpenRead()
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "Toolchain: .NET SDK $((dotnet --version).Trim()), PowerShell $($PSVersionTable.PSVersion)"
Write-Host "Version : $version ($($commit.Substring(0, 7))$(if ($dirty) { ', dirty' }))"
Write-Host "Zip     : $zip ($size MB, $($files.Count) files)"
Write-Host "SHA-256 : $hash"
Write-Host "Run     : extract the zip, then start GmailWatcher\GmailWatcher.exe"
