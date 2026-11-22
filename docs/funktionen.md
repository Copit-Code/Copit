# Funktionen

Übersicht über die Seiten der Windows-App. Die VSCodium-Extension bietet Board, Agent-Tab, Activity, Budgets, Customize, Automationen und Zeitbuchung in derselben Logik.

- [Cockpit](#cockpit)
- [Agents und Session](#agents-und-session)
- [Änderungen pro Ticket](#änderungen-pro-ticket)
- [Activity und Budgets](#activity-und-budgets)
- [Zeitbuchung](#zeitbuchung)
- [Projekte](#projekte)
- [Automationen](#automationen)
- [Doku](#doku)
- [Customize und Einstellungen](#customize-und-einstellungen)
- [Extensions und Tools](#extensions-und-tools)

## Cockpit

Copit startet immer im Cockpit. Es zeigt:
- die Stunden des Tages und den Buchungsstand,
- zuletzt genutzte Projekte,
- laufende Agents,
- Pipelines und Reviews.

Ohne Jira- und GitLab-Verbindung erscheint ein Hinweis mit Link zu den Einstellungen.

## Agents und Session

- **Board:** Inbox → In Progress → Review → Done. Karten lassen sich per Drag & Drop verschieben. Das Menü einer Karte bietet: Öffnen, Stoppen, Status setzen, Code-Änderungen des Tickets, Archivieren und Löschen.
- **Neuer Agent:** Projekt, Titel, Ticket, Branch, Auftrag, Anbieter (Claude/Copilot), Modus (Fragen, Plan, Edit, Auto) und Modell. Optional gibt es einen eigenen git worktree pro Agent. Vorlagen aus Extensions füllen das Formular vor.
- **Session:**
  - Verlauf mit Text, Tool-Aufrufen und Sub-Agents.
  - Rückfragen des Agents erscheinen als Auswahl. Mehrere Fragen beantwortest du gesammelt und sendest sie mit **Antworten senden**. Offene Fragen entscheidet der Agent selbst.
  - Fehlende Berechtigungen lassen sich einmalig oder dauerhaft freigeben.
  - Pro Lauf werden erfasst: Agent-Zeit (Wanduhr), KI-Zeit, Tool-Zeit, Tokens, Kosten und geänderte Zeilen.
- **Kontext:** Das Kontextfenster ist wählbar. Die Prompt-Bibliothek liefert wiederkehrende Aufträge.

## Änderungen pro Ticket

Seite **Änderungen**, auch erreichbar über das Ticket-Menü im Projekt oder die Agent-Karte.

- **Übersicht:** alle Tickets mit Commits im gewählten Zeitraum, mit Titel aus Jira, Commits, Dateien, ±-Zeilen und Autoren. Zu einem Ticket gehören:
  - Commits mit dem Key in der Message,
  - Commits auf einem Branch mit dem Key im Namen, über alle Branches, auch remote.
- **Ticket-Ansicht:**
  - **Gesamtänderung** fasst alle Ticket-Commits pro Datei zusammen.
  - Alternativ ein **einzelner Commit** oder die noch **nicht committete Arbeit** eines Agents (auch im Worktree).
  - Dateibaum mit Status (neu, geändert, umbenannt, gelöscht) und ±-Balken.
  - Diff mit Zeilennummern alt und neu, Funktions- und Klassenkontext im Hunk-Kopf und hervorgehobenen geänderten Wörtern.
  - Ein Klick auf eine Zeilennummer öffnet die Datei an dieser Stelle in VS Code bzw. VSCodium.

Der Key wird exakt abgeglichen: `SHOP-1` trifft nicht `SHOP-12`.

## Lieferstatus pro Ticket

Copit zeigt zu jedem Agent, wie weit das Ticket geliefert ist: **Commit → Push → Merge Request → Pipeline → Jira**.
- **Board-Karte:** Symbole je Schritt und ein Satz als Urteil, z. B. „Gepusht · Pipeline grün“ oder „Pipeline fehlgeschlagen“.
- **Session:** Panel mit allen Schritten, Links nach GitLab und Jira, Pipeline-Jobs mit Status, Dauer und Log. Eine rote Pipeline lässt sich erneut starten.
- **Aktualisierung:** nach jedem Lauf, nach dem Verschieben einer Karte und im Hintergrund. Solange eine Pipeline läuft, alle 30 Sekunden. Wird sie grün oder rot, kommt eine Meldung.

## Activity und Budgets

- **Activity:**
  - Tag- und Wochenansicht mit Zeitleisten pro Agent und Projekt.
  - Getrennt nach KI-Zeit, Agent-Zeit und Tool-Zeit.
  - Mit Tokens und Kosten in Dollar.
  - Echte Claude-Sessions werden einbezogen.
- **Budgets:**
  - Als Kosten oder Stunden, pro Woche, Monat oder gesamt.
  - Begrenzt auf bestimmte Projektordner oder für alles.
  - Warnschwellen (z. B. 80 % und 100 %) werden als Hinweis gemeldet.
  - Optional ein Hard-Stop: neue Läufe werden dann blockiert.
- **Tageslimits:** je Anbieter, für Claude in Dollar, für Copilot in Stunden oder Läufen.

## Zeitbuchung

- **Vorschläge:** Die KI erstellt Buchungsvorschläge aus:
  - Agent-Läufen,
  - Commits,
  - GitLab-Aktivität (Pushes, MRs, Kommentare),
  - Notizen.

  Gebucht wird der geschätzte Aufwand einer Person, nicht die reine KI-Zeit.
- **Rückfragen mit Gedächtnis:** Antworten wie „Thema X gehört zu Ticket Y“ merkt sich Copit für die nächsten Tage.
- **Tempo-Accounts:** Copit bucht nur mit gültigem Account. Accounts kommen aus dem Ticket, aus dem Gedächtnis oder aus der Projektzuordnung.
- **Account-Status im Kalender:**
  - Der Tagesbalken ist aufgeteilt: grün mit Account, orange ohne, grau unbekannt. Darüber stehen die Stunden.
  - Das gilt auch für Buchungen von außerhalb Copit, die Accounts liest Copit über Tempo.
  - In der Tagesansicht lässt sich ein fehlender Account an der bestehenden Buchung nachtragen.
- **Tasks im Epic:** Fehlt ein passendes Ticket, legt Copit es im Epic an, mit Account.
- **Ohne Jira:** Gebucht wird intern, auch ohne Ticket.

## Projekte

- Ein Projekt verbindet Jira-Epic, Quellordner, GitLab-Projekt, Farbe und KI-Anweisungen.
- **Ordner:**
  - Projekte lassen sich in Ordner und Unterordner gruppieren (`Kunde A/Backend`).
  - Ordner lassen sich auf- und zuklappen, der Zustand wird gespeichert.
  - Ordner lassen sich umbenennen, verschieben und löschen. Beim Löschen rücken die Projekte eine Ebene nach oben.
  - Projekte verschiebst du per Drag & Drop, über „In Ordner…“ oder im Editor.
  - Die Seitenleiste ist gruppiert, sobald Ordner existieren.
- **Mehrere Projekte in einem Epic:** Im Projekt-Editor unter „Ticket-Filter“ eine Jira-Component, ein Label oder JQL eintragen. Das Projekt zeigt dann nur passende Tickets, neue Tickets bekommen Component und Label automatisch. Ohne Filter ordnet Copit über Commits und Branches im Repo zu, manuell über Ticket → „Projekt zuordnen…“. Nicht zugeordnete Tickets sind markiert und lassen sich ausblenden.
- **Projektseite:**
  - Ticket-Board des Epics, Notizen und Sessions.
  - Neues Ticket mit KI-Ausarbeitung und Schätzung.
  - Aus einem Ticket heraus eine Session starten, mit oder ohne Branch.

## Automationen

WENN Trigger [UND Bedingungen] DANN Aktionen, mit **Testlauf**.

- **Trigger:**
  - Agent startet, ist fertig, braucht Input, ist fehlgeschlagen oder gestoppt
  - Karte verschoben, mit Von- und Nach-Spalte (z. B. Review → Done)
  - Neuer Commit, Push, neuer Branch
  - Pipeline fehlgeschlagen oder erfolgreich
  - Ticket-Status geändert, Ticket zugewiesen (per JQL eingrenzbar)
  - Zeitplan (Uhrzeit, Wochentage)
- **Aktionen:**
  - Status setzen, neuen Agent starten, Agent fortsetzen, benachrichtigen
  - Jira-Kommentar, Jira-Status ändern, Zeit buchen, Stunden-Check
  - Commit, Push (auch gezielt den Branch der Karte), Branch anlegen, Merge Request
  - Doku aktualisieren, KI fragen, Befehl ausführen
  - Aktionen aus Extensions (`ext:<id>/<aktion>`)

Platzhalter wie `{{ticket}}`, `{{title}}`, `{{summary}}`, `{{branch}}`, `{{from}}` und `{{to}}` übernehmen Werte aus dem Ereignis.

Beispiel: WENN *Karte verschoben* von **Review** nach **Done**, DANN *Push* mit Branch `{{branch}}`.

Mitgelieferte Rezepte, zum Teil ausgeschaltet:
- Fertig → Jira-Kommentar
- Pipeline rot → Fix-Agent
- Feierabend-Check
- Ticket „In Arbeit“ → Plan-Agent

## Doku

**Doku** erzeugt pro Projekt verlinktes Markdown unter `/docs` im Repo. GitHub und GitLab zeigen es direkt an.
- **Code & Architektur:** Module, Abhängigkeiten (Mermaid), Dateien und Typen.
- **Arbeit & Verlauf:** Tickets ↔ Commits ↔ MRs ↔ Agent-Sessions ↔ Zeitbuchungen.
- **Projektwissen:** Konventionen, Zuordnungen, Notizen und Entscheidungen.

Generierte Blöcke stehen zwischen Markern, alles andere bleibt erhalten. Ein Doku-Agent ergänzt Beschreibungen und darf nur unter `docs/` schreiben.

## Customize und Einstellungen

- **Permission-Regeln:**
  - Allow, Ask oder Deny für Tools und Befehle, als Matrix auch für Git-Operationen.
  - Copit gibt sie den CLIs als `--allowedTools`/`--disallowedTools` mit.
  - Die Claude-`settings.json` wird einbezogen.
- **Integrationen:** Jira, GitLab und Auto-Update.
- **Token sparen mit der Skript-Bibliothek** (Einstellungen → KI):
  - Agents legen für wiederkehrende, mechanische Aufgaben kleine Skripte an, etwa für Suchen in Dateien, Logs filtern oder Testergebnisse zusammenfassen.
  - Statt vieler einzelner Tool-Aufrufe ruft der Agent das Skript auf.
  - Copit gibt die vorhandenen Skripte als kompakten Index im Systemprompt mit.
  - Ausführen ist nur aus der Bibliothek erlaubt, die Nutzung wird gezählt.
  - Die Bibliothek liegt global (`%LOCALAPPDATA%\Copit\scripts`) oder pro Projekt (`.copit/scripts` im Repo, kann mit eingecheckt werden).
  - Startskripte: `suche.ps1`, `dateien.ps1`.

## Extensions und Tools

Extensions kommen aus GitHub oder einem lokalen Ordner und bringen mit:
- Agent-Vorlagen,
- Automations-Aktionen,
- Rezepte,
- Permission-Regeln,
- **eigene Tools** mit Navigationseintrag.

Installation, Aufbau und Veröffentlichung beschreibt [extensions.md](extensions.md).

| Extension | Inhalt |
|---|---|
| [copit-obsidian](../extensions/copit-obsidian/README.md) | Wissensgraph im Obsidian-Vault: Agents, Tickets, Projekte und Architektur (Applikationen, Services, API-Aufrufe, Endpunkte, Systeme, Werkzeuge). Dazu Tagesbericht und Vault-Kontext für Agents. |
| [copit-mcp](../extensions/copit-mcp/README.md) | Tool „MCP-Auswertung“: MCP-Server bewusst auswählen, nur diese werden verwendet. Auswertung prompten, Verlauf und Export. |
| [copit-dotnet](../extensions/copit-dotnet) | Agent-Vorlagen (Tests, Review) und `dotnet test` als Aktion. |
