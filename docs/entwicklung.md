# Entwicklung

## Aufbau

| Ordner | Inhalt |
|---|---|
| `src/Copit.Core/` | Kern ohne UI (.NET 9): CLI-Runner für Claude/Copilot, Jira-, GitLab- und Git-Clients, Zeitbuchung, Activity, Budgets, Automationen, Doku, Extensions, MCP, Skript-Bibliothek, Projektordner, Ticket-Änderungen |
| `src/Copit/` | Windows-App (.NET MAUI, WinUI). Views sind teils XAML, teils im Code gebaut (`Views/Ui.cs`). |
| `vscode/copit/` | VSCodium-Extension (TypeScript, sql.js) |
| `extensions/` | Mitgelieferte Copit-Extensions (`copit-obsidian`, `copit-mcp`, `copit-dotnet`, `template`) |
| `tests/Copit.Core.Tests/` | xUnit-Tests für den Kern, inklusive Läufen gegen echte Git-Repos und PowerShell-Skripte |
| `installer/` | Inno-Setup-Skript für den Windows-Installer |
| `scripts/` | Setup von VSCodium, Release-Skript, Startskripte |
| `docs/` | Dokumentation, Release-Notes (`docs/releases/vX.Y.Z.md`), Logo (`docs/assets/`) |

## .NET

```powershell
dotnet test tests/Copit.Core.Tests          # Kern-Tests (laufen auch unter Linux)
dotnet build src/Copit/Copit.csproj         # Windows-App (braucht das Workload maui-windows)
```

- `global.json` legt das .NET 9 SDK fest (`rollForward: latestFeature`). Grund: Das MAUI-10-Workload war auf dem Entwicklungsrechner unvollständig. Nach `dotnet workload repair` (Admin) kann auf .NET 10 umgestellt werden.
- **Optionale Integrationstests:**
  - Die Tests für die PowerShell-Skripte laufen nur, wenn `pwsh` installiert ist. Auf den GitHub-Runnern ist es vorinstalliert.
  - `COPIT_IT=1` aktiviert den Test, der eine Extension echt aus GitHub installiert.
- Für Tests und Demos lässt sich der Datenordner mit `COPIT_HOME` umlenken.
- `COPIT_START_ROUTE` öffnet beim Start eine bestimmte Seite, z. B. `board`, `changes` oder `settings`.

## VSCodium-Extension

```powershell
cd vscode/copit
npm install
npm test          # Unit-Tests
npm run build     # dist/
npm run package   # ../../dist/copit.vsix
```

Zum Ausprobieren mit Beispieldaten `COPIT_DEMO=1` setzen. Mit `COPIT_OPEN=board|agent|calendar|budgets|customize|automations` öffnet eine bestimmte Ansicht.

## CI

`.github/workflows/ci.yml` läuft bei Pushes auf `main` und bei Pull Requests:
- Typecheck, Tests und Build der VSCodium-Extension,
- Kern-Tests (.NET) unter Linux.

## Release

Der Workflow `.github/workflows/release.yml` baut und veröffentlicht:
- die Windows-App, selbstständig (self-contained),
- den Installer `Copit-Setup-X.Y.Z.exe` (Inno Setup),
- das portable ZIP,
- die `.vsix`.

Gestartet wird er auf zwei Wegen:

1. **Tag pushen**, z. B. mit `./scripts/release.ps1 0.5.0` (setzt die Extension-Version, committet, taggt und pusht).
2. **Manuell** unter Actions → Release → „Run workflow“ mit `version` und `publish`. Tag und Release werden dann auf dem gebauten Commit angelegt. Ohne `publish` entsteht ein Entwurf.

Die Version der Windows-App steht in `src/Copit/Copit.csproj`. Der Workflow überschreibt sie beim Build mit der Release-Version.

Liegt `docs/releases/vX.Y.Z.md` vor, wird die Datei zum Release-Text. Darunter hängt GitHub die automatisch erzeugten Änderungen an.

App und Extension holen sich neue Releases selbst über den Auto-Updater.

## Konventionen

- Oberfläche und Texte sind deutsch, sachlich und ohne Emojis.
- Design: hartkantig, kein Eckenradius, Mono-Labels, ein Akzent (`#E8743B`) auf dunklem Grund (`#14110F`).
- Daten sind lokale JSON-Dateien unter `%LOCALAPPDATA%\Copit` (`JsonStore`, atomisches Schreiben). Secrets liegen im Windows-Tresor (DPAPI).
- Neue Kernlogik gehört nach `Copit.Core`, damit sie ohne UI testbar ist.
