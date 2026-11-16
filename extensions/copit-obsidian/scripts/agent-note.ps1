# Copit-Aktion: Notiz zu einem Agent-Lauf im Obsidian-Vault anlegen/aktualisieren.
# Verkettung: Agent → Projekt, Ticket, Tag, Werkzeug (Anbieter), geänderte Dateien → Architektur-Notizen (Services/Applikationen).
# Hub-Notizen (Ticket, Projekt) bekommen eine aktuelle Liste ihrer Agents.
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CopitObsidian.psm1') -Force -DisableNameChecking

$vault = Resolve-Vault
$agentId = Get-CopitValue 'agentId'
$title = Get-CopitValue 'title' 'Agent'
$ticket = (Get-CopitValue 'ticket').ToUpperInvariant()
$repo = Get-CopitValue 'repo' $env:COPIT_REPO
$project = Resolve-ProjectName $repo (Get-CopitValue 'projectId')
$provider = Get-CopitValue 'provider' 'Claude'
$created = [datetime]::Now
$c = Get-CopitValue 'created'
if ($c) { [void][datetime]::TryParse($c, [ref]$created) }
$inv = [Globalization.CultureInfo]::InvariantCulture
function Num([string]$n) { $d = 0.0; [void][double]::TryParse(((Get-CopitValue $n '0') -replace ',', '.'), [Globalization.NumberStyles]::Float, $inv, [ref]$d); return $d }

# ---------------------------------------------------------------- Bestehende Notiz desselben Agents finden
$agentsFolder = Join-NoteId @($vault.Root, 'Agents')
$existing = $null
if ($agentId) { $existing = Get-FolderNotes $vault $agentsFolder | Where-Object { $_['agent_id'] -eq $agentId } | Select-Object -First 1 }
if ($existing) { $noteId = $existing['_id'] }
else {
    $name = Get-SafeName ("{0} {1}{2}" -f $created.ToString('yyyy-MM-dd'), $(if ($ticket) { "$ticket " } else { '' }), $title)
    $noteId = Join-NoteId @($agentsFolder, $created.ToString('yyyy'), $name)
}

# ---------------------------------------------------------------- Geänderte Dateien → Architektur
$files = @()
if ($repo -and (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
    $base = Get-CopitValue 'baseSha'
    try {
        if ($base) { $files = @(& git -C $repo diff --name-only $base 2>$null) }
        else { $files = @(& git -C $repo diff --name-only HEAD 2>$null) }
        $files += @(& git -C $repo ls-files --others --exclude-standard 2>$null)
    } catch { }
    $files = @($files | Where-Object { $_ } | Select-Object -Unique | Select-Object -First 200)
}
$archIndex = Read-JsonFile (Join-Path $vault.Path ((Join-NoteId @($vault.Root, 'Architektur', (Get-SafeName $project), 'copit-index')) + '.json'))
$touched = [ordered]@{}
$fileLines = foreach ($f in $files) {
    $targets = @()
    if ($archIndex) { $targets = @(Get-Prop (Get-Prop $archIndex 'files') $f @()) }
    foreach ($t in $targets) { $touched[$t] = $true }
    $links = ($targets | ForEach-Object { Get-Link $_ }) -join ', '
    if ($links) { "- ``$f`` → $links" } else { "- ``$f``" }
}

# ---------------------------------------------------------------- Agent-Notiz
$projectId = Get-ProjectNoteId $vault $project
$ticketId = if ($ticket) { Get-TicketNoteId $vault $ticket } else { '' }
$toolName = $provider.ToLowerInvariant()
$toolId = Get-ToolNoteId $vault $toolName
$dayId = Get-DayNoteId $vault $created
$cost = Num 'cost'

$front = [ordered]@{
    type          = 'agent'
    agent_id      = $agentId
    title         = $title
    status        = (Get-CopitValue 'status')
    project       = (Get-Link $projectId $project)
    ticket        = $(if ($ticketId) { Get-Link $ticketId $ticket } else { '' })
    day           = (Get-Link $dayId $created.ToString('yyyy-MM-dd'))
    tool          = (Get-Link $toolId $provider)
    model         = (Get-CopitValue 'model')
    mode          = (Get-CopitValue 'mode')
    branch        = (Get-CopitValue 'branch')
    created       = $created.ToString('yyyy-MM-ddTHH:mm:ss')
    updated       = [datetime]::Now.ToString('yyyy-MM-ddTHH:mm:ss')
    cost_usd      = [Math]::Round($cost, 2)
    tokens        = [long](Num 'tokens')
    output_tokens = [long](Num 'outputTokens')
    agent_min     = [int](Num 'agentMinutes')
    ai_min        = [int](Num 'aiMinutes')
    tool_min      = [int](Num 'toolMinutes')
    turns         = [int](Num 'turns')
    lines_added   = [int](Num 'linesAdded')
    lines_removed = [int](Num 'linesRemoved')
    files_changed = [int](Num 'files')
    touches       = @($touched.Keys | ForEach-Object { Get-Link $_ })
    tags          = @('copit', 'copit/agent', ('copit/status/' + ((Get-CopitValue 'status' 'offen') -replace '\s+', '-').ToLowerInvariant()))
}

$prompt = Get-CopitValue 'prompt'
$summary = Get-CopitValue 'summary'
$sb = New-Object System.Text.StringBuilder
$meta = @("**Projekt:** $(Get-Link $projectId $project)")
if ($ticketId) { $meta += "**Ticket:** $(Get-Link $ticketId $ticket)" }
$meta += "**Tag:** $(Get-Link $dayId $created.ToString('dd.MM.yyyy'))"
$meta += "**Anbieter:** $(Get-Link $toolId $provider)$(if ($front.model) { ' · ' + $front.model })"
if ($front.branch) { $meta += "**Branch:** ``$($front.branch)``" }
[void]$sb.AppendLine("> [!info] $($front.status) · $title")
[void]$sb.AppendLine('> ' + ($meta -join ' · '))
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Auftrag')
[void]$sb.AppendLine($(if ($prompt) { $prompt } else { '_(kein Auftrag übermittelt)_' }))
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Ergebnis')
[void]$sb.AppendLine($(if ($summary) { $summary } else { '_(noch keine Zusammenfassung)_' }))
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Kennzahlen')
[void]$sb.AppendLine('| Agent-Zeit | KI-Zeit | Tool-Zeit | Kosten | Tokens (Output) | Turns | Zeilen | Dateien |')
[void]$sb.AppendLine('|---|---|---|---|---|---|---|---|')
[void]$sb.AppendLine(('| {0} | {1} | {2} | ${3} | {4} ({5}) | {6} | +{7} −{8} | {9} |' -f (Format-Minutes $front.agent_min), (Format-Minutes $front.ai_min), (Format-Minutes $front.tool_min), $cost.ToString('0.00', $inv), (Format-Number $front.tokens), (Format-Number $front.output_tokens), $front.turns, $front.lines_added, $front.lines_removed, $front.files_changed))
if ($touched.Count -gt 0) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Betroffene Architektur')
    foreach ($t in $touched.Keys) { [void]$sb.AppendLine("- $(Get-Link $t)") }
}
if ($fileLines) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Geänderte Dateien')
    foreach ($l in $fileLines) { [void]$sb.AppendLine($l) }
}
[void](Write-Note $vault $noteId $front ([ordered]@{ agent = $sb.ToString() }) $title)

