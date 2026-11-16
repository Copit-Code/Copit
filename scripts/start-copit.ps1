<#
.SYNOPSIS
  Startet Copit: eigenes, aufgeräumtes VS-Code-Profil (nur Copit + Editor), Extension direkt aus dem Quellordner.

.EXAMPLE
  ./scripts/start-copit.ps1                      # öffnet C:\GIT\Copit
  ./scripts/start-copit.ps1 -Folder C:\GIT\Shop  # anderes Projekt
  ./scripts/start-copit.ps1 -Shortcut            # zusätzlich Desktop-Verknüpfung anlegen
#>
param(
  [string]$Folder = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [string]$Open = "time",
  [switch]$Shortcut
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$ext = Join-Path $root "vscode\copit"
$profile = Join-Path $env:LOCALAPPDATA "Copit\vscode-profile"
$extDir = Join-Path $env:LOCALAPPDATA "Copit\vscode-extensions"

# Editor finden: VSCodium bevorzugt, sonst VS Code
$candidates = @(
  "$env:LOCALAPPDATA\Copit\VSCodium\VSCodium.exe", "$env:LOCALAPPDATA\Programs\VSCodium\VSCodium.exe", "C:\Program Files\VSCodium\VSCodium.exe",
  "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe", "C:\Program Files\Microsoft VS Code\Code.exe")
$editor = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $editor) { throw "Weder VSCodium noch VS Code gefunden." }

# Extension bauen, falls nötig
if (-not (Test-Path "$ext\dist\extension.js")) {
  Push-Location $ext
  if (-not (Test-Path node_modules)) { npm install --no-fund --no-audit | Out-Null }
  npm run build | Out-Null
  Pop-Location
}

# Einstellungen des Copit-Profils (bestehende Werte bleiben erhalten)
New-Item -ItemType Directory -Force "$profile\User" | Out-Null
$settingsFile = "$profile\User\settings.json"
$s = @{}
if (Test-Path $settingsFile) { try { (Get-Content $settingsFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $s[$_.Name] = $_.Value } } catch { } }
$defaults = [ordered]@{
  "workbench.colorTheme" = "Copit Dark"
  "workbench.startupEditor" = "none"
  "workbench.tips.enabled" = $false
  "workbench.editor.empty.hint" = "hidden"
  "workbench.layoutControl.enabled" = $false
  "workbench.activity.showAccounts" = $false
  "workbench.secondarySideBar.defaultVisibility" = "hidden"
  "window.commandCenter" = $false
  "window.title" = "Copit — `${rootName}`${separator}`${activeEditorShort}"
  "chat.disableAIFeatures" = $true
  "chat.commandCenter.enabled" = $false
  "github.copilot.enable" = @{ "*" = $false }
  "security.workspace.trust.enabled" = $false
  "extensions.ignoreRecommendations" = $true
  "update.showReleaseNotes" = $false
  "telemetry.telemetryLevel" = "off"
  "editor.minimap.enabled" = $false
  "breadcrumbs.enabled" = $false
  "git.openRepositoryInParentFolders" = "never"
}
foreach ($k in $defaults.Keys) { if (-not $s.ContainsKey($k)) { $s[$k] = $defaults[$k] } }
$s | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $settingsFile

# Läuft Copit schon? Dann nur Fenster öffnen, sonst vorher die Leisten aufräumen
$running = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*Copit\vscode-profile*" }
if (-not $running) {
  New-Item -ItemType Directory -Force "$profile\User\globalStorage" | Out-Null
  node (Join-Path $PSScriptRoot "clean-profile.js") "$ext\node_modules\sql.js\dist\sql-wasm.js" "$profile\User\globalStorage\state.vscdb"
}

$env:COPIT_OPEN = $Open
$env:COPIT_PROFILE = "1"
Start-Process $editor -ArgumentList "--user-data-dir=`"$profile`"", "--extensions-dir=`"$extDir`"", "--extensionDevelopmentPath=`"$ext`"", "--new-window", "`"$Folder`""

if ($Shortcut) {
  $lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Copit.lnk"
  $sh = New-Object -ComObject WScript.Shell
  $l = $sh.CreateShortcut($lnk)
  $l.TargetPath = "powershell.exe"
  $l.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  $l.IconLocation = "$editor,0"
  $l.Description = "Copit – Agent IDE"
  $l.Save()
  Write-Host "Verknüpfung angelegt: $lnk"
}
