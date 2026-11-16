# Copit-Aktion: Tagesbericht in die Tagesnotiz (Plugin „Tägliche Notizen“) oder nach <Root>/Tage/JJJJ-MM-TT.
# Inhalt: Agent-Läufe des Tages (Zeit, Kosten, Tokens), Zeitbuchung (gebucht/Vorschläge), Projekt-Notizen.
# Verkettung: Tag → Agents, Tickets, Projekte, Werkzeuge; die Tagesnotiz bekommt nur einen Copit-Block, eigener Text bleibt.
param([string]$Date = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CopitObsidian.psm1') -Force -DisableNameChecking

$vault = Resolve-Vault
if (-not $Date) { $Date = Get-CopitValue 'date' }
$day = [datetime]::Today
if ($Date -match '^gestern$') { $day = $day.AddDays(-1) } elseif ($Date) { $day = [datetime]::Parse($Date, [Globalization.CultureInfo]::GetCultureInfo('de-DE')).Date }
$key = $day.ToString('yyyy-MM-dd')
$inv = [Globalization.CultureInfo]::InvariantCulture
$de = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
function T($s) { return ([string]$s -replace '\|', '/' -replace "`r?`n", ' ') }
function Tbl([string]$Row) { return [regex]::Replace($Row, '\[\[[^\]]*\]\]', { param($m) $m.Value -replace '\|', '\|' }) }

$projects = @{}
foreach ($p in (Get-CopitProjects)) { $projects[$p.Id] = $p.Name }
$agentNotes = Get-FolderNotes $vault (Join-NoteId @($vault.Root, 'Agents'))
$noteByAgent = @{}
foreach ($n in $agentNotes) { if ($n['agent_id']) { $noteByAgent[$n['agent_id']] = $n['_id'] } }

# ---------------------------------------------------------------- Agent-Läufe des Tages
$rows = New-Object System.Collections.Generic.List[object]
foreach ($s in (Get-CopitSessions)) {
    $runs = @(Get-Prop $s 'Runs' @()) | Where-Object { ([datetime](Get-Prop $_ 'Start' '2000-01-01')).Date -eq $day }
    if (-not $runs) { continue }
    $wall = 0.0; $ai = 0.0; $tool = 0.0; $usd = 0.0; $tok = 0L
    foreach ($r in $runs) {
        $st = [datetime]$r.Start
        $en = Get-Prop $r 'End'; if ($en) { $wall += ([datetime]$en - $st).TotalMinutes }
        $ai += [double](Get-Prop $r 'AiMs' 0) / 60000; $tool += [double](Get-Prop $r 'ToolMs' 0) / 60000
        $usd += [double](Get-Prop $r 'CostUsd' 0)
        $tok += [long](Get-Prop $r 'InputTokens' 0) + [long](Get-Prop $r 'OutputTokens' 0) + [long](Get-Prop $r 'CacheRead' 0) + [long](Get-Prop $r 'CacheWrite' 0)
    }
    $pname = $projects[[string](Get-Prop $s 'ProjectId' '')]; if (-not $pname) { $pname = 'Ohne Projekt' }
    $rows.Add([pscustomobject]@{ Id = $s.Id; Title = (Get-Prop $s 'Title' 'Agent'); Ticket = ([string](Get-Prop $s 'Ticket' '')).ToUpperInvariant(); Project = $pname; Provider = [string](Get-Prop $s 'Provider' 'Claude'); Status = [string](Get-Prop $s 'Status' ''); Wall = $wall; Ai = $ai; Tool = $tool; Usd = $usd; Tokens = $tok; Runs = @($runs).Count; First = ([datetime]$runs[0].Start) })
}

# ---------------------------------------------------------------- Zeitbuchung
$worklogs = @(Get-Prop (Read-JsonFile (Join-Path (Get-CopitHome) 'worklogs.json')) 'Worklogs' @()) | Where-Object { ([datetime]$_.Started).Date -eq $day }
$dayEntry = Get-Prop (Get-Prop (Read-JsonFile (Join-Path (Get-CopitHome) 'days.json')) 'Days') $key
$suggestions = @(Get-Prop $dayEntry 'LastSuggestions' @())
$pnotes = @(Get-Prop (Read-JsonFile (Join-Path (Get-CopitHome) 'notes.json')) 'Notes' @()) | Where-Object { ($_.Date -is [datetime] -and $_.Date.ToString('yyyy-MM-dd') -eq $key) -or ([string]$_.Date).StartsWith($key) }

# ---------------------------------------------------------------- Bericht
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("## Copit · $($day.ToString('dddd, dd.MM.yyyy', $de))")
$totWall = ($rows | Measure-Object Wall -Sum).Sum; $totAi = ($rows | Measure-Object Ai -Sum).Sum; $totUsd = ($rows | Measure-Object Usd -Sum).Sum; $totTok = ($rows | Measure-Object Tokens -Sum).Sum
$booked = (@($worklogs) | ForEach-Object { [double]$_.TimeSpentSeconds / 3600 } | Measure-Object -Sum).Sum
$bookedS = (@($suggestions | Where-Object { $_.Booked }) | Measure-Object Hours -Sum).Sum
[void]$sb.AppendLine(('**{0} Agents** · Agent-Zeit {1} · KI-Zeit {2} · ${3} · {4} Tokens · gebucht {5} h' -f $rows.Count, (Format-Minutes ([double]$totWall)), (Format-Minutes ([double]$totAi)), ([double]$totUsd).ToString('0.00', $inv), (Format-Number ([double]$totTok)), ([double]$booked + [double]$bookedS).ToString('0.##', $de)))

$touchProjects = New-Object System.Collections.Generic.HashSet[string]; $touchTickets = New-Object System.Collections.Generic.HashSet[string]; $touchTools = New-Object System.Collections.Generic.HashSet[string]
if ($rows.Count) {
    [void]$sb.AppendLine("`n### Agents`n| Start | Agent | Projekt | Ticket | Zeit | KI | Kosten | Tokens |`n|---|---|---|---|---|---|---|---|")
    foreach ($r in ($rows | Sort-Object First)) {
        $agentLink = if ($noteByAgent.ContainsKey($r.Id)) { Get-Link $noteByAgent[$r.Id] (T $r.Title) } else { T $r.Title }
        $pl = Get-Link (Get-ProjectNoteId $vault $r.Project) $r.Project; [void]$touchProjects.Add($pl)
        $tl = ''; if ($r.Ticket) { $tl = Get-Link (Get-TicketNoteId $vault $r.Ticket) $r.Ticket; [void]$touchTickets.Add($tl) }
        [void]$touchTools.Add((Get-Link (Get-ToolNoteId $vault $r.Provider.ToLowerInvariant()) $r.Provider))
        [void]$sb.AppendLine((Tbl ('| {0} | {1} | {2} | {3} | {4} | {5} | ${6} | {7} |' -f $r.First.ToString('HH:mm'), $agentLink, $pl, $tl, (Format-Minutes $r.Wall), (Format-Minutes $r.Ai), $r.Usd.ToString('0.00', $inv), (Format-Number $r.Tokens))))
    }
}
if (@($worklogs).Count -or $suggestions.Count) {
    [void]$sb.AppendLine("`n### Zeitbuchung`n| Ticket | Stunden | Status | Text |`n|---|---|---|---|")
    foreach ($w in $worklogs) { $tl = Get-Link (Get-TicketNoteId $vault $w.IssueKey) $w.IssueKey; [void]$touchTickets.Add($tl); [void]$sb.AppendLine((Tbl ('| {0} | {1} | gebucht (intern) | {2} |' -f $tl, ([double]$w.TimeSpentSeconds / 3600).ToString('0.##', $de), (T $w.Comment)))) }
    foreach ($g in $suggestions) {
        if (-not $g.IssueKey) { continue }
        $tl = Get-Link (Get-TicketNoteId $vault $g.IssueKey) $g.IssueKey; [void]$touchTickets.Add($tl)
        $st = if ($g.Booked) { 'gebucht' } else { 'Vorschlag' }
        $acc = Get-Prop $g 'BookedAccount' (Get-Prop $g 'Account' '')
        if ($acc) { $st += " · $acc" }
        [void]$sb.AppendLine((Tbl ('| {0} | {1} | {2} | {3} |' -f $tl, ([double]$g.Hours).ToString('0.##', $de), $st, (T $g.Comment))))
    }
}
if (@($pnotes).Count) {
    [void]$sb.AppendLine("`n### Projekt-Notizen")
    foreach ($n in $pnotes) { $pn = $projects[[string]$n.ProjectId]; $pl = if ($pn) { Get-Link (Get-ProjectNoteId $vault $pn) $pn } else { '' }; if ($pl) { [void]$touchProjects.Add($pl) }; [void]$sb.AppendLine("- $pl $(T $n.Text)") }
}
if ($dayEntry -and (Get-Prop $dayEntry 'Note' '')) { [void]$sb.AppendLine("`n### Tagesnotiz aus Copit`n$($dayEntry.Note)") }
if ($rows.Count -eq 0 -and -not @($worklogs).Count -and -not $suggestions.Count -and -not @($pnotes).Count) { [void]$sb.AppendLine("`n_Keine Copit-Aktivität an diesem Tag._") }

$noteId = Get-DayNoteId $vault $day
$front = [ordered]@{
    copit_agents   = $rows.Count
    copit_agent_min = [int][Math]::Round([double]$totWall)
    copit_cost_usd = [Math]::Round([double]$totUsd, 2)
    copit_booked_h = [Math]::Round([double]$booked + [double]$bookedS, 2)
    copit_projects = @($touchProjects)
    copit_tickets  = @($touchTickets)
    copit_tools    = @($touchTools)
    tags           = @('copit/tag')
}
[void](Write-Note $vault $noteId $front ([ordered]@{ tag = $sb.ToString() }) $day.ToString('dddd, dd.MM.yyyy', $de))
Write-Output ("Tagesbericht {0}: {1} Agents, {2} → {3}" -f $day.ToString('dd.MM.yyyy'), $rows.Count, ('$' + ([double]$totUsd).ToString('0.00', $inv)), $noteId)
