# Gibt Vault-Wissen als Kontext für einen Agent aus (stdout, Markdown):
# Projekt-Notiz, Ticket-Notiz, frühere Agents zum Ticket, eigene Notizen, die das Ticket erwähnen, Architektur-Überblick.
# Mit -Architecture: vollständige Liste der Architektur-Notizen + Format für .copit/obsidian/beschreibungen.md.
param([string]$Ticket = '', [string]$Project = '', [switch]$Architecture, [int]$MaxChars = 40000)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CopitObsidian.psm1') -Force -DisableNameChecking
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$vault = Resolve-Vault
$repo = (Get-Location).Path
if (-not $Project) { $Project = Resolve-ProjectName $repo }
$Ticket = $Ticket.Trim().ToUpperInvariant()
$out = New-Object System.Text.StringBuilder
function Clean([string]$Body, [int]$Max = 4000) {
    $t = ($Body -replace '(?m)^%% copit:[^\n]*%%\r?\n?', '').Trim()
    if ($t.Length -gt $Max) { $t = $t.Substring(0, $Max) + "`n…(gekürzt)" }
    return $t
}
function Section([string]$Title, [string]$Text) { if ($Text.Trim()) { [void]$out.AppendLine("`n## $Title`n$Text") } }

[void]$out.AppendLine("# Vault-Kontext ($($vault.Path))")
[void]$out.AppendLine("Projekt: $Project$(if ($Ticket) { " · Ticket: $Ticket" })")

$p = Read-NoteFront $vault (Get-ProjectNoteId $vault $Project)
if ($p) { Section "Projekt-Notiz ($Project)" (Clean $p.Body 3000) }

if ($Ticket) {
    $t = Read-NoteFront $vault (Get-TicketNoteId $vault $Ticket)
    if ($t) { Section "Ticket-Notiz ($Ticket)" (Clean $t.Body 3000) }
    $agents = Get-FolderNotes $vault (Join-NoteId @($vault.Root, 'Agents')) | Where-Object { $_['ticket'] -match ('\|' + [regex]::Escape($Ticket) + '\]\]$') } | Sort-Object { $_['created'] } -Descending | Select-Object -First 5
    $sb = New-Object System.Text.StringBuilder
    foreach ($a in $agents) {
        $res = [regex]::Match($a['_body'], '(?ms)^## Ergebnis\s*\r?\n(.*?)(?=^## |^%% copit|\z)').Groups[1].Value.Trim()
        $arch = [regex]::Match($a['_body'], '(?ms)^## Betroffene Architektur\s*\r?\n(.*?)(?=^## |^%% copit|\z)').Groups[1].Value.Trim()
        [void]$sb.AppendLine("### $($a['created']) · $($a['title']) ($($a['status']))")
        if ($res) { [void]$sb.AppendLine((Clean $res 1500)) }
        if ($arch) { [void]$sb.AppendLine("Betroffen: " + (($arch -split "`n" | ForEach-Object { $_.Trim('- ').Trim() }) -join ', ')) }
    }
    Section 'Frühere Agents zu diesem Ticket' $sb.ToString()

    # Eigene Notizen (außerhalb des Copit-Ordners), die das Ticket erwähnen
    $rootDir = Join-Path $vault.Path $vault.Root
    $own = New-Object System.Text.StringBuilder; $n = 0
    foreach ($f in [IO.Directory]::EnumerateFiles($vault.Path, '*.md', 'AllDirectories')) {
        if ($f.StartsWith($rootDir) -or $f -match '[\\/]\.(obsidian|trash)[\\/]') { continue }
        $txt = [IO.File]::ReadAllText($f)
        if ($txt -notmatch ('\b' + [regex]::Escape($Ticket) + '\b')) { continue }
        # nur eigener Text: ohne Frontmatter und ohne von Copit erzeugte Blöcke
        $txt = (Split-Note $txt).Body
        $txt = [regex]::Replace($txt, '%% copit:([\w-]+):start[^\n]*%%[\s\S]*?%% copit:\1:end %%', '').Trim()
        if ($txt -notmatch ('\b' + [regex]::Escape($Ticket) + '\b')) { continue }
        $rel = $f.Substring($vault.Path.TrimEnd('\', '/').Length + 1)
        [void]$own.AppendLine("### $rel`n$(Clean $txt 1500)")
        if (++$n -ge 5) { break }
    }
    Section 'Eigene Notizen zum Ticket' $own.ToString()
}

# Architektur
$archFolder = Join-NoteId @($vault.Root, 'Architektur', (Get-SafeName $Project))
$notes = Get-FolderNotes $vault $archFolder
if ($notes.Count) {
    $sb = New-Object System.Text.StringBuilder
    $short = { param($l) (@($l) | ForEach-Object { ($_ -replace '^\[\[[^|]*\|', '' -replace '\]\]$', '') }) -join ', ' }
    foreach ($a in ($notes | Where-Object { $_['type'] -eq 'applikation' } | Sort-Object { $_['_id'] })) {
        [void]$sb.AppendLine("- **$(($a['_id'] -split '/')[-1])** ($($a['kind']), $($a['path'])) · Systeme: $(& $short $a['systems']) · hängt ab von: $(& $short $a['depends_on'])")
    }
    [void]$sb.AppendLine()
    foreach ($s in ($notes | Where-Object { $_['type'] -eq 'service' } | Sort-Object { $_['_id'] })) {
        $line = "- $(($s['_id'] -split '/')[-1]) [$($s['kind'])] ``$($s['file'])``"
        $u = & $short $s['uses']; if ($u) { $line += " · nutzt $u" }
        $y = & $short $s['systems']; if ($y) { $line += " · API → $y" }
        $w = & $short $s['tools']; if ($w) { $line += " · startet $w" }
        if ($Architecture) { $line += ' · id: `' + $s['_id'].Substring($archFolder.Length + 1) + '`' }
        [void]$sb.AppendLine($line)
    }
    Section 'Architektur (aus dem Vault)' $sb.ToString()
} elseif ($Architecture) {
    Section 'Architektur' 'Noch kein Architektur-Export vorhanden. Führe zuerst die Aktion „Architektur nach Obsidian“ aus (architecture.ps1).'
}

if ($Architecture) {
    Section 'Format für .copit/obsidian/beschreibungen.md' @'
Je Applikation/Service ein Abschnitt; Überschrift = id oder Name. Die Zeilen „Nutzt:“ ergänzen Beziehungen,
die die automatische Analyse nicht erkennt (Namen von Services, Applikationen oder externen Systemen, kommagetrennt).

## Services/JiraClient
Kapselt die Jira-REST-API (Tickets, Worklogs, Tempo-Accounts). Authentifizierung per Token aus den Einstellungen.
Nutzt: HttpFactory, Jira

## Applikationen/Copit.Core
Fachlogik ohne UI: Agents, Zeitbuchung, Automationen, Integrationen.
'@
}

$text = $out.ToString()
if ($text.Length -gt $MaxChars) { $text = $text.Substring(0, $MaxChars) + "`n…(Kontext gekürzt)" }
Write-Output $text
