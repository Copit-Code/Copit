# Copit – Agent IDE

Copit gibt es als **Windows-App** (.NET MAUI) und als **VSCodium-Extension**. Beide bringen KI-Agents
(Claude CLI / Copilot CLI) mit Kanban-Board, Activity (AI time / Agent time / Tokens / Dollar), Budgets,
Permission-Regeln mit Git-Matrix, Automation-Builder für Git, GitLab und Jira sowie die
Jira-Zeitbuchung mit KI (Rückfragen, gelernte Zuordnungen, Accounts, Tasks im Epic). Alles lokal.

```
vscode/copit/          VSCodium-Extension (TypeScript)  ← Hauptprodukt
src/Copit.Core/        .NET-Bibliothek (Jira, GitLab, Zeitbuchung, CLI-Runner)
src/Copit/             Windows-App (.NET MAUI)
installer/             Inno-Setup-Skript für den Windows-Installer
tests/                 Unit-Tests (.NET)
scripts/               Setup & Release
.github/workflows/     CI und Release (Installer, portables ZIP, .vsix)
```

## Installation – Windows-App

Neuestes Release unter **Releases** öffnen und `Copit-Setup-X.Y.Z.exe` ausführen
(Installation pro Benutzer nach `%LOCALAPPDATA%\Programs\Copit`, keine Admin-Rechte nötig).
Alternativ `Copit-X.Y.Z-win-x64-portable.zip` entpacken und `Copit.exe` starten.

Updates: Copit prüft beim Start und alle 6 Stunden das neueste Release, lädt den Installer und
aktualisiert sich still (Copit startet danach neu). Das Repository ist öffentlich – ein Token ist nicht nötig.
Nur wenn du Releases aus einem privaten Fork beziehst: unter **Customize → Updates** Repo und einen
GitHub-Token mit „Contents: Read-only“ hinterlegen.

## Doku, Extensions, ohne Jira

- **Doku:** Copit → Doku erzeugt pro Projekt verlinktes Markdown unter `/docs` im Repo – Code & Architektur,
  Arbeit & Verlauf (Tickets ↔ Commits ↔ MRs ↔ Agent-Sessions ↔ Zeitbuchungen) und Projektwissen.
  Ein Doku-Agent kann Beschreibungen und Entscheidungen ergänzen; er darf nur unter `docs/` schreiben.
- **Extensions:** Agent-Vorlagen, Automations-Aktionen, Rezepte und Regeln aus GitHub – siehe [docs/extensions.md](docs/extensions.md).
- **Ohne Jira:** Die Zeitbuchung funktioniert auch ohne Jira-Verbindung; gebucht wird dann intern
  (`%LOCALAPPDATA%\Copit\worklogs.json`), auch ohne Ticket.

## Installation – VSCodium-Extension

```powershell
./scripts/setup-vscodium.ps1 -Repo "Copit-Code/Copit"
```

Lädt VSCodium portable nach `%LOCALAPPDATA%\Copit\VSCodium`, installiert Copit (neuestes Release),
Theme „Copit Dark“, Solution Explorer und C#-IntelliSense aus Open VSX und legt eine Desktop-Verknüpfung an.
Für private Releases wird die GitHub CLI benötigt (`winget install GitHub.cli`, `gh auth login`).

## Auto-Update

In Copit → **Customize → Integrationen → Updates** das Repository (`owner/copit`) und ggf. ein GitHub-Token
hinterlegen (sonst wird `gh auth token` genutzt). Copit prüft beim Start und stündlich das neueste Release,
lädt die `.vsix`, installiert sie und bietet „Neu laden“ an.

## Release erstellen

```powershell
./scripts/release.ps1 0.2.0
```

Setzt die Version, taggt `v0.2.0` und pusht – der Workflow `release.yml` baut auf GitHub den
Windows-Installer (Inno Setup), ein portables ZIP und die `.vsix` und veröffentlicht alles als Release.

## Entwicklung

```powershell
cd vscode/copit
npm install
npm test          # Unit-Tests
npm run build     # dist/
npm run package   # ../../dist/copit.vsix
```

Zum Ausprobieren mit Beispieldaten: `COPIT_DEMO=1` und optional `COPIT_OPEN=board|agent|calendar|budgets|customize|automations` setzen.

## .NET-Teil

```powershell
dotnet test tests/Copit.Core.Tests
dotnet build src/Copit/Copit.csproj
```

Hinweis: Auf dem Entwicklungsrechner ist das MAUI-10-Workload unvollständig (Manifest 10.0.20 ohne Packs),
daher ist `global.json` auf .NET 9 festgelegt. Nach `dotnet workload repair` (Admin) kann auf .NET 10 umgestellt werden.
