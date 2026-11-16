# Copit-Extensions

Extensions erweitern Copit um **Agent-Vorlagen**, **Automations-Aktionen** (Skripte), **Automations-Rezepte**,
**Permission-Regeln** und **eigene Tools** (eigener Eintrag in der Navigation). Sie liegen in einem GitHub-Repository und werden von Copit installiert und automatisch aktualisiert.

## Installieren

In Copit → **Extensions** eine Quelle eingeben:

| Quelle | Bedeutung |
|---|---|
| `owner/repo` | Neuestes Release mit `*.copit.zip`, sonst Default-Branch (Manifest in der Wurzel) |
| `owner/repo@v1.2.0` | Bestimmtes Release bzw. Tag/Branch |
| `owner/repo/pfad/zum/ordner` | Ordner im Repo (z. B. mehrere Extensions in einem Repo) |
| `https://github.com/owner/repo/tree/main/ordner` | wie oben, als URL |

Öffentliche Repositories funktionieren ohne Anmeldung. Private brauchen einen GitHub-Token mit „Contents: Read-only“ (Copit → Customize → Updates).
Zum Entwickeln: **Lokaler Ordner…** – „Neu laden“ übernimmt Änderungen.

Mitgelieferte Extensions in diesem Repo:

| Quelle | Inhalt |
|---|---|
| `Copit-Code/Copit/extensions/copit-dotnet` | Agent-Vorlagen und `dotnet test` als Aktion |
| `Copit-Code/Copit/extensions/copit-obsidian` | Verketteter Wissensgraph im Obsidian-Vault: Agents, Tickets, Projekte, Architektur (Apps, Services, API-Aufrufe, Systeme), Tagesbericht, Vault-Kontext für Agents ([README](../extensions/copit-obsidian/README.md)) |
| `Copit-Code/Copit/extensions/copit-mcp` | Tool „MCP-Auswertung“: MCP-Server bewusst auswählen und Auswertungen prompten ([README](../extensions/copit-mcp/README.md)) |

## Aufbau

```
meine-extension/
  copit-extension.json      Manifest (Pflicht)
  scripts/…                 Skripte für Aktionen
  .github/workflows/release.yml   optional: Release als <id>.copit.zip
```

### copit-extension.json

```json
{
  "id": "meine-extension",
  "name": "Meine Extension",
  "version": "1.0.0",
  "description": "Was sie tut",
  "author": "Team",
  "agentTemplates": [
    {
      "id": "tests",
      "title": "Tests für {{ticket}}",
      "mode": "Edit",
      "prompt": "Schreibe Tests für {{ticket}} in {{project}} …",
      "branch": "test/{{ticket}}",
      "allowedTools": ["Bash(dotnet test:*)"]
    }
  ],
  "actions": [
    {
      "id": "test",
      "label": "dotnet test",
      "run": "powershell -NoProfile -ExecutionPolicy Bypass -File \"{ext}/scripts/test.ps1\"",
      "params": [{ "key": "filter", "label": "Testfilter", "placeholder": "Category=Fast" }],
      "timeoutMinutes": 20
    }
  ],
  "automations": [
    {
      "id": "test-after-agent",
      "name": "Agent fertig → dotnet test",
      "trigger": "agent.finished",
      "actions": [{ "type": "ext:meine-extension/test", "params": { "filter": "" } }]
    }
  ],
  "permissions": [{ "pattern": "Bash(dotnet test:*)", "level": "Allow" }]
}
```

| Feld | Bedeutung |
|---|---|
| `id` | eindeutig, `a-z 0-9 - .` – Ordnername unter `%LOCALAPPDATA%\Copit\extensions` |
| `agentTemplates` | erscheinen in **Agents → Neuer Agent → Vorlage**; Platzhalter `{{ticket}}`, `{{project}}`, `{{branch}}`, `{{ext}}` (Extension-Ordner, auch in `allowedTools`); `mode`: Ask, Plan, Edit, Auto |
| `tools` | eigene Tools mit Navigationseintrag; Art `mcp-prompt`: MCP-Server auswählen + Auswertung prompten (`systemPrompt`, `presets`, `placeholder`, `readFiles`, `icon`) |
| `actions` | erscheinen im **Automation-Builder** unter dem Namen der Extension (Typ `ext:<id>/<aktion>`) |
| `automations` | werden **deaktiviert** übernommen – im Builder prüfen und einschalten |
| `permissions` | werden nach Rückfrage als Copit-Regeln übernommen (`Allow`, `Ask`, `Deny`) |

### Aktionen

`run` wird mit `cmd /c` im Repository des Agents ausgeführt. Platzhalter: `{ext}` = Extension-Ordner, `{repo}` = Repository
(ohne Anführungszeichen ersetzt – bei Bedarf selbst quoten). Umgebungsvariablen:

| Variable | Inhalt |
|---|---|
| `COPIT_EXT_DIR`, `COPIT_REPO` | Extension-Ordner, Repository |
| `COPIT_PARAM_<KEY>` | Parameter der Aktion aus dem Builder (Platzhalter wie `{{ticket}}` schon ersetzt) |
| `COPIT_VAR_<NAME>` | Variablen des Ereignisses: `TITLE`, `TICKET`, `BRANCH`, `STATUS`, `SUMMARY`, `PROJECT`, `REPO`, `COST`, bei Agents zusätzlich `AGENTID`, `PROJECTID`, `MODEL`, `MODE`, `BASESHA`, `TOKENS`, `OUTPUTTOKENS`, `TURNS`, `AGENTMINUTES`, `AIMINUTES`, `TOOLMINUTES`, `LINESADDED`, `LINESREMOVED`, `CREATED`, `PROMPT` |

Exit-Code ≠ 0 gilt als Fehler. Die letzten Zeilen der Ausgabe stehen im Automations-Protokoll.

## Per GitHub veröffentlichen

1. Repository mit dem Extension-Ordner anlegen und pushen – schon installierbar über `owner/repo`.
2. Für saubere Versionen den Workflow aus [`extensions/template/.github/workflows/release.yml`](../extensions/template/.github/workflows/release.yml)
   übernehmen und einen Tag pushen:

   ```bash
   git tag v1.0.0 && git push origin v1.0.0
   ```

   Der Workflow setzt die Version im Manifest und hängt `<id>.copit.zip` an das Release.
3. Copit prüft beim Start und alle 6 Stunden auf neue Releases bzw. Commits und meldet Updates unter **Extensions**.

## Sicherheit

Aktionen führen Befehle mit deinen Rechten aus. Copit zeigt vor jeder Installation, welche Befehle eine Extension mitbringt.
Nur Extensions aus vertrauenswürdigen Quellen installieren.
