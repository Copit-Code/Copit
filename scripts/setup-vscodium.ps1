<#
.SYNOPSIS
  Richtet Copit auf VSCodium (portable) ein: VSCodium, Copit-Extension, Theme,
  Solution Explorer und C#-IntelliSense aus Open VSX, Desktop-Verknüpfung.

.EXAMPLE
  ./scripts/setup-vscodium.ps1 -Repo "dein-user/copit"
  ./scripts/setup-vscodium.ps1 -Vsix ./dist/copit.vsix
#>
param(
  [string]$Repo = "",                                   # GitHub owner/name für Copit-Releases (Auto-Update)
  [string]$Vsix = "",                                   # lokale .vsix statt Release
  [string]$Target = "$env:LOCALAPPDATA\Copit\VSCodium"  # Installationsordner
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "== VSCodium (portable) ==" -ForegroundColor DarkYellow
if (-not (Test-Path "$Target\VSCodium.exe")) {
  $rel = Invoke-RestMethod "https://api.github.com/repos/VSCodium/vscodium/releases/latest" -Headers @{ "User-Agent" = "copit-setup" }
  $asset = $rel.assets | Where-Object { $_.name -match '^VSCodium-win32-x64-[\d.]+\.zip$' } | Select-Object -First 1
  if (-not $asset) { throw "Kein VSCodium-ZIP im Release $($rel.tag_name) gefunden." }
  $zip = Join-Path $env:TEMP $asset.name
  Write-Host "Lade $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)…"
  Invoke-WebRequest $asset.browser_download_url -OutFile $zip
  New-Item -ItemType Directory -Force $Target | Out-Null
  Expand-Archive $zip -DestinationPath $Target -Force
  Remove-Item $zip
} else { Write-Host "Bereits vorhanden: $Target" }

# Portable-Modus: alle Daten im Ordner "data"
$data = Join-Path $Target "data"
New-Item -ItemType Directory -Force "$data\user-data\User" | Out-Null
$settingsFile = "$data\user-data\User\settings.json"
$settings = @{}
if (Test-Path $settingsFile) {
  try { (Get-Content $settingsFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $settings[$_.Name] = $_.Value } } catch { }
}
$settings["workbench.colorTheme"] = "Copit Dark"
$settings["window.title"] = "Copit — `${rootName}`${separator}`${activeEditorShort}"
$settings["workbench.startupEditor"] = "none"
$settings["editor.fontFamily"] = "'Cascadia Code', Consolas, monospace"
$settings["editor.fontLigatures"] = $true
$settings["telemetry.telemetryLevel"] = "off"
if ($Repo) { $settings["copit.update.repo"] = $Repo; $settings["copit.update.auto"] = $true }
$settings | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $settingsFile

$codium = Join-Path $Target "bin\codium.cmd"

Write-Host "== Extensions (Open VSX) ==" -ForegroundColor DarkYellow
foreach ($ext in @(
  "fernandoescolar.vscode-solution-explorer",   # Solution Explorer (.sln/.slnx/.csproj)
  "muhammad-sammy.csharp",                      # C#: IntelliSense, Code-Vervollständigung, Debugger (netcoredbg)
  "editorconfig.editorconfig",
  "eamodio.gitlens"
)) {
  Write-Host "  $ext"
  & $codium --install-extension $ext --force 2>$null | Out-Null
}

Write-Host "== Copit ==" -ForegroundColor DarkYellow
if (-not $Vsix -and $Repo) {
  # Neueste Release-VSIX über die GitHub CLI (privates Repo)
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if (-not $gh) { throw "Für private Releases wird die GitHub CLI benötigt: winget install GitHub.cli; gh auth login" }
  $dir = Join-Path $env:TEMP "copit-release"
  Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
  & gh release download --repo $Repo --pattern "*.vsix" --dir $dir
  $Vsix = (Get-ChildItem $dir -Filter *.vsix | Select-Object -First 1).FullName
}
if (-not $Vsix) {
  $local = Join-Path $PSScriptRoot "..\dist\copit.vsix"
  if (Test-Path $local) { $Vsix = (Resolve-Path $local).Path }
}
if ($Vsix) { & $codium --install-extension $Vsix --force | Out-Null; Write-Host "  installiert: $Vsix" }
else { Write-Warning "Keine Copit-VSIX gefunden. Mit -Vsix oder -Repo angeben." }

Write-Host "== Verknüpfung ==" -ForegroundColor DarkYellow
$lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Copit.lnk"
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $Target "VSCodium.exe"
$s.WorkingDirectory = $Target
$s.Description = "Copit – Agent IDE"
$s.Save()
Write-Host "Fertig. Starte Copit über die Desktop-Verknüpfung." -ForegroundColor Green
