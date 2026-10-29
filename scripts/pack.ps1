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

.EXAMPLE
    ./scripts/pack.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
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
    -o $output
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed. 若提示版本已存在，说明 $output 里已有 >= $Version 的发布：" +
          '换一个更高的版本号，或删掉该目录从头开始。'
}

Write-Host "Done. Artifacts in $output"
