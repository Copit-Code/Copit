<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-dark.svg">
    <img src="docs/assets/logo-light.svg" alt="Copit" width="340">
  </picture>
</p>

<p align="center">
  <b>Die Agent-IDE für Claude &amp; Copilot CLI.</b><br>
  Agents auf dem Kanban-Board, Zeiten, Tokens und Kosten im Blick, Code-Änderungen pro Ticket – und die Arbeitszeit landet in Jira.
</p>

<p align="center">
  <a href="https://github.com/Copit-Code/Copit/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/Copit-Code/Copit?style=flat-square&color=E8743B&labelColor=14110F"></a>
  <a href="https://github.com/Copit-Code/Copit/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/Copit-Code/Copit/ci.yml?style=flat-square&label=CI&labelColor=14110F"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows-App-EDE6DD?style=flat-square&labelColor=14110F">
  <img alt="VSCodium" src="https://img.shields.io/badge/VSCodium-Extension-EDE6DD?style=flat-square&labelColor=14110F">
</p>

<p align="center">
  <a href="https://copit-code.github.io/Copit/"><b>Website</b></a> ·
  <a href="https://github.com/Copit-Code/Copit/releases/latest"><b>Download</b></a> ·
  <a href="docs/erste-schritte.md">Erste Schritte</a> ·
  <a href="docs/funktionen.md">Funktionen</a> ·
  <a href="docs/extensions.md">Extensions</a> ·
  <a href="docs/entwicklung.md">Entwicklung</a>
</p>

---

Copit macht aus den KI-CLIs von **Claude Code** und **GitHub Copilot** ein Cockpit für die tägliche Arbeit: Agents laufen parallel in eigenen Worktrees, jeder Lauf wird mit KI-Zeit, Tool-Zeit, Tokens und Kosten erfasst, und am Ende des Tages schlägt Copit die Zeitbuchung für Jira vor. Alles läuft lokal; Daten liegen unter `%LOCALAPPDATA%\Copit`.

## Was Copit kann

| | |
|---|---|
| **Agents & Board** | Agents mit Claude oder Copilot starten (Fragen, Plan, Edit, Auto), auf einem Kanban-Board von Inbox bis Done verfolgen, parallel in eigenen git worktrees. |
| **Änderungen pro Ticket** | Alle Commits und offene Agent-Arbeit zu einem Ticket – Gesamtänderung, einzelne Commits, Dateibaum und Diff mit Zeilennummern. |
| **Activity & Budgets** | Tag/Woche mit Zeitleisten, KI-/Agent-/Tool-Zeit, Tokens und Dollar; Budgets pro Projekt mit Warnschwellen und Hard-Stop, Tageslimits je Anbieter. |
| **Zeitbuchung** | KI-Vorschläge aus Agent-Läufen, Commits und GitLab-Aktivität; Rückfragen mit Gedächtnis, Tempo-Accounts, Tasks im Epic – oder intern ohne Jira. |
| **Projekte** | Jira-Epic + Quellordner + GitLab, in Ordnern gruppiert; Tickets, Branch und Pipeline auf einen Blick. |
| **Automationen** | WENN/DANN-Regeln: Agent fertig → Jira-Kommentar, Pipeline rot → Fix-Agent, Feierabend → Stunden prüfen, mit Testlauf. |
| **Doku** | Vernetzte Projektdoku unter `/docs` im Repo: Architektur, Verlauf (Tickets ↔ Commits ↔ MRs ↔ Sessions), Projektwissen. |
| **Customize** | Permission-Regeln mit Git-Matrix, Skript-Bibliothek zum Token-Sparen, Integrationen und Auto-Update. |
| **Extensions** | Agent-Vorlagen, Aktionen, Rezepte, Regeln und eigene Tools aus GitHub – z. B. [Obsidian](extensions/copit-obsidian) und [MCP-Auswertung](extensions/copit-mcp). |

## Installation

**Windows-App:** [Neuestes Release](https://github.com/Copit-Code/Copit/releases/latest) öffnen und `Copit-Setup-X.Y.Z.exe` ausführen – Installation pro Benutzer, keine Admin-Rechte nötig. Alternativ das portable ZIP entpacken und `Copit.exe` starten. Copit aktualisiert sich danach selbst.

**VSCodium-Extension:**

```powershell
./scripts/setup-vscodium.ps1 -Repo "Copit-Code/Copit"
```

Voraussetzung für Agents ist mindestens eine der CLIs: [Claude Code](https://docs.claude.com/en/docs/claude-code) (`claude`) oder [GitHub Copilot CLI](https://github.com/github/copilot-cli) (`copilot`). Details in [Erste Schritte](docs/erste-schritte.md).

## Extensions

```
Copit-Code/Copit/extensions/copit-obsidian   Wissensgraph im Obsidian-Vault
Copit-Code/Copit/extensions/copit-mcp        MCP-Server wählen und Auswertungen prompten
Copit-Code/Copit/extensions/copit-dotnet     Vorlagen und dotnet test für .NET-Projekte
```

In Copit → **Extensions** eine dieser Quellen eintragen. Eigene Extensions: [docs/extensions.md](docs/extensions.md).

## Repository

```
src/Copit/            Windows-App (.NET MAUI)
src/Copit.Core/       Kern: Agents/CLI-Runner, Jira, GitLab, Git, Zeitbuchung, Doku, Extensions
vscode/copit/         VSCodium-Extension (TypeScript)
extensions/           Mitgelieferte Copit-Extensions
tests/                Unit- und Integrationstests (.NET)
installer/            Inno-Setup-Skript
docs/                 Dokumentation, Release-Notes, Logo
```

Bauen, testen und Releases: [docs/entwicklung.md](docs/entwicklung.md).
