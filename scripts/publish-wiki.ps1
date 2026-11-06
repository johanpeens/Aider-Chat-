<#
.SYNOPSIS
  Publishes the pages in the repository's wiki\ folder to the GitHub wiki.

.DESCRIPTION
  A GitHub wiki is its own git repository (<repo>.wiki.git). It only exists after the first page has
  been created once in the browser: open the repository's Wiki tab, press "Create the first page" and
  save anything. After that, run this script. It clones the wiki, copies the pages over, and pushes.

.EXAMPLE
  .\scripts\publish-wiki.ps1
#>
param(
    [string]$WikiUrl = 'https://github.com/johanpeens/Aider-Chat-.wiki.git'
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\wiki'
$work = Join-Path ([IO.Path]::GetTempPath()) ('aider-chat-wiki-' + [guid]::NewGuid().ToString('N').Substring(0, 8))

$env:GIT_TERMINAL_PROMPT = '0'
git clone $WikiUrl $work
if ($LASTEXITCODE -ne 0) {
    throw "Could not clone $WikiUrl. Open the repository's Wiki tab on GitHub, create the first page once (any text), then run this again."
}

try {
    Get-ChildItem $work -Filter *.md | Remove-Item -Force
    Copy-Item (Join-Path $source '*.md') $work -Force

    Push-Location $work
    git add -A
    if (-not (git status --porcelain)) { Write-Host 'The wiki is already up to date.'; return }
    git commit -m 'Update wiki pages'
    git push
    if ($LASTEXITCODE -ne 0) { throw 'The push failed.' }
    Write-Host 'Wiki published.'
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
