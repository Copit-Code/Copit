# Copit MCP-Auswertung

Bringt ein eigenes Tool **„MCP-Auswertung“** in die Copit-Navigation:

1. **MCP-Server bewusst auswählen**: Copit zeigt alle Server aus Claude Code (`~/.claude.json`, auch die projektbezogenen), aus `.mcp.json` im Projekt, aus Claude Desktop und die in Copit angelegten. Für die Auswertung werden **nur die angehakten Server** verwendet. Copit startet Claude dafür mit `--mcp-config` und `--strict-mcp-config`, sodass kein anderer Server mitläuft.
2. **Prompten**, welche Auswertung du haben möchtest, oder eine Vorlage wählen (Wochenrückblick, Pipeline-Gesundheit, offene Reviews, Fehler-Hotspots, Datenbank-Kennzahlen). Eine Vorlage wählt passende Server automatisch vor, sofern sie vorhanden sind.
3. **Ergebnis** als Markdown, live während der Auswertung. Daneben siehst du die Tool-Aufrufe und am Ende Kosten, Tokens und Dauer. Das Ergebnis lässt sich kopieren oder als `.md` speichern. Alle Läufe stehen im **Verlauf** und lassen sich wieder öffnen.

Die KI darf nur die Tools der gewählten Server benutzen und arbeitet im Fragen-Modus. Optional darf sie Dateien im Projektordner lesen (Schalter im Tool).

## Installation

In Copit unter **Extensions → Installieren**:

```
Copit-Code/Copit/extensions/copit-mcp
```

## Eigene Server

Im Tool unter **„Server hinzufügen“**. Erlaubt sind:
- eine Befehlszeile, z. B. `npx -y @modelcontextprotocol/server-postgres postgresql://localhost/db`,
- eine URL (HTTP-Server),
- ein JSON-Objekt im Claude-Format.

Die Server werden in `%LOCALAPPDATA%\Copit\mcp.json` gespeichert.

## Eigene Tools in anderen Extensions

Jede Extension kann unter `tools` ein solches Tool mitbringen, mit eigenen Anweisungen und Vorlagen:

```json
"tools": [{
  "id": "vertrieb",
  "title": "Vertriebs-Report",
  "icon": "activity",
  "kind": "mcp-prompt",
  "systemPrompt": "Du erstellst Vertriebsauswertungen …",
  "presets": [{ "title": "Pipeline nach Phase", "prompt": "…", "servers": ["hubspot"] }],
  "readFiles": false
}]
```
