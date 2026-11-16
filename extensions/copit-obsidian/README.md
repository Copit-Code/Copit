# Copit Obsidian

Macht aus deiner Arbeit in Copit einen verketteten Wissensgraphen in Obsidian. Jede Einheit ist eine eigene Notiz mit Frontmatter. Die Verbindungen stehen als Wikilinks in den Properties und im Text, so dass Graph-Ansicht, Backlinks und Dataview direkt funktionieren.

```
Projekt ─┬─ Ticket ── Agent ── Tag (Tagesnotiz)
         │              └── Werkzeug (claude, copilot)
         └─ Architektur
              Applikation ──enthält──▶ Service ──nutzt──▶ Service
                                         ├─ruft auf──▶ API-Aufruf ──▶ System (Jira, Stripe …)
                                         │                       └─▶ Endpunkt (intern, andere App)
                                         ├─stellt bereit──▶ Endpunkt
                                         └─startet──▶ Werkzeug (git, dotnet …)
```

Systeme und Werkzeuge gibt es pro Vault nur einmal. Jedes Projekt bekommt darin einen eigenen Abschnitt. So sieht man im Graphen, welche Projekte dieselbe API oder dasselbe Tool nutzen.

## Installation

In Copit unter **Extensions → Installieren**:

```
Copit-Code/Copit/extensions/copit-obsidian
```

Den Vault findet die Extension automatisch: Es wird der zuletzt geöffnete Vault der Obsidian-App verwendet. Einen festen Vault legst du so fest:
- mit der Aktion **„Obsidian-Vault festlegen“** (Automationen → Testlauf),
- mit dem Parameter `vault` an jeder Aktion,
- oder mit der Umgebungsvariable `COPIT_OBSIDIAN_VAULT`.

Alle Notizen landen im Unterordner `Copit/` des Vaults. Das lässt sich mit dem Parameter `folder` ändern.

## Was die Extension mitbringt

| Art | Name | Funktion |
|---|---|---|
| Aktion | Agent-Notiz in Obsidian | Notiz pro Agent mit Auftrag, Ergebnis, Kennzahlen (Zeit, Kosten, Tokens), geänderten Dateien und den betroffenen Services. Aktualisiert dabei Ticket-, Projekt- und Werkzeug-Notiz. Ein erneuter Lauf desselben Agents aktualisiert dieselbe Notiz. |
| Aktion | Architektur nach Obsidian | Scannt das Repo (C#, TypeScript/JavaScript, Python, Go, Java/Kotlin/PHP) und schreibt Applikationen, Services, API-Aufrufe, Endpunkte, Systeme und Werkzeuge als Notizen. Dazu kommen eine Übersicht mit Mermaid-Diagramm und ein Obsidian-Canvas. |
| Aktion | Tagesbericht in Obsidian | Agents, Kosten, Tokens, Zeitbuchung und Projekt-Notizen des Tages. Geschrieben wird in die Tagesnotiz des Plugins „Tägliche Notizen“, sonst nach `Copit/Tage/`. |
| Aktion | Obsidian-Vault festlegen | Speichert Vault und Ordner in `%LOCALAPPDATA%\Copit\obsidian.json`. |
| Automation | Agent fertig / fehlgeschlagen → Obsidian-Notiz | Anfangs ausgeschaltet. |
| Automation | Feierabend → Tagesbericht | Anfangs ausgeschaltet. Läuft Mo–Fr um 17:45. |
| Automation | Push → Architektur aktualisieren | Anfangs ausgeschaltet. |
| Agent-Vorlage | *Ticket* mit Vault-Kontext | Der Agent lädt vorher Projekt-Notiz, frühere Agents zum Ticket, eigene Notizen, die das Ticket erwähnen, und die Architektur. |
| Agent-Vorlage | Architektur für Obsidian beschreiben | Der Agent liest den Code und beschreibt Apps und Services in `.copit/obsidian/beschreibungen.md`. Mit `Nutzt: …` ergänzt er Beziehungen, die die Analyse nicht erkennt. Danach aktualisiert er den Vault. |

## Eigene Inhalte bleiben erhalten

Copit ersetzt nur die Blöcke zwischen `%% copit:<name>:start %%` und `%% copit:<name>:end %%` sowie die eigenen Frontmatter-Keys.

Alles andere bleibt unverändert: Text unter `## Notizen`, eigene Properties und zusätzliche Tags. In deinen Tagesnotizen schreibt Copit nur einen Block und Properties mit dem Präfix `copit_`.

Architektur-Notizen, die der Scanner nicht mehr findet, werden nicht gelöscht. Sie bekommen `veraltet: true` und den Tag `copit/veraltet`.

## Frontmatter (Auszug)

```yaml
type: "service"            # agent | ticket | project | applikation | service | api-aufruf | endpunkt | system | werkzeug | architektur
app: "[[…/Applikationen/Shop.Api|Shop.Api]]"
uses: ["[[…/Services/OrderService|OrderService]]"]
used_by: [...]
calls: ["[[…/API-Aufrufe/Jira/GET ∕ rest ∕ api ∕ 2 ∕ myself|GET /rest/api/2/myself]]"]
systems: ["[[Copit/Systeme/Jira|Jira]]"]
tools: ["[[Copit/Werkzeuge/git|git]]"]
tags: ["copit", "copit/architektur", "copit/service"]
```

Beispiel für Dataview:

```dataview
TABLE app, length(calls) AS "API-Aufrufe", systems
FROM #copit/service
SORT length(calls) DESC
```

## Voraussetzungen

Windows PowerShell 5.1 (bei Windows dabei) oder PowerShell 7. Die Skripte laufen ohne weitere Abhängigkeiten. Für die Liste der geänderten Dateien in der Agent-Notiz wird `git` benötigt.
