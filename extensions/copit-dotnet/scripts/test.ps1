# Copit-Extension-Aktion: dotnet test im Repo ($env:COPIT_REPO), optional mit Filter
$ErrorActionPreference = "Continue"
$repo = if ($env:COPIT_REPO) { $env:COPIT_REPO } else { (Get-Location).Path }
Set-Location $repo
$dotnetArgs = @("test", "--nologo", "-v", "q")
if ($env:COPIT_PARAM_FILTER) { $dotnetArgs += @("--filter", $env:COPIT_PARAM_FILTER) }
$out = & dotnet @dotnetArgs 2>&1 | Out-String
$summary = ($out -split "`n" | Where-Object { $_ -match "(Bestanden|Passed|Fehler|Failed)!?\s*[-:]" } | Select-Object -Last 3) -join " | "
if ($LASTEXITCODE -ne 0) { Write-Output "Tests fehlgeschlagen: $summary"; exit 1 }
Write-Output "Tests grün: $summary"
