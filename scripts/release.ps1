<#
.SYNOPSIS
  Neues Copit-Release: Version setzen, committen, Tag pushen.
  GitHub Actions baut daraus die .vsix und hängt sie ans Release –
  alle Copit-Installationen aktualisieren sich dann automatisch.

.EXAMPLE
  ./scripts/release.ps1 0.2.0
#>
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (git status --porcelain) { throw "Arbeitskopie ist nicht sauber – erst committen." }
Push-Location vscode/copit
npm version $Version --no-git-tag-version | Out-Null
npm test
Pop-Location

git add vscode/copit/package.json vscode/copit/package-lock.json
git commit -m "Release v$Version"
git tag "v$Version"
git push
git push origin "v$Version"
Write-Host "Tag v$Version gepusht – Release-Workflow läuft auf GitHub." -ForegroundColor Green
