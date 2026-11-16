# Copit – Agent IDE

Copit macht **VSCodium** zur Agent-IDE: Editor, Solution Explorer und C#-Code-Vervollständigung
von VSCodium, dazu KI-Agents (Claude CLI / Copilot CLI) mit Kanban-Board, Flow-Ansicht, Kalender,
Budgets, Permission-Regeln und Automationen für Git, GitLab und Jira. Alles lokal (SQLite).

```
vscode/copit/          VSCodium-Extension (TypeScript)  ← Hauptprodukt
src/Copit.Core/        .NET-Bibliothek (Jira, GitLab, Zeitbuchung, CLI-Runner)
src/Copit/             .NET-MAUI-Cockpit (Zeitbuchung, Desktop-App)
tests/                 Unit-Tests (.NET)
scripts/               Setup & Release
.github/workflows/     CI und Release (baut die .vsix)
```

## Installation (Windows)

```powershell
./scripts/setup-vscodium.ps1 -Repo "<owner>/copit"
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

Setzt die Version, taggt `v0.2.0` und pusht – der Workflow `release.yml` baut und veröffentlicht die `.vsix`.

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
