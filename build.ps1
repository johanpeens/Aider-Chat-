<#
.SYNOPSIS
  Builds the Aider Chat Visual Studio extension and writes artifacts\AiderChat-<version>.vsix.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\AiderVS\AiderVS.csproj'

# Find MSBuild through vswhere (installed with Visual Studio).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio was not found (vswhere.exe is missing).' }

$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found. Install Visual Studio 2022 or later.' }

Write-Host "Building with $msbuild"
& $msbuild $project -restore -t:Rebuild "-p:Configuration=$Configuration" -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)." }

$vsix = Join-Path $root "src\AiderVS\bin\$Configuration\net472\AiderVS.vsix"
if (-not (Test-Path $vsix)) { throw "The build finished but $vsix was not produced." }

$manifest = [xml](Get-Content (Join-Path $root 'src\AiderVS\source.extension.vsixmanifest'))
$version = $manifest.PackageManifest.Metadata.Identity.Version

$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$target = Join-Path $artifacts "AiderChat-$version.vsix"
Copy-Item $vsix $target -Force

Write-Host ''
Write-Host "Done: $target"
