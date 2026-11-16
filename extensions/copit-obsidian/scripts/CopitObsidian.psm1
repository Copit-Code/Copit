# Gemeinsame Funktionen der Copit-Obsidian-Extension (Windows PowerShell 5.1 kompatibel).
# Notizen bestehen aus: Frontmatter (von Copit gepflegte Keys + eigene Keys bleiben erhalten),
# generierten Blöcken zwischen %% copit:<key>:start %% … %% copit:<key>:end %% und frei editierbarem Rest.

Set-StrictMode -Version 2
$script:Utf8 = New-Object System.Text.UTF8Encoding $false
$script:UnionKeys = @('tags', 'aliases', 'projects')

# ------------------------------------------------------------------ Eingaben von Copit

function Get-CopitValue([string]$Name, [string]$Default = '') {
    $key = ($Name.ToUpperInvariant() -replace '[^A-Z0-9]', '_')
    foreach ($prefix in 'COPIT_PARAM_', 'COPIT_VAR_') {
        $v = [Environment]::GetEnvironmentVariable($prefix + $key)
        if ($v -and $v.Trim().Length -gt 0) { return $v.Trim() }
    }
    return $Default
}

function Get-CopitHome {
    if ($env:COPIT_HOME) { return $env:COPIT_HOME }
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    return (Join-Path $local 'Copit')
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json) } catch { return $null }
}

function Get-Prop($Obj, [string]$Name, $Default = $null) {
    if ($null -eq $Obj) { return $Default }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value) { return $Default }
    return $p.Value
}

function Get-CopitProjects {
    $d = Read-JsonFile (Join-Path (Get-CopitHome) 'projects.json')
    $list = Get-Prop $d 'Projects' @()
    return @($list)
}

function Get-CopitSessions {
    $dir = Join-Path (Get-CopitHome) 'sessions'
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $out = New-Object System.Collections.ArrayList
    foreach ($f in [IO.Directory]::EnumerateFiles($dir, '*.json')) {
        $s = Read-JsonFile $f
        if ($s) { [void]$out.Add($s) }
    }
    return @($out)
}