# ---------------------------------------------------------------- Hubs: Werkzeug, System, Projekt, Ticket
Ensure-SharedNote $vault $toolId 'werkzeug' $provider @('copit/werkzeug') @($project) "KI-Agent-Anbieter. Verlinkte Agents erscheinen unter Backlinks."
$all = Get-FolderNotes $vault $agentsFolder

function Format-AgentList($list, [int]$Max) {
    $rows = @('| Datum | Agent | Status | Zeit | Kosten |', '|---|---|---|---|---|')
    foreach ($a in ($list | Sort-Object { $_['created'] } -Descending | Select-Object -First $Max)) {
        $d = ''; if ($a['created']) { $d = ([datetime]$a['created']).ToString('dd.MM.yyyy') }
        $rows += ('| {0} | {1} | {2} | {3} | ${4} |' -f $d, ((Get-Link $a['_id'] $a['title']) -replace '\|', '\|'), $a['status'], (Format-Minutes ([double]$a['agent_min'])), $a['cost_usd'])
    }
    return ($rows -join "`n")
}
function Sum-Agents($list) {
    $min = 0.0; $usd = 0.0
    foreach ($a in $list) { $min += [double]$a['agent_min']; $usd += [double]::Parse(([string]$a['cost_usd'] -replace ',', '.'), $inv) }
    return ('{0} Agents · {1} Agent-Zeit · ${2}' -f @($list).Count, (Format-Minutes $min), $usd.ToString('0.00', $inv))
}

if ($ticketId) {
    $jiraId = Get-SystemNoteId $vault 'Jira'
    Ensure-SharedNote $vault $jiraId 'system' 'Jira' @('copit/system') @($project) 'Ticketsystem. Tickets und Architektur-Aufrufe verlinken hierher.'
    $tAgents = @($all | Where-Object { $_['ticket'] -match ('\|' + [regex]::Escape($ticket) + '\]\]$') })
    $tFront = [ordered]@{ type = 'ticket'; ticket = $ticket; project = (Get-Link $projectId $project); system = (Get-Link $jiraId 'Jira'); tags = @('copit', 'copit/ticket') }
    $tBody = (Sum-Agents $tAgents) + "`n`n" + (Format-AgentList $tAgents 50)
    [void](Write-Note $vault $ticketId $tFront ([ordered]@{ agents = $tBody }) $ticket)
}

$pAgents = @($all | Where-Object { $_['project'] -match ('\|' + [regex]::Escape($project) + '\]\]$') })
$pTickets = @($pAgents | Where-Object { $_['ticket'] } | ForEach-Object { $_['ticket'] } | Select-Object -Unique)
$pFront = [ordered]@{ type = 'project'; project = $project; tags = @('copit', 'copit/projekt') }
if ($repo) { $pFront['repo'] = $repo }
$archId = Get-ArchNoteId $vault $project
$pBlocks = [ordered]@{}
$pBlocks['agents'] = "### Agents`n" + (Sum-Agents $pAgents) + "`n`n" + (Format-AgentList $pAgents 15)
$tl = if ($pTickets.Count) { ($pTickets | ForEach-Object { "- $_" }) -join "`n" } else { '_keine_' }
$pBlocks['tickets'] = "### Tickets`n$tl"
if (Test-Path -LiteralPath (Get-NoteFile $vault $archId)) { $pFront['architecture'] = (Get-Link $archId 'Architektur') }
[void](Write-Note $vault $projectId $pFront $pBlocks $project)

Write-Output ("Obsidian: {0}" -f $noteId)
