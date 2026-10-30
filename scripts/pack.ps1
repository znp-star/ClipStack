<#
.SYNOPSIS
    Builds a Velopack installer for ClipStack.

.DESCRIPTION
    Publishes a self-contained win-x64 build and hands it to vpk, which produces the installer
    (Setup.exe), the portable zip and the update packages (nupkg + RELEASES) consumed by
    Updater.cs.

.PARAMETER Version
    SemVer version, e.g. 0.2.0. Must be higher than the published one, otherwise the app will
    never see an update.

.PARAMETER ReleaseNotes
    Markdown file used as the release notes. Defaults to the matching section of CHANGELOG.md,
    which must exist, so a release never ships with just a version number.

.EXAMPLE
    ./scripts/pack.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseNotes
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ClipStack.App\ClipStack.App.csproj'
$icon = Join-Path $root 'src\ClipStack.App\Assets\ClipStack.ico'
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\releases'

$vpk = $null
$cmd = Get-Command vpk -ErrorAction SilentlyContinue
if ($cmd) {
    $vpk = $cmd.Source
} else {
    $vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
}

if (-not (Test-Path $vpk)) {
    throw 'vpk not found. Install it once with: dotnet tool install --global vpk'
}

# vpk embeds the notes into the package, so the GitHub release body is the changelog section
# rather than a bare version number.
if (-not $ReleaseNotes) {
    $changelog = Join-Path $root 'CHANGELOG.md'
    if (-not (Test-Path $changelog)) {
        throw "CHANGELOG.md not found: $changelog"
    }

    $escaped = [regex]::Escape($Version)
    $section = [regex]::Match(
        (Get-Content $changelog -Raw),
        "(?ms)^##[ \t]*\[?v?$escaped\]?[^\r\n]*\r?\n(.*?)(?=^##[ \t]|\z)"
    )
    if (-not $section.Success) {
        throw "CHANGELOG.md has no section for $Version. Add one before packing."
    }

    $body = $section.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($body)) {
        throw "CHANGELOG.md section for $Version is empty. Write what changed in this release."
    }

    $ReleaseNotes = Join-Path $root "artifacts\release-notes-$Version.md"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReleaseNotes) | Out-Null
    [System.IO.File]::WriteAllText($ReleaseNotes, $body, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host "Release notes ($ReleaseNotes):"
Get-Content $ReleaseNotes | ForEach-Object { "  $_" }

Write-Host "Publishing $Version (win-x64, self-contained)..."
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed'
}

Write-Host 'Packing with Velopack...'
& $vpk pack `
    --packId ClipStack `
    --packVersion $Version `
    --runtime win-x64 `
    --packDir $publish `
    --mainExe ClipStack.exe `
    --packTitle ClipStack `
    --packAuthors znp-star `
    --icon $icon `
    --releaseNotes $ReleaseNotes `
    -o $output
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed. 若提示版本已存在，说明 $output 里已有 >= $Version 的发布：" +
          '换一个更高的版本号，或删掉该目录从头开始。'
}

Write-Host "Done. Artifacts in $output"