function Resolve-ProjectName([string]$Repo, [string]$ProjectId = '') {
    $name = Get-CopitValue 'project'
    if ($name) { return $name }
    $projects = Get-CopitProjects
    if ($ProjectId) {
        $p = $projects | Where-Object { $_.Id -eq $ProjectId } | Select-Object -First 1
        if ($p) { return $p.Name }
    }
    if ($Repo) {
        $norm = $Repo.TrimEnd('\', '/')
        $p = $projects | Where-Object { $_.SourceFolder -and ($_.SourceFolder.TrimEnd('\', '/') -ieq $norm) } | Select-Object -First 1
        if ($p) { return $p.Name }
        return (Split-Path $norm -Leaf)
    }
    return 'Allgemein'
}

# ------------------------------------------------------------------ Vault

# Reihenfolge: Parameter "vault" → COPIT_OBSIDIAN_VAULT → %LOCALAPPDATA%\Copit\obsidian.json → zuletzt geöffneter Vault der Obsidian-App.
function Resolve-Vault {
    $vault = Get-CopitValue 'vault'
    $folder = Get-CopitValue 'folder'
    $cfg = Read-JsonFile (Join-Path (Get-CopitHome) 'obsidian.json')
    if (-not $vault -and $env:COPIT_OBSIDIAN_VAULT) { $vault = $env:COPIT_OBSIDIAN_VAULT }
    if (-not $vault) { $vault = Get-Prop $cfg 'vault' '' }
    if (-not $folder) { $folder = Get-Prop $cfg 'folder' '' }
    if (-not $vault) {
        $appData = [Environment]::GetFolderPath('ApplicationData')
        $obs = Read-JsonFile (Join-Path $appData 'obsidian\obsidian.json')
        $vaults = Get-Prop $obs 'vaults'
        if ($vaults) {
            $cands = foreach ($p in $vaults.PSObject.Properties) {
                [pscustomobject]@{ Path = (Get-Prop $p.Value 'path' ''); Open = [bool](Get-Prop $p.Value 'open' $false); Ts = [long](Get-Prop $p.Value 'ts' 0) }
            }
            $best = $cands | Where-Object { $_.Path -and (Test-Path -LiteralPath $_.Path) } | Sort-Object @{ e = { $_.Open }; Descending = $true }, @{ e = { $_.Ts }; Descending = $true } | Select-Object -First 1
            if ($best) { $vault = $best.Path }
        }
    }
    if (-not $vault) { throw 'Kein Obsidian-Vault gefunden. Parameter "vault" setzen, Aktion "Vault festlegen" ausführen oder Obsidian einmal öffnen.' }
    if (-not (Test-Path -LiteralPath $vault)) { throw "Vault-Ordner existiert nicht: $vault" }
    if (-not $folder) { $folder = 'Copit' }
    return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $vault).Path; Root = $folder.Trim('\', '/') }
}

# ------------------------------------------------------------------ Namen & Links

function Get-SafeName([string]$Text, [int]$Max = 90) {
    $s = ($Text -replace '[\\/:*?"<>|#^\[\]]', '-' -replace '\s+', ' ' -replace '-{2,}', '-').Trim(' ', '.', '-')
    if ($s.Length -gt $Max) { $s = $s.Substring(0, $Max).Trim(' ', '.', '-') }
    if (-not $s) { $s = 'Unbenannt' }
    return $s
}

# Vault-relativer Notizpfad ohne .md, immer mit '/'.
# Stabile Kurz-ID (gleich über Läufe hinweg, anders als String.GetHashCode in .NET Core).
function Get-StableId([string]$Text) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
    return (($bytes[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Join-NoteId([string[]]$Parts) {
    return (($Parts | Where-Object { $_ } | ForEach-Object { $_.Trim('/', '\') }) -join '/')
}

function Get-Link([string]$NoteId, [string]$Alias = '') {
    if (-not $Alias) { $Alias = ($NoteId -split '/')[-1] }
    $Alias = $Alias -replace '[|\[\]]', '/' -replace '\s+', ' '
    return "[[$NoteId|$Alias]]"
}

function Get-NoteFile($Vault, [string]$NoteId) {
    return (Join-Path $Vault.Path (($NoteId -replace '/', [IO.Path]::DirectorySeparatorChar) + '.md'))
}

# ------------------------------------------------------------------ YAML-Frontmatter

function ConvertTo-YamlScalar($v) {
    if ($null -eq $v) { return '""' }
    if ($v -is [bool]) { if ($v) { return 'true' } else { return 'false' } }
    if ($v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal]) { return ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $v)) }
    $s = [string]$v -replace '\\', '\\' -replace '"', '\"' -replace "`r?`n", ' '
    return '"' + $s + '"'
}

function ConvertTo-YamlEntry([string]$Key, $Value) {
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $items = @($Value | Where-Object { $null -ne $_ -and "$_" -ne '' })
        if ($items.Count -eq 0) { return "${Key}: []" }
        $lines = @("${Key}:") + ($items | ForEach-Object { '  - ' + (ConvertTo-YamlScalar $_) })
        return ($lines -join "`n")
    }
    return "${Key}: " + (ConvertTo-YamlScalar $Value)
}

# Zerlegt Text in Frontmatter-Einträge (Key → Rohzeilen) und Rumpf.
function Split-Note([string]$Text) {
    $entries = [ordered]@{}
    $body = $Text
    if ($Text -match '^﻿?---\r?\n') {
        $m = [regex]::Match($Text, '^﻿?---\r?\n([\s\S]*?)\r?\n---[ \t]*(\r?\n|$)')
        if ($m.Success) {
            $body = $Text.Substring($m.Length)
            $current = $null
            foreach ($line in ($m.Groups[1].Value -split '\r?\n')) {
                $km = [regex]::Match($line, '^([^\s#\-][^:]*):(.*)$')
                if ($km.Success) { $current = $km.Groups[1].Value.Trim(); $entries[$current] = @($line) }
                elseif ($current) { $entries[$current] = @($entries[$current]) + $line }
            }
        }
    }
    return [pscustomobject]@{ Entries = $entries; Body = $body }
}

function Get-YamlListValues([string[]]$Lines) {
    $vals = @()
    $first = $Lines[0] -replace '^[^:]*:\s*', ''
    if ($first -match '^\[(.*)\]$') { $vals += ($Matches[1] -split ',') }
    elseif ($first.Trim()) { $vals += $first }
    foreach ($l in ($Lines | Select-Object -Skip 1)) { if ($l -match '^\s*-\s*(.*)$') { $vals += $Matches[1] } }
    return @($vals | ForEach-Object { $_.Trim().Trim('"', "'") } | Where-Object { $_ })
}

function Merge-Frontmatter($Entries, $Front) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($k in $Front.Keys) {
        $v = $Front[$k]
        if ($script:UnionKeys -contains $k -and $Entries.Contains($k)) {
            $old = Get-YamlListValues $Entries[$k]
            $v = @(@($v) + $old | Select-Object -Unique)
        }
        [void]$lines.Add((ConvertTo-YamlEntry $k $v))
    }
    foreach ($k in $Entries.Keys) { if (-not $Front.Contains($k)) { foreach ($l in $Entries[$k]) { [void]$lines.Add($l) } } }
    return ($lines -join "`n")
}

# ------------------------------------------------------------------ Generierte Blöcke

function Set-Block([string]$Body, [string]$Key, [string]$Content) {
    $start = "%% copit:${Key}:start – von Copit erzeugt, wird bei jedem Lauf ersetzt %%"
    $end = "%% copit:${Key}:end %%"
    $block = "$start`n$($Content.TrimEnd())`n$end"
    $rx = [regex]::new('%% copit:' + [regex]::Escape($Key) + ':start[^\n]*%%[\s\S]*?%% copit:' + [regex]::Escape($Key) + ':end %%')
    if ($rx.IsMatch($Body)) { return $rx.Replace($Body, { param($m) $block }, 1) }
    # Neuer Block: vor "## Notizen" einfügen, sonst anhängen
    $notes = [regex]::Match($Body, '(?m)^## Notizen\s*$')
    if ($notes.Success) { return $Body.Substring(0, $notes.Index) + $block + "`n`n" + $Body.Substring($notes.Index) }
    return $Body.TrimEnd() + "`n`n" + $block + "`n"
}

function Get-Block([string]$Body, [string]$Key) {
    $m = [regex]::Match($Body, '%% copit:' + [regex]::Escape($Key) + ':start[^\n]*%%\r?\n([\s\S]*?)\r?\n%% copit:' + [regex]::Escape($Key) + ':end %%')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

# Schreibt/aktualisiert eine Notiz. $Blocks: geordnete Hashtable Key → Markdown.
# Neue Notizen bekommen Überschrift, Blöcke und einen freien Abschnitt "## Notizen".
function Write-Note($Vault, [string]$NoteId, $Front, $Blocks, [string]$Title = '', [string]$Intro = '') {
    $file = Get-NoteFile $Vault $NoteId
    $dir = Split-Path $file -Parent
    if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    if (Test-Path -LiteralPath $file) {
        $parts = Split-Note ([IO.File]::ReadAllText($file))
        $body = $parts.Body
        $entries = $parts.Entries
    } else {
        if (-not $Title) { $Title = ($NoteId -split '/')[-1] }
        $body = "# $Title`n`n"
        if ($Intro) { $body += "$Intro`n`n" }
        $body += "## Notizen`n`n"
        $entries = [ordered]@{}
    }
    foreach ($k in $Blocks.Keys) { $body = Set-Block $body $k ([string]$Blocks[$k]) }
    $text = "---`n" + (Merge-Frontmatter $entries $Front) + "`n---`n" + $body.TrimStart("`r", "`n")
    $text = $text -replace "`r`n", "`n"
    if ((Test-Path -LiteralPath $file) -and ([IO.File]::ReadAllText($file) -replace "`r`n", "`n") -eq $text) { return $false }
    [IO.File]::WriteAllText($file, $text, $script:Utf8)
    return $true
}

function Read-NoteFront($Vault, [string]$NoteId) {
    $file = Get-NoteFile $Vault $NoteId
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    return (Split-Note ([IO.File]::ReadAllText($file)))
}

# ------------------------------------------------------------------ Gemeinsame Knoten (projektübergreifend)

function Get-ToolNoteId($Vault, [string]$Tool) { return (Join-NoteId @($Vault.Root, 'Werkzeuge', (Get-SafeName $Tool))) }
function Get-SystemNoteId($Vault, [string]$System) { return (Join-NoteId @($Vault.Root, 'Systeme', (Get-SafeName $System))) }
function Get-ProjectNoteId($Vault, [string]$Project) { return (Join-NoteId @($Vault.Root, 'Projekte', (Get-SafeName $Project))) }
function Get-TicketNoteId($Vault, [string]$Ticket) { return (Join-NoteId @($Vault.Root, 'Tickets', (Get-SafeName $Ticket.ToUpperInvariant()))) }
function Get-ArchNoteId($Vault, [string]$Project) { return (Join-NoteId @($Vault.Root, 'Architektur', (Get-SafeName $Project), ((Get-SafeName $Project) + ' – Architektur'))) }

# Stellt sicher, dass eine gemeinsame Notiz (System/Werkzeug/Projekt) existiert, ohne fremde Blöcke zu verändern.
function Ensure-SharedNote($Vault, [string]$NoteId, [string]$Type, [string]$Title, [string[]]$Tags, [string[]]$Projects = @(), [string]$Intro = '') {
    $front = [ordered]@{ type = $Type; tags = @('copit') + $Tags; projects = @($Projects | ForEach-Object { Get-Link (Get-ProjectNoteId $Vault $_) $_ }) }
    [void](Write-Note $Vault $NoteId $front ([ordered]@{}) $Title $Intro)
}

# Tagesnotiz: nutzt die Einstellungen des Obsidian-Plugins "Tägliche Notizen" (.obsidian/daily-notes.json), sonst <Root>/Tage/JJJJ-MM-TT.
function Get-DayNoteId($Vault, [datetime]$Date) {
    $useDaily = (Get-CopitValue 'dailyNotes' 'ja') -notmatch '^(nein|no|false|0)$'
    $cfgFile = Join-Path $Vault.Path '.obsidian\daily-notes.json'
    if ($useDaily -and (Test-Path -LiteralPath $cfgFile)) {
        $cfg = Read-JsonFile $cfgFile
        $format = [string](Get-Prop $cfg 'format' '')
        if (-not $format) { $format = 'YYYY-MM-DD' }
        $folder = ([string](Get-Prop $cfg 'folder' '')).Trim('/', '\')
        $de = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
        $map = @{ 'YYYY' = 'yyyy'; 'YY' = 'yy'; 'MMMM' = 'MMMM'; 'MMM' = 'MMM'; 'MM' = 'MM'; 'M' = '%M'; 'DD' = 'dd'; 'D' = '%d'; 'dddd' = 'dddd'; 'ddd' = 'ddd' }
        $rest = [regex]::Replace($format, '\[[^\]]*\]|YYYY|YY|MMMM|MMM|MM|M|DD|D|dddd|ddd', '')
        if ($rest -notmatch '[A-Za-z]') {
            $name = [regex]::Replace($format, '\[([^\]]*)\]|YYYY|YY|MMMM|MMM|MM|M|DD|D|dddd|ddd', {
                    param($m)
                    if ($m.Groups[1].Success) { return $m.Groups[1].Value }
                    return $Date.ToString($map[$m.Value], $de)
                })
            return (Join-NoteId @($folder, $name))
        }
    }
    return (Join-NoteId @($Vault.Root, 'Tage', $Date.ToString('yyyy-MM-dd')))
}

# Liest Frontmatter aller Notizen eines Ordners als Hashtables (Key → Text, Listen als Array).
function Get-FolderNotes($Vault, [string]$Folder) {
    $dir = Join-Path $Vault.Path ($Folder -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $out = New-Object System.Collections.ArrayList
    foreach ($f in [IO.Directory]::EnumerateFiles($dir, '*.md', 'AllDirectories')) {
        $parts = Split-Note ([IO.File]::ReadAllText($f))
        $h = @{}
        foreach ($k in $parts.Entries.Keys) {
            $lines = $parts.Entries[$k]
            if ($lines.Count -gt 1 -or $lines[0] -match ':\s*\[') { $h[$k] = Get-YamlListValues $lines }
            else { $h[$k] = (($lines[0] -replace '^[^:]*:\s*', '').Trim() -replace '^"(.*)"$', '$1' -replace '\\"', '"') }
        }
        $rel = $f.Substring($Vault.Path.TrimEnd('\', '/').Length + 1) -replace '\\', '/'
        $h['_id'] = $rel.Substring(0, $rel.Length - 3)
        $h['_body'] = $parts.Body
        [void]$out.Add($h)
    }
    return @($out)
}

function Format-Minutes([double]$Min) {
    $m = [int][Math]::Round($Min)
    if ($m -lt 60) { return "$m min" }
    return ('{0} h {1:00} min' -f [int][Math]::Floor($m / 60), ($m % 60))
}

function Format-Number([double]$N) { return $N.ToString('#,0', [Globalization.CultureInfo]::GetCultureInfo('de-DE')) }

Export-ModuleMember -Function *
