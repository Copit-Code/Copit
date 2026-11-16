# Copit-Aktion: Architektur eines Repos als verkettete Obsidian-Notizen.
#   Applikation ──enthält──▶ Service ──nutzt──▶ Service
#                              │ ├─ruft auf──▶ API-Aufruf ──an──▶ System (projektübergreifend)  bzw. ──▶ Endpunkt (intern)
#                              │ ├─stellt bereit──▶ Endpunkt
#                              │ └─startet──▶ Werkzeug (projektübergreifend, z. B. git, claude)
#   Applikation ──hängt ab von──▶ Applikation, ──konfiguriert──▶ System
# Dazu: Übersicht mit Mermaid-Diagramm, Obsidian-Canvas und copit-index.json (Datei → Notizen) für Agent-Notizen.
# Beschreibungen/zusätzliche Beziehungen: <repo>/.copit/obsidian/beschreibungen.md (z. B. vom Agent „Architektur beschreiben“).
param([string]$Repo = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CopitObsidian.psm1') -Force -DisableNameChecking

if (-not $Repo) { $Repo = Get-CopitValue 'repo' $env:COPIT_REPO }
if (-not $Repo) { $Repo = (Get-Location).Path }
$Repo = (Resolve-Path -LiteralPath $Repo).Path.TrimEnd('\', '/')
$vault = Resolve-Vault
$project = Resolve-ProjectName $Repo
$projSafe = Get-SafeName $project
$archFolder = Join-NoteId @($vault.Root, 'Architektur', $projSafe)
$projectId = Get-ProjectNoteId $vault $project
$projectLink = Get-Link $projectId $project
$blockKey = 'projekt-' + (($projSafe.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-'))
$sep = [IO.Path]::DirectorySeparatorChar

# ================================================================ Dateien einsammeln
$ignore = @('.git', 'bin', 'obj', 'node_modules', 'dist', 'dist-test', 'out', 'build', '.vs', '.idea', '.vscode', 'artifacts', 'packages', 'coverage',
    '.copit', 'TestResults', 'vendor', '__pycache__', '.venv', 'venv', 'target', '.next', '.claude', '.obsidian')
$sourceExt = @('.cs', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.py', '.go', '.php', '.java', '.kt', '.vue', '.svelte', '.rs', '.rb')
$markerNames = @('package.json', 'pyproject.toml', 'go.mod', 'composer.json', 'pom.xml', 'build.gradle', 'Cargo.toml')

$allFiles = New-Object System.Collections.Generic.List[string]
function Walk([string]$Dir) {
    try { foreach ($f in [IO.Directory]::GetFiles($Dir)) { $allFiles.Add($f) } } catch { return }
    try { $subs = [IO.Directory]::GetDirectories($Dir) } catch { return }
    foreach ($d in $subs) {
        $n = [IO.Path]::GetFileName($d)
        if ($ignore -contains $n -or $n.StartsWith('.')) { continue }
        Walk $d
    }
}
Walk $Repo
function Rel([string]$Full) { if ($Full.Length -le $Repo.Length) { return '' }; return $Full.Substring($Repo.Length + 1).Replace('\', '/') }
function FileUrl([string]$RelPath) { $p = (($Repo + '/' + $RelPath) -replace '\\', '/' -replace ' ', '%20'); if ($p.StartsWith('/')) { return 'file://' + $p }; return 'file:///' + $p }

# ================================================================ Applikationen (Module)
function Slug([string]$s) { $x = (($s.ToLowerInvariant().Replace('.', '-') -replace '[^a-z0-9\-]+', '-') -replace '-{2,}', '-').Trim('-'); if (-not $x) { $x = 'modul' }; return $x }
$markerByDir = [ordered]@{}
foreach ($f in $allFiles) {
    $n = [IO.Path]::GetFileName($f)
    if ($n -match '\.(cs|fs)proj$' -or $markerNames -contains $n) {
        $d = [IO.Path]::GetDirectoryName($f)
        if (-not $markerByDir.Contains($d)) { $markerByDir[$d] = New-Object System.Collections.Generic.List[string] }
        $markerByDir[$d].Add($f)
    }
}
if ($markerByDir.Count -eq 0) {
    foreach ($d in [IO.Directory]::GetDirectories($Repo)) { $n = [IO.Path]::GetFileName($d); if (-not ($ignore -contains $n -or $n.StartsWith('.'))) { $markerByDir[$d] = New-Object System.Collections.Generic.List[string] } }
}
$apps = New-Object System.Collections.Generic.List[object]
foreach ($d in ($markerByDir.Keys | Sort-Object Length)) {
    $rel = Rel $d
    $markers = $markerByDir[$d]
    $proj = $markers | Where-Object { $_ -match 'proj$' } | Select-Object -First 1
    if ($proj) { $name = [IO.Path]::GetFileNameWithoutExtension($proj) } elseif ($rel) { $name = ($rel -split '/')[-1] } else { $name = Split-Path $Repo -Leaf }
    $kind = 'Ordner'
    $pj = $markers | Where-Object { $_ -match 'package\.json$' } | Select-Object -First 1
    if ($pj) { $j = Read-JsonFile $pj; $pn = Get-Prop $j 'name' ''; if ($pn) { $name = $pn }; $kind = 'Node' }
    if ($proj) { $kind = '.NET'; $t = [IO.File]::ReadAllText($proj); if ($t -match 'Microsoft\.NET\.Sdk\.(Web|Worker|Razor|BlazorWebAssembly)') { $kind = ".NET $($Matches[1])" } elseif ($t -match '<UseMaui>true') { $kind = '.NET MAUI' } elseif ($t -match 'xunit|nunit|MSTest') { $kind = '.NET Tests' } }
    elseif ($markers | Where-Object { $_ -match 'pyproject\.toml$' }) { $kind = 'Python' }
    elseif ($markers | Where-Object { $_ -match 'go\.mod$' }) { $kind = 'Go' }
    elseif ($markers | Where-Object { $_ -match 'Cargo\.toml$' }) { $kind = 'Rust' }
    elseif ($markers | Where-Object { $_ -match '(pom\.xml|build\.gradle)$' }) { $kind = 'Java' }
    elseif ($markers | Where-Object { $_ -match 'composer\.json$' }) { $kind = 'PHP' }
    $apps.Add([pscustomobject]@{ Name = $name; Path = $rel; Dir = $d; Kind = $kind; Markers = $markers; Slug = (Slug $name); DependsOn = (New-Object System.Collections.Generic.HashSet[string]); Packages = (New-Object System.Collections.Generic.List[string]); Systems = (New-Object System.Collections.Generic.HashSet[string]); Services = (New-Object System.Collections.Generic.List[object]); Lang = @{}; Files = 0; NoteId = '' })
}
foreach ($g in ($apps | Group-Object Name | Where-Object Count -gt 1)) { foreach ($a in $g.Group) { $a.Name = "$($a.Name) ($(if ($a.Path) { $a.Path } else { '/' }))" } }
function OwnerApp([string]$RelFile) {
    $best = $null
    foreach ($a in $apps) { if (($a.Path -eq '' -or $RelFile.StartsWith($a.Path + '/', [StringComparison]::OrdinalIgnoreCase)) -and ($null -eq $best -or $a.Path.Length -gt $best.Path.Length)) { $best = $a } }
    return $best
}
# Projekt-/Paketabhängigkeiten
foreach ($a in $apps) {
    foreach ($mk in $a.Markers) {
        $text = [IO.File]::ReadAllText($mk)
        if ($mk -match 'proj$') {
            foreach ($m in [regex]::Matches($text, '<ProjectReference\s+Include="([^"]+)"')) {
                $target = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath((Join-Path $a.Dir ($m.Groups[1].Value -replace '\\', $sep))))
                $t = $apps | Where-Object { $_.Dir -ieq $target } | Select-Object -First 1
                if ($t -and $t -ne $a) { [void]$a.DependsOn.Add($t.Name) }
            }
            foreach ($m in [regex]::Matches($text, '<PackageReference\s+Include="([^"]+)"')) { $a.Packages.Add($m.Groups[1].Value) }
        } elseif ($mk -match 'package\.json$') {
            $j = Read-JsonFile $mk
            foreach ($key in 'dependencies', 'devDependencies') {
                $deps = Get-Prop $j $key
                if ($deps) { foreach ($p in $deps.PSObject.Properties) { $t = $apps | Where-Object { $_.Name -eq $p.Name } | Select-Object -First 1; if ($t -and $t -ne $a) { [void]$a.DependsOn.Add($t.Name) } elseif ($key -eq 'dependencies') { $a.Packages.Add($p.Name) } } }
            }
        }
    }
}

# ================================================================ Quelltext analysieren
# Typdeklarationen, an denen eine Datei in Abschnitte geteilt wird (eine Klasse = ein möglicher Service)
$rxClass = @{
    cs = '(?m)^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file)\s+)?(?:(?:sealed|static|abstract|partial|readonly)\s+)*(?:class|record|struct)\s+(\w+)'
    ts = '(?m)^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)'
    py = '(?m)^class\s+(\w+)'
    jv = '(?m)^\s*(?:(?:public|final|abstract|open|data|internal)\s+)*class\s+(\w+)'
}
$serviceSuffix = '(Service|Client|Repository|Gateway|Api|Controller|Handler|Manager|Store|Runner|Locator|Hub|Factory|Provider|Updater|Resource|Router)$'
$calls = New-Object System.Collections.Generic.List[object]       # ausgehende API-Aufrufe
$endpoints = New-Object System.Collections.Generic.List[object]   # bereitgestellte Endpunkte
$services = New-Object System.Collections.Generic.List[object]
$configSystems = New-Object System.Collections.Generic.List[object]

function Normalize-Path([string]$p) {
    $p = $p.Trim()
    $p = [regex]::Replace($p, '\$?\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}', { param($m) $tok = [regex]::Matches($m.Groups[1].Value, '[A-Za-z_]\w*'); if ($tok.Count) { '{' + $tok[$tok.Count - 1].Value.ToLowerInvariant() + '}' } else { '{x}' } })
    $p = ($p -split '\?')[0]
    return $p
}
function Add-Call($Svc, [string]$Method, [string]$Raw, [string]$RelFile, [int]$Line) {
    $path = Normalize-Path $Raw
    if (-not $path -or $path -match '\s' -or $path.Length -gt 200 -or $path -match '^\{[^}]*\}$') { return }
    $system = ''
    if ($path -match '^(https?)://([^/{}]+)(.*)$') {
        $system = $Matches[2] -replace '^www\.', ''
        $path = $Matches[3]
        if ($system -match '^(localhost|127\.0\.0\.1)') { $system = 'localhost' }
    }
    if (-not $path) { $path = '/' }
    if (-not $path.StartsWith('/')) { $path = '/' + $path }
    $calls.Add([pscustomobject]@{ Service = $Svc; Method = $Method.ToUpperInvariant(); Path = $path; System = $system; File = $RelFile; Line = $Line })
}

$script:nl = $null
function Line([int]$Index) { $i = [Array]::BinarySearch($script:nl, $Index); if ($i -lt 0) { $i = -bnot $i }; return $i + 1 }

function Analyze-Segment($svc, [string]$lang, [string]$seg, [int]$off, [string]$rel) {
    # --- ausgehende HTTP-Aufrufe
    $rxCalls = switch ($lang) {
        'cs' { @('(?<![\w])(Get|Post|Put|Delete|Patch)(?:Async|FromJsonAsync|AsJsonAsync|StringAsync|StreamAsync|ByteArrayAsync)\(\s*\$?@?"([^"]*)"', 'HttpRequestMessage\(\s*HttpMethod\.(\w+)\s*,\s*\$?@?"([^"]*)"') }
        'ts' { @('\b(?:axios|http|https|api|client|this\.http|this\.client|\$http|ky)\.(get|post|put|delete|patch)\(\s*[`''"]([^`''"]+)[`''"]') }
        'py' { @('\b(?:requests|httpx|session|client|self\.session|self\.client)\.(get|post|put|delete|patch)\(\s*f?[''"]([^''"]+)[''"]') }
        'go' { @('\bhttp\.(Get|Post|Head)\(\s*"([^"]+)"') }
        'jv' { @('\.(get|post|put|delete|patch)(?:ForObject|ForEntity)?\(\s*"([^"]+)"') }
        default { @() }
    }
    foreach ($rx in $rxCalls) { foreach ($m in [regex]::Matches($seg, $rx)) { Add-Call $svc $m.Groups[1].Value $m.Groups[2].Value $rel (Line ($off + $m.Index)) } }
    if ($lang -eq 'ts') {
        foreach ($m in [regex]::Matches($seg, '\bfetch\(\s*[`''"]([^`''"]+)[`''"](?:\s*,\s*\{[\s\S]{0,200}?method\s*:\s*[''"](\w+)[''"])?')) { $meth = 'GET'; if ($m.Groups[2].Success) { $meth = $m.Groups[2].Value }; Add-Call $svc $meth $m.Groups[1].Value $rel (Line ($off + $m.Index)) }
    }
    foreach ($m in [regex]::Matches($seg, '(?:BaseAddress\s*=\s*new\s+Uri\(|baseURL\s*:\s*|base_url\s*=\s*)\$?[`''"](https?://[^/"''`{]+)')) { [void]$svc.Systems.Add(($m.Groups[1].Value -replace '^https?://(www\.)?', '')) }

    # --- bereitgestellte Endpunkte
    $eps = New-Object System.Collections.Generic.List[object]
    if ($lang -eq 'cs') {
        $classRoute = ''
        $cm = [regex]::Match($seg, '\[Route\("([^"]*)"\)\][\s\S]{0,300}?class\s+(\w+?)(Controller)?\b')
        if ($cm.Success) { $classRoute = $cm.Groups[1].Value -replace '\[controller\]', $cm.Groups[2].Value.ToLowerInvariant() }
        foreach ($m in [regex]::Matches($seg, '\[Http(Get|Post|Put|Delete|Patch)(?:\(\s*"([^"]*)"[^)]*\))?\]')) { $sub = $m.Groups[2].Value; if ($sub.StartsWith('/') -or $sub.StartsWith('~')) { $route = $sub.TrimStart('~') } else { $route = ($classRoute.TrimEnd('/') + '/' + $sub).TrimEnd('/') }; $eps.Add(@($m.Groups[1].Value, $route, $m.Index)) }
        foreach ($m in [regex]::Matches($seg, '\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"')) { $eps.Add(@($m.Groups[1].Value, $m.Groups[2].Value, $m.Index)) }
    } elseif ($lang -eq 'ts') {
        foreach ($m in [regex]::Matches($seg, '\b(?:app|router|server|fastify)\.(get|post|put|delete|patch)\(\s*[`''"](/[^`''"]*)[`''"]')) { $eps.Add(@($m.Groups[1].Value, $m.Groups[2].Value, $m.Index)) }
        $nest = [regex]::Match($seg, '@Controller\(\s*[''"]([^''"]*)[''"]')
        if ($nest.Success) { foreach ($m in [regex]::Matches($seg, '@(Get|Post|Put|Delete|Patch)\(\s*(?:[''"]([^''"]*)[''"])?\s*\)')) { $eps.Add(@($m.Groups[1].Value, ($nest.Groups[1].Value + '/' + $m.Groups[2].Value), $m.Index)) } }
    } elseif ($lang -eq 'py') {
        foreach ($m in [regex]::Matches($seg, '@\w+\.(get|post|put|delete|patch|route)\(\s*[''"]([^''"]+)[''"]')) { $meth = $m.Groups[1].Value; if ($meth -eq 'route') { $meth = 'GET' }; $eps.Add(@($meth, $m.Groups[2].Value, $m.Index)) }
    } elseif ($lang -eq 'jv') {
        foreach ($m in [regex]::Matches($seg, '@(Get|Post|Put|Delete|Patch)Mapping\(\s*(?:value\s*=\s*)?"([^"]*)"')) { $eps.Add(@($m.Groups[1].Value, $m.Groups[2].Value, $m.Index)) }
    }
    foreach ($e in $eps) {
        $r = '/' + ((Normalize-Path $e[1]) -replace '^/+', '' -replace '/+$', '')
        $endpoints.Add([pscustomobject]@{ Service = $svc; Method = ([string]$e[0]).ToUpperInvariant(); Path = $r; File = $rel; Line = (Line ($off + [int]$e[2])) })
    }

    # --- gestartete Werkzeuge (externe Anwendungen)
    $toolRx = switch ($lang) {
        'cs' { @('RunAsync\(\s*"([\w.\-]+)"', 'Process\.Start\(\s*"([\w.\-]+)"', 'FileName\s*=\s*"([\w.\-]+)"', 'ProcessStartInfo\(\s*"([\w.\-]+)"') }
        'ts' { @('(?<![\w.])(?:spawn|spawnSync|execFile|execFileSync|execa)\(\s*[''"]([\w.\-]+)[''"]', '(?:child_process|cp|childProcess)\.(?:spawn|spawnSync|execFile|execFileSync|exec|execSync)\(\s*[`''"]([\w.\-]+)[\s`''"]', '(?<![\w.])exec(?:Sync)?\(\s*[`''"]([\w.\-]+)\s') }
        'py' { @('subprocess\.\w+\(\s*\[\s*[''"]([\w.\-]+)[''"]', 'subprocess\.\w+\(\s*[''"]([\w.\-]+)[\s''"]') }
        'go' { @('exec\.Command\(\s*"([\w.\-]+)"') }
        default { @() }
    }
    foreach ($rx in $toolRx) { foreach ($m in [regex]::Matches($seg, $rx)) { $tool = ($m.Groups[1].Value -replace '\.exe$', '').ToLowerInvariant(); if ($tool -notmatch '^(cmd|sh|bash|zsh|start|open|xdg-open|select|insert|update|delete|create|drop|pragma)$' -and $tool.Length -gt 1) { [void]$svc.Tools.Add($tool) } } }
}

foreach ($full in $allFiles) {
    $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
    $name = [IO.Path]::GetFileName($full)
    $rel = Rel $full
    # Konfiguration → externe Systeme
    if ($name -match '^(appsettings.*\.json|config\.json|settings\.json|launchSettings\.json|application.*\.(ya?ml|properties)|\.env.*)$') {
        try { $ct = [IO.File]::ReadAllText($full) } catch { continue }
        $app = OwnerApp $rel
        foreach ($m in [regex]::Matches($ct, '(?im)["'']?([\w.]*?(?:url|uri|endpoint|baseaddress|host|server))["'']?\s*[:=]\s*["'']?(https?://[^"''\s,}]+)')) {
            $h = ([regex]::Match($m.Groups[2].Value, '^https?://([^/:]+)')).Groups[1].Value -replace '^www\.', ''
            if ($h -and $h -notmatch '^(localhost|127\.0\.0\.1|0\.0\.0\.0|\*|\+)$' -and $app) { $configSystems.Add([pscustomobject]@{ App = $app; System = $h; Key = $m.Groups[1].Value; File = $rel }); [void]$app.Systems.Add($h) }
        }
        continue
    }
    if ($sourceExt -notcontains $ext) { continue }
    $app = OwnerApp $rel
    if (-not $app) { continue }
    try { if ((New-Object IO.FileInfo $full).Length -gt 600000) { continue }; $text = [IO.File]::ReadAllText($full) } catch { continue }
    $app.Files++
    $app.Lang[$ext] = 1 + [int]$app.Lang[$ext]
    # Kommentarzeilen ausblenden (Zeilennummern bleiben gleich)
    $code = [regex]::Replace($text, '(?m)^[ \t]*(//|#(?![\[!])|\*|/\*).*$', '')
    $script:nl = [int[]]@([regex]::Matches($code, "`n") | ForEach-Object { $_.Index })
    $lang = switch -regex ($ext) { '^\.cs$' { 'cs' } '^\.(ts|tsx|js|jsx|mjs|vue|svelte)$' { 'ts' } '^\.py$' { 'py' } '^\.(java|kt|php)$' { 'jv' } '^\.go$' { 'go' } default { '' } }
    # Abschnitte: je Klasse einer; Dateien ohne Klassen (z. B. TS-Module) sind ein Abschnitt mit dem Dateinamen
    $decls = @()
    if ($rxClass.ContainsKey($lang)) { $decls = @([regex]::Matches($code, $rxClass[$lang])) }
    $segs = New-Object System.Collections.Generic.List[object]
    if ($decls.Count -eq 0) { $segs.Add(@(([IO.Path]::GetFileNameWithoutExtension($full) -replace '\..*$', ''), 0, $code.Length)) }
    else {
        for ($i = 0; $i -lt $decls.Count; $i++) {
            $st = if ($i -eq 0) { 0 } else { $decls[$i].Index }
            $en = if ($i + 1 -lt $decls.Count) { $decls[$i + 1].Index } else { $code.Length }
            $segs.Add(@($decls[$i].Groups[1].Value, $st, $en))
        }
    }
    foreach ($sg in $segs) {
        $sname = [string]$sg[0]; $seg = $code.Substring([int]$sg[1], [int]$sg[2] - [int]$sg[1])
        $svc = [pscustomobject]@{ Name = $sname; Lang = $lang; App = $app; File = $rel; Line = (Line ([int]$sg[1] + 1)); Text = $seg; Kind = ''; Uses = (New-Object System.Collections.Generic.HashSet[string]); Tools = (New-Object System.Collections.Generic.HashSet[string]); Systems = (New-Object System.Collections.Generic.HashSet[string]); IsService = ($sname -match $serviceSuffix); NoteId = ''; Extra = (New-Object System.Collections.Generic.List[string]) }
        if ($sname -match 'Controller$') { $svc.Kind = 'Controller' } elseif ($sname -match 'Client$') { $svc.Kind = 'Client' } elseif ($sname -match '(Repository|Store)$') { $svc.Kind = 'Datenzugriff' } elseif ($svc.IsService) { $svc.Kind = 'Service' } else { $svc.Kind = 'Komponente' }
        $before = $calls.Count; $beforeEp = $endpoints.Count
        Analyze-Segment $svc $lang $seg ([int]$sg[1]) $rel
        if ($svc.IsService -or $calls.Count -gt $before -or $endpoints.Count -gt $beforeEp -or $svc.Tools.Count -gt 0) { $services.Add($svc); $app.Services.Add($svc) }
    }
}

# Service ↔ Service (Typname kommt im Code eines anderen Services vor, z. B. Konstruktor-Injektion oder Import)
$byName = @{}
foreach ($s in $services) { if (-not $byName.ContainsKey($s.Name)) { $byName[$s.Name] = $s } }
# Interfaces → Implementierung (class JiraClient : IJiraClient), damit Injektion über Interfaces verkettet wird
$alias = @{}
foreach ($s in $services) {
    $m = [regex]::Match($s.Text, '(?:class|record)\s+' + [regex]::Escape($s.Name) + '\b[^{;]*?(?::|implements)\s*([^{]+)')
    if ($m.Success) { foreach ($i in [regex]::Matches($m.Groups[1].Value, '\bI[A-Z]\w+')) { if (-not $byName.ContainsKey($i.Value) -and -not $alias.ContainsKey($i.Value)) { $alias[$i.Value] = $s.Name } } }
}
$names = @(@($byName.Keys) + @($alias.Keys) | Where-Object { $_.Length -ge 4 } | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape($_) })
if ($names.Count -gt 0) {
    $rxUse = [regex]::new('\b(' + ($names -join '|') + ')\b')
    foreach ($s in $services) {
        foreach ($m in $rxUse.Matches($s.Text)) {
            $target = $m.Value; if ($alias.ContainsKey($target)) { $target = $alias[$target] }
            if ($target -ne $s.Name -and $byName[$target].Lang -eq $s.Lang) { [void]$s.Uses.Add($target) }   # nur innerhalb derselben Sprache
        }
    }
}

# System für relative Aufrufe: Host aus BaseAddress, sonst Name des Clients ohne Suffix (JiraClient → Jira)
function Match-Endpoint($call) {
    $norm = ($call.Path -replace '\{[^}]*\}', '{}' -replace ':\w+', '{}').TrimEnd('/').ToLowerInvariant()
    foreach ($e in $endpoints) {
        $en = ($e.Path -replace '\{[^}]*\}', '{}' -replace ':\w+', '{}').TrimEnd('/').ToLowerInvariant()
        if ($e.Method -eq $call.Method -and ($norm -eq $en -or $norm.EndsWith($en) -and $en.Length -gt 3)) { return $e }
    }
    return $null
}
foreach ($c in $calls) {
    $c | Add-Member -NotePropertyName Endpoint -NotePropertyValue $null
    if (-not $c.System -or $c.System -eq 'localhost') {
        $e = Match-Endpoint $c
        if ($e -and $e.Service -ne $c.Service) { $c.Endpoint = $e; $c.System = '' ; continue }
        if (-not $c.System) {
            if ($c.Service.Systems.Count -gt 0) { $c.System = @($c.Service.Systems)[0] }
            else { $sys = $c.Service.Name -replace '(Http)?(Client|Service|Api|Gateway|Repository|Connector|Proxy)$', ''; if (-not $sys) { $sys = $c.Service.Name }; $c.System = $sys }
        }
    }
    if ($c.System) { [void]$c.Service.Systems.Add($c.System) }
}

# ================================================================ Beschreibungen (KI/Hand) aus dem Repo
$descriptions = @{}
$descFile = Join-Path $Repo '.copit\obsidian\beschreibungen.md'
if (Test-Path -LiteralPath $descFile) {
    $dt = [IO.File]::ReadAllText($descFile)
    foreach ($m in [regex]::Matches($dt, '(?ms)^##\s+(.+?)\s*$\r?\n(.*?)(?=^##\s|\z)')) { $descriptions[$m.Groups[1].Value.Trim().Trim('[', ']')] = $m.Groups[2].Value.Trim() }
}
function Get-Description([string[]]$Keys) { foreach ($k in $Keys) { if ($descriptions.ContainsKey($k)) { return $descriptions[$k] } }; return '' }

# ================================================================ Notiz-IDs
foreach ($a in $apps) { $a.NoteId = Join-NoteId @($archFolder, 'Applikationen', (Get-SafeName $a.Name)) }
$dupe = @($services | Group-Object Name | Where-Object Count -gt 1 | ForEach-Object Name)
foreach ($s in $services) { $n = $s.Name; if ($dupe -contains $n) { $n = "$n ($($s.App.Name))" }; $s.NoteId = Join-NoteId @($archFolder, 'Services', (Get-SafeName $n)) }
foreach ($e in $endpoints) { $e | Add-Member -NotePropertyName NoteId -NotePropertyValue (Join-NoteId @($archFolder, 'Endpunkte', (Get-SafeName $e.Service.App.Name), (Get-SafeName ("$($e.Method) $($e.Path)" -replace '/', ' ∕ ') 120))) -Force }
$callGroups = $calls | Group-Object { "$($_.Method) $($_.Path) @ $($_.System)" }
$callNotes = @{}
foreach ($g in $callGroups) {
    $c0 = $g.Group[0]
    $folderSys = if ($c0.Endpoint) { $c0.Endpoint.Service.App.Name } else { $c0.System }
    $id = Join-NoteId @($archFolder, 'API-Aufrufe', (Get-SafeName $folderSys), (Get-SafeName ("$($c0.Method) $($c0.Path)" -replace '/', ' ∕ ') 120))
    $callNotes[$g.Name] = $id
    foreach ($c in $g.Group) { $c | Add-Member -NotePropertyName NoteId -NotePropertyValue $id -Force }
}
function SvcLink($s) { return (Get-Link $s.NoteId $s.Name) }
function AppLink($a) { return (Get-Link $a.NoteId $a.Name) }
function SysLink([string]$s) { return (Get-Link (Get-SystemNoteId $vault $s) $s) }
function ToolLink([string]$t) { return (Get-Link (Get-ToolNoteId $vault $t) $t) }
function Tbl([string]$Row) { return [regex]::Replace($Row, '\[\[[^\]]*\]\]', { param($m) $m.Value -replace '\|', '\|' }) }
function CallTitle($c) { return "$($c.Method) $($c.Path)" }
$tagBase = @('copit', 'copit/architektur')
$written = New-Object System.Collections.Generic.HashSet[string]
$index = @{}
function Index([string]$File, [string]$NoteId) { if (-not $index.ContainsKey($File)) { $index[$File] = New-Object System.Collections.Generic.List[string] }; if (-not $index[$File].Contains($NoteId)) { $index[$File].Add($NoteId) } }
$changed = 0
function Save($id, $front, $blocks, $title, $intro = '') { [void]$written.Add($id); if (Write-Note $vault $id $front $blocks $title $intro) { $script:changed++ } }

# Zusätzliche Beziehungen aus Beschreibungen ("Nutzt: A, B")
function Apply-Extra($s, [string]$desc) {
    foreach ($m in [regex]::Matches($desc, '(?im)^(?:Nutzt|Ruft auf|Verwendet|Abhängig von)\s*:\s*(.+)$')) {
        foreach ($n in ($m.Groups[1].Value -split ',')) { $n = $n.Trim().Trim('[', ']'); if ($n) { $s.Extra.Add($n) } }
    }
}
function Resolve-Extra([string]$n, $svc) {
    if ($byName.ContainsKey($n)) { return (SvcLink $byName[$n]) }
    $a = $apps | Where-Object { $_.Name -eq $n } | Select-Object -First 1
    if ($a) { return (AppLink $a) }
    [void]$svc.Systems.Add($n)   # unbekannter Name = externes System → bekommt eigene Notiz
    return (SysLink $n)
}

# ================================================================ Services
$usedBy = @{}
foreach ($s in $services) { foreach ($u in $s.Uses) { if (-not $usedBy.ContainsKey($u)) { $usedBy[$u] = New-Object System.Collections.Generic.List[object] }; $usedBy[$u].Add($s) } }
foreach ($s in $services) {
    $desc = Get-Description @(($s.NoteId.Substring($archFolder.Length + 1)), $s.Name, "service/$($s.Name)")
    Apply-Extra $s $desc
    $myCalls = @($calls | Where-Object { $_.Service -eq $s })
    $myEps = @($endpoints | Where-Object { $_.Service -eq $s })
    $users = @(); if ($usedBy.ContainsKey($s.Name)) { $users = $usedBy[$s.Name].ToArray() }
    $front = [ordered]@{
        type      = 'service'
        kind      = $s.Kind
        project   = $projectLink
        app       = (AppLink $s.App)
        file      = $s.File
        uses      = @(@($s.Uses | Sort-Object | ForEach-Object { SvcLink $byName[$_] }) + @($s.Extra | ForEach-Object { Resolve-Extra $_ $s }) | Select-Object -Unique)
        used_by   = @($users | Sort-Object Name | ForEach-Object { SvcLink $_ } | Select-Object -Unique)
        calls     = @($myCalls | ForEach-Object { Get-Link $_.NoteId (CallTitle $_) } | Select-Object -Unique)
        endpoints = @($myEps | ForEach-Object { Get-Link $_.NoteId "$($_.Method) $($_.Path)" } | Select-Object -Unique)
        systems   = @($s.Systems | Sort-Object | ForEach-Object { SysLink $_ })
        tools     = @($s.Tools | Sort-Object | ForEach-Object { ToolLink $_ })
        tags      = $tagBase + @('copit/service', ('copit/service/' + $s.Kind.ToLowerInvariant()))
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("**$($s.Kind)** in $(AppLink $s.App) · Projekt $projectLink · Quelle: [$($s.File)]($(FileUrl $s.File))")
    if ($front.uses.Count) { [void]$sb.AppendLine("`n### Nutzt`n" + (($front.uses | ForEach-Object { "- $_" }) -join "`n")) }
    if ($front.used_by.Count) { [void]$sb.AppendLine("`n### Genutzt von`n" + (($front.used_by | ForEach-Object { "- $_" }) -join "`n")) }
    if ($myCalls.Count) {
        [void]$sb.AppendLine("`n### API-Aufrufe`n| Methode | Pfad | Ziel | Zeile |`n|---|---|---|---|")
        foreach ($c in ($myCalls | Sort-Object System, Path)) {
            $target = if ($c.Endpoint) { Get-Link $c.Endpoint.NoteId "$($c.Endpoint.Method) $($c.Endpoint.Path)" } else { SysLink $c.System }
            [void]$sb.AppendLine((Tbl "| $($c.Method) | $(Get-Link $c.NoteId ($c.Path -replace '\|', '/')) | $target | [$($c.Line)]($(FileUrl $c.File)) |"))
        }
    }
    if ($myEps.Count) { [void]$sb.AppendLine("`n### Stellt bereit`n" + (($myEps | ForEach-Object { "- $(Get-Link $_.NoteId "$($_.Method) $($_.Path)")" }) -join "`n")) }
    if ($s.Tools.Count) { [void]$sb.AppendLine("`n### Startet`n" + (($front.tools | ForEach-Object { "- $_" }) -join "`n")) }
    # lokaler Graph
    $mm = New-Object System.Collections.Generic.List[string]
    $mm.Add('```mermaid'); $mm.Add('flowchart LR'); $mm.Add("  S[""$($s.Name)""]")
    $i = 0
    foreach ($u in $users) { $i++; $mm.Add("  U$i[""$($u.Name)""] --> S") }
    foreach ($u in $s.Uses) { $i++; $mm.Add("  S --> N$i[""$u""]") }
    foreach ($y in $s.Systems) { $i++; $mm.Add("  S -->|API| Y$i[(""$y"")]") }
    foreach ($t in $s.Tools) { $i++; $mm.Add("  S -->|startet| T$i{{""$t""}}") }
    $mm.Add('```')
    if ($i -gt 0) { [void]$sb.AppendLine("`n### Verkettung`n" + ($mm -join "`n")) }
    $blocks = [ordered]@{ arch = $sb.ToString() }
    if ($desc) { $blocks['beschreibung'] = "## Beschreibung`n$desc" }
    Save $s.NoteId $front $blocks $s.Name
    Index $s.File $s.NoteId
}

# ================================================================ Endpunkte
foreach ($e in $endpoints) {
    $callers = @($calls | Where-Object { $_.Endpoint -eq $e })
    $front = [ordered]@{ type = 'endpunkt'; method = $e.Method; path = $e.Path; project = $projectLink; app = (AppLink $e.Service.App); service = (SvcLink $e.Service); called_by = @($callers | ForEach-Object { SvcLink $_.Service } | Select-Object -Unique); tags = $tagBase + @('copit/endpunkt') }
    $body = "``$($e.Method) $($e.Path)`` – bereitgestellt von $(SvcLink $e.Service) in $(AppLink $e.Service.App) ([$($e.File):$($e.Line)]($(FileUrl $e.File)))"
    if ($callers.Count) { $body += "`n`n### Aufgerufen von`n" + (($callers | ForEach-Object { "- $(SvcLink $_.Service) über $(Get-Link $_.NoteId (CallTitle $_))" } | Select-Object -Unique) -join "`n") }
    Save $e.NoteId $front ([ordered]@{ arch = $body }) "$($e.Method) $($e.Path)"
}

# ================================================================ API-Aufrufe
foreach ($g in $callGroups) {
    $c0 = $g.Group[0]
    $front = [ordered]@{ type = 'api-aufruf'; method = $c0.Method; path = $c0.Path; project = $projectLink; callers = @($g.Group | ForEach-Object { SvcLink $_.Service } | Select-Object -Unique); tags = $tagBase + @('copit/api-aufruf') }
    if ($c0.Endpoint) { $front['endpoint'] = Get-Link $c0.Endpoint.NoteId "$($c0.Endpoint.Method) $($c0.Endpoint.Path)"; $front['app'] = AppLink $c0.Endpoint.Service.App; $target = "intern → $($front.endpoint) in $($front.app)" }
    else { $front['system'] = SysLink $c0.System; $target = "an $($front.system)" }
    $body = "``$($c0.Method) $($c0.Path)`` $target`n`n### Aufgerufen in`n" + (($g.Group | ForEach-Object { "- $(SvcLink $_.Service) · [$($_.File):$($_.Line)]($(FileUrl $_.File))" }) -join "`n")
    Save $c0.NoteId $front ([ordered]@{ arch = $body }) (CallTitle $c0)
}

# ================================================================ Applikationen
foreach ($a in $apps) {
    if ($a.Files -eq 0 -and $a.DependsOn.Count -eq 0) { continue }
    $langTop = ($a.Lang.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1)
    $lang = ''; if ($langTop) { $lang = switch ($langTop.Key) { '.cs' { 'C#' } { $_ -in '.ts', '.tsx' } { 'TypeScript' } { $_ -in '.js', '.mjs', '.jsx' } { 'JavaScript' } '.py' { 'Python' } '.go' { 'Go' } '.java' { 'Java' } '.kt' { 'Kotlin' } '.php' { 'PHP' } default { $langTop.Key.TrimStart('.') } } }
    $sys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($x in $a.Systems) { [void]$sys.Add($x) }
    foreach ($s in $a.Services) { foreach ($x in $s.Systems) { [void]$sys.Add($x) } }
    $tools = @($a.Services | ForEach-Object { $_.Tools } | Select-Object -Unique)
    $usedByApps = @($apps | Where-Object { $_.DependsOn.Contains($a.Name) })
    $desc = Get-Description @(($a.NoteId.Substring($archFolder.Length + 1)), $a.Name, "app/$($a.Name)")
    $front = [ordered]@{
        type = 'applikation'; kind = $a.Kind; language = $lang; path = $(if ($a.Path) { $a.Path } else { '/' }); project = $projectLink
        depends_on = @($a.DependsOn | Sort-Object | ForEach-Object { $n = $_; AppLink ($apps | Where-Object Name -eq $n | Select-Object -First 1) })
        used_by = @($usedByApps | ForEach-Object { AppLink $_ })
        services = @($a.Services | Sort-Object Name | ForEach-Object { SvcLink $_ })
        systems = @($sys | Sort-Object | ForEach-Object { SysLink $_ })
        tools = @($tools | Sort-Object | ForEach-Object { ToolLink $_ })
        files = $a.Files
        tags = $tagBase + @('copit/applikation')
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("**$($a.Kind)**$(if ($lang) { " · $lang" }) · $($a.Files) Quelldateien · Pfad ``$($front.path)`` · Projekt $projectLink")
    if ($front.depends_on.Count) { [void]$sb.AppendLine("`n### Hängt ab von`n" + (($front.depends_on | ForEach-Object { "- $_" }) -join "`n")) }
    if ($front.used_by.Count) { [void]$sb.AppendLine("`n### Genutzt von`n" + (($front.used_by | ForEach-Object { "- $_" }) -join "`n")) }
    if ($a.Services.Count) {
        [void]$sb.AppendLine("`n### Services`n| Service | Art | Nutzt | API-Aufrufe | Endpunkte |`n|---|---|---|---|---|")
        foreach ($s in ($a.Services | Sort-Object Name)) { [void]$sb.AppendLine((Tbl "| $(SvcLink $s) | $($s.Kind) | $($s.Uses.Count) | $(@($calls | Where-Object Service -eq $s).Count) | $(@($endpoints | Where-Object Service -eq $s).Count) |")) }
    }
    if ($sys.Count) { [void]$sb.AppendLine("`n### Externe Systeme`n" + (($front.systems | ForEach-Object { "- $_" }) -join "`n")) }
    $cfg = @($configSystems | Where-Object App -eq $a)
    if ($cfg.Count) { [void]$sb.AppendLine("`n### Konfiguration`n" + (($cfg | ForEach-Object { "- ``$($_.Key)`` → $(SysLink $_.System) ($($_.File))" }) -join "`n")) }
    if ($a.Packages.Count) { [void]$sb.AppendLine("`n### Pakete`n" + (($a.Packages | Select-Object -Unique | Select-Object -First 30 | ForEach-Object { "``$_``" }) -join ' · ')) }
    $blocks = [ordered]@{ arch = $sb.ToString() }
    if ($desc) { $blocks['beschreibung'] = "## Beschreibung`n$desc" }
    Save $a.NoteId $front $blocks $a.Name
}

# ================================================================ Systeme & Werkzeuge (projektübergreifend, eigener Block je Projekt)
$allSystems = New-Object System.Collections.Generic.HashSet[string]
foreach ($s in $services) { foreach ($x in $s.Systems) { [void]$allSystems.Add($x) } }
foreach ($x in $configSystems) { [void]$allSystems.Add($x.System) }
foreach ($sysName in $allSystems) {
    $id = Get-SystemNoteId $vault $sysName
    $users = @($services | Where-Object { $_.Systems.Contains($sysName) })
    $myCalls = @($calls | Where-Object { $_.System -eq $sysName })
    $body = "### $projectLink`n"
    if ($users.Count) { $body += "Genutzt von: " + (($users | ForEach-Object { SvcLink $_ }) -join ', ') + "`n" }
    $cfg = @($configSystems | Where-Object System -eq $sysName)
    if ($cfg.Count) { $body += "Konfiguriert in: " + (($cfg | ForEach-Object { "$(AppLink $_.App) (``$($_.Key)``)" } | Select-Object -Unique) -join ', ') + "`n" }
    if ($myCalls.Count) { $body += "`n| Methode | Pfad | Service |`n|---|---|---|`n" + (($myCalls | Sort-Object Path | ForEach-Object { Tbl "| $($_.Method) | $(Get-Link $_.NoteId ($_.Path -replace '\|', '/')) | $(SvcLink $_.Service) |" } | Select-Object -Unique) -join "`n") }
    $front = [ordered]@{ type = 'system'; tags = @('copit', 'copit/system'); projects = @($projectLink) }
    Save $id $front ([ordered]@{ $blockKey = $body }) $sysName 'Externes System / API. Jedes Projekt, das es nutzt, hat unten einen eigenen Abschnitt.'
}
$allTools = @($services | ForEach-Object { $_.Tools } | Select-Object -Unique)
foreach ($t in $allTools) {
    $id = Get-ToolNoteId $vault $t
    $users = @($services | Where-Object { $_.Tools.Contains($t) })
    $body = "### $projectLink`nGestartet von: " + (($users | ForEach-Object { SvcLink $_ }) -join ', ')
    Save $id ([ordered]@{ type = 'werkzeug'; tags = @('copit', 'copit/werkzeug'); projects = @($projectLink) }) ([ordered]@{ $blockKey = $body }) $t 'Externe Anwendung / CLI. Jedes Projekt, das sie startet, hat unten einen eigenen Abschnitt.'
}

# ================================================================ Übersicht (MOC) mit Mermaid
$archId = Get-ArchNoteId $vault $project
$liveApps = @($apps | Where-Object { $_.Files -gt 0 -or $_.DependsOn.Count -gt 0 })
function Mid([string]$s) { return ('n' + (Get-StableId $s)) }
$mm = New-Object System.Collections.Generic.List[string]
$mm.Add('```mermaid'); $mm.Add('flowchart LR')
$detail = $services.Count -le 45
foreach ($a in $liveApps) {
    if ($detail -and $a.Services.Count) {
        $mm.Add("  subgraph $(Mid ('app' + $a.Name))[""$($a.Name)""]")
        foreach ($s in $a.Services) { $mm.Add("    $(Mid ('svc' + $s.NoteId))[""$($s.Name)""]") }
        $mm.Add('  end')
    } else { $mm.Add("  $(Mid ('app' + $a.Name))[[""$($a.Name)""]]") }
}
foreach ($a in $liveApps) { foreach ($d in $a.DependsOn) { $mm.Add("  $(Mid ('app' + $a.Name)) -.-> $(Mid ('app' + $d))") } }
foreach ($x in $allSystems) { $mm.Add("  $(Mid ('sys' + $x))[(""$x"")]") }
foreach ($t in $allTools) { $mm.Add("  $(Mid ('tool' + $t)){{""$t""}}") }
foreach ($s in $services) {
    $from = if ($detail) { Mid ('svc' + $s.NoteId) } else { Mid ('app' + $s.App.Name) }
    if ($detail) { foreach ($u in $s.Uses) { $mm.Add("  $from --> $(Mid ('svc' + $byName[$u].NoteId))") } }
    foreach ($x in $s.Systems) { $mm.Add("  $from -->|API| $(Mid ('sys' + $x))") }
    foreach ($t in $s.Tools) { $mm.Add("  $from -->|startet| $(Mid ('tool' + $t))") }
}
foreach ($c in ($calls | Where-Object Endpoint)) { $from = if ($detail) { Mid ('svc' + $c.Service.NoteId) } else { Mid ('app' + $c.Service.App.Name) }; $to = if ($detail) { Mid ('svc' + $c.Endpoint.Service.NoteId) } else { Mid ('app' + $c.Endpoint.Service.App.Name) }; $mm.Add("  $from ==>|$($c.Method)| $to") }
$mm.Add('```')
$seen = New-Object System.Collections.Generic.HashSet[string]
$mermaid = (($mm | Where-Object { $_ -match '^\s*(end|subgraph|```)' -or $seen.Add($_) }) -join "`n")

$canvasRel = "$archFolder/$projSafe – Architektur.canvas"
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("Projekt $projectLink · Repo ``$Repo`` · Stand $([datetime]::Now.ToString('dd.MM.yyyy HH:mm'))")
[void]$sb.AppendLine("`n| Applikationen | Services | API-Aufrufe | Endpunkte | Systeme | Werkzeuge |`n|---|---|---|---|---|---|")
[void]$sb.AppendLine("| $($liveApps.Count) | $($services.Count) | $(@($callGroups).Count) | $($endpoints.Count) | $($allSystems.Count) | $($allTools.Count) |")
[void]$sb.AppendLine("`nVisuell: [[$canvasRel|Canvas öffnen]] · Graph: Filter ``path:`"$archFolder`"``")
[void]$sb.AppendLine("`n### Verkettung`n$mermaid")
[void]$sb.AppendLine("`n### Applikationen")
foreach ($a in $liveApps) { [void]$sb.AppendLine("- $(AppLink $a) – $($a.Kind), $($a.Services.Count) Services$(if ($a.DependsOn.Count) { ' · nutzt ' + (($a.DependsOn | ForEach-Object { $n = $_; AppLink ($apps | Where-Object Name -eq $n | Select-Object -First 1) }) -join ', ') })") }
if ($allSystems.Count) { [void]$sb.AppendLine("`n### Externe Systeme`n" + (($allSystems | Sort-Object | ForEach-Object { $n = $_; "- $(SysLink $n) ← " + ((@($services | Where-Object { $_.Systems.Contains($n) }) | ForEach-Object { SvcLink $_ }) -join ', ') }) -join "`n")) }
if ($allTools.Count) { [void]$sb.AppendLine("`n### Werkzeuge`n" + (($allTools | Sort-Object | ForEach-Object { $n = $_; "- $(ToolLink $n) ← " + ((@($services | Where-Object { $_.Tools.Contains($n) }) | ForEach-Object { SvcLink $_ }) -join ', ') }) -join "`n")) }
$mocFront = [ordered]@{
    type = 'architektur'; project = $projectLink; repo = $Repo; updated = [datetime]::Now.ToString('yyyy-MM-ddTHH:mm:ss')
    apps = @($liveApps | ForEach-Object { AppLink $_ }); systems = @($allSystems | Sort-Object | ForEach-Object { SysLink $_ }); tools = @($allTools | Sort-Object | ForEach-Object { ToolLink $_ })
    tags = $tagBase + @('copit/architektur/uebersicht')
}
Save $archId $mocFront ([ordered]@{ uebersicht = $sb.ToString() }) "$project – Architektur"

# ================================================================ Canvas
$nodes = New-Object System.Collections.Generic.List[object]; $edges = New-Object System.Collections.Generic.List[object]
$nodeIds = @{}
$edgeIds = New-Object System.Collections.Generic.HashSet[string]
function Node([string]$Key, [string]$NoteId, [int]$X, [int]$Y, [string]$Color = '') { if ($nodeIds.ContainsKey($Key)) { return }; $id = Get-StableId $Key; $nodeIds[$Key] = $id; $n = [ordered]@{ id = $id; type = 'file'; file = "$NoteId.md"; x = $X; y = $Y; width = 360; height = 70 }; if ($Color) { $n.color = $Color }; $nodes.Add([pscustomobject]$n) }
function Edge([string]$From, [string]$To, [string]$Label, [string]$Color = '') { if (-not ($nodeIds.ContainsKey($From) -and $nodeIds.ContainsKey($To))) { return }; $id = Get-StableId "$From>$To"; if (-not $edgeIds.Add($id)) { return }; $e = [ordered]@{ id = $id; fromNode = $nodeIds[$From]; fromSide = 'right'; toNode = $nodeIds[$To]; toSide = 'left' }; if ($Label) { $e.label = $Label }; if ($Color) { $e.color = $Color }; $edges.Add([pscustomobject]$e) }
$y = 0; $ys = 0
foreach ($a in $liveApps) {
    $top = $ys
    foreach ($s in ($a.Services | Sort-Object Name)) { Node ('svc' + $s.NoteId) $s.NoteId 520 $ys '4'; $ys += 100 }
    $ay = [Math]::Max($top, [int](($top + $ys) / 2) - 50); if ($a.Services.Count -eq 0) { $ay = $ys; $ys += 100 }
    Node ('app' + $a.Name) $a.NoteId 0 $ay '6'
}
$yr = 0
foreach ($x in ($allSystems | Sort-Object)) { Node ('sys' + $x) (Get-SystemNoteId $vault $x) 1080 $yr '1'; $yr += 100 }
foreach ($t in ($allTools | Sort-Object)) { Node ('tool' + $t) (Get-ToolNoteId $vault $t) 1080 $yr '3'; $yr += 100 }
foreach ($a in $liveApps) { foreach ($s in $a.Services) { Edge ('app' + $a.Name) ('svc' + $s.NoteId) '' } }
foreach ($s in $services) {
    foreach ($x in $s.Systems) { Edge ('svc' + $s.NoteId) ('sys' + $x) 'API' '1' }
    foreach ($t in $s.Tools) { Edge ('svc' + $s.NoteId) ('tool' + $t) 'startet' '3' }
}
foreach ($c in ($calls | Where-Object Endpoint)) { Edge ('svc' + $c.Service.NoteId) ('svc' + $c.Endpoint.Service.NoteId) $c.Method '2' }
$canvasFile = Join-Path $vault.Path ($canvasRel -replace '/', $sep)
$canvasJson = ([ordered]@{ nodes = $nodes.ToArray(); edges = $edges.ToArray() } | ConvertTo-Json -Depth 5)
[IO.File]::WriteAllText($canvasFile, $canvasJson, (New-Object System.Text.UTF8Encoding $false))

# ================================================================ Index & veraltete Notizen
$idxObj = [ordered]@{ project = $project; repo = $Repo; updated = [datetime]::Now.ToString('s'); files = @{} }
foreach ($k in $index.Keys) { $app = OwnerApp $k; $list = $index[$k].ToArray(); if ($app) { $list += $app.NoteId }; $idxObj.files[$k] = $list }
[IO.File]::WriteAllText((Join-Path $vault.Path (($archFolder + '/copit-index.json') -replace '/', $sep)), ($idxObj | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))

$stale = 0
$archDir = Join-Path $vault.Path ($archFolder -replace '/', $sep)
foreach ($f in [IO.Directory]::EnumerateFiles($archDir, '*.md', 'AllDirectories')) {
    $rel = $f.Substring($vault.Path.TrimEnd('\', '/').Length + 1) -replace '\\', '/'
    $id = $rel.Substring(0, $rel.Length - 3)
    if ($written.Contains($id)) { continue }
    $parts = Split-Note ([IO.File]::ReadAllText($f))
    if (-not $parts.Entries.Contains('type')) { continue }   # eigene Notizen nicht anfassen
    [void](Write-Note $vault $id ([ordered]@{ veraltet = $true; tags = @('copit/veraltet') }) ([ordered]@{}) '')
    $stale++
}

# Projekt-Notiz verlinkt die Architektur
[void](Write-Note $vault $projectId ([ordered]@{ type = 'project'; project = $project; repo = $Repo; architecture = (Get-Link $archId 'Architektur'); tags = @('copit', 'copit/projekt') }) ([ordered]@{ architektur = "### Architektur`n$(Get-Link $archId "$project – Architektur") · $($liveApps.Count) Applikationen, $($services.Count) Services, $($allSystems.Count) Systeme" }) $project)

Write-Output ("Architektur {0}: {1} Apps, {2} Services, {3} API-Aufrufe, {4} Endpunkte, {5} Systeme, {6} Werkzeuge – {7} Notizen geändert{8}" -f $project, $liveApps.Count, $services.Count, @($callGroups).Count, $endpoints.Count, $allSystems.Count, $allTools.Count, $changed, $(if ($stale) { ", $stale veraltet" } else { '' }))
