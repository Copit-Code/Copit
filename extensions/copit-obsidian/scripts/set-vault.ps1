# Copit-Aktion: Vault (und Unterordner) dauerhaft festlegen → %LOCALAPPDATA%\Copit\obsidian.json
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CopitObsidian.psm1') -Force -DisableNameChecking

$vault = Get-CopitValue 'vault'
$folder = Get-CopitValue 'folder' 'Copit'
if (-not $vault) { $v = Resolve-Vault; $vault = $v.Path }   # leer = automatisch erkannten Vault übernehmen
if (-not (Test-Path -LiteralPath $vault)) { throw "Ordner existiert nicht: $vault" }
if (-not (Test-Path -LiteralPath (Join-Path $vault '.obsidian'))) { Write-Warning "$vault enthält keinen .obsidian-Ordner – ist das ein Vault?" }
$file = Join-Path (Get-CopitHome) 'obsidian.json'
$json = [ordered]@{ vault = (Resolve-Path -LiteralPath $vault).Path; folder = $folder.Trim('\', '/') } | ConvertTo-Json
[IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Output "Vault festgelegt: $vault (Ordner $folder)"
