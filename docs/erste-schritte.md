# Erste Schritte

## 1. Installieren

**Windows-App**
1. [Neuestes Release](https://github.com/Copit-Code/Copit/releases/latest) öffnen.
2. `Copit-Setup-X.Y.Z.exe` ausführen.
   - Copit wird pro Benutzer nach `%LOCALAPPDATA%\Programs\Copit` installiert. Admin-Rechte sind nicht nötig.
   - Alternative: `Copit-X.Y.Z-win-x64-portable.zip` entpacken und `Copit.exe` starten.
3. Copit startet im **Cockpit**.

Updates kommen automatisch. Copit prüft beim Start und alle 6 Stunden auf ein neues Release, lädt den Installer und startet danach neu. Releases aus einem privaten Fork brauchen unter **Customize → Updates** das Repo und einen GitHub-Token mit „Contents: Read-only“.

> **Hinweis:** Der Installer ist noch nicht signiert. Windows SmartScreen zeigt deshalb beim ersten Start „Der Computer wurde durch Windows geschützt“. Klicke auf **Weitere Informationen → Trotzdem ausführen**.

**VSCodium-Extension**

```powershell
./scripts/setup-vscodium.ps1 -Repo "Copit-Code/Copit"
```

Das Skript erledigt Folgendes:
- lädt VSCodium portable nach `%LOCALAPPDATA%\Copit\VSCodium`,
- installiert Copit,
- installiert Theme „Copit Dark“, Solution Explorer und C#-IntelliSense aus Open VSX,
- legt eine Desktop-Verknüpfung an.

Auch die Extension aktualisiert sich selbst: Sie prüft beim Start und stündlich das neueste Release, installiert die `.vsix` und bietet „Neu laden“ an. Für private Releases trägst du unter **Customize → Integrationen → Updates** Repository und Token ein. Ohne Token nutzt die Extension `gh auth token`.

## 2. KI-CLI bereitstellen

Copit steuert die offiziellen Kommandozeilen-Tools. Mindestens eines muss installiert und angemeldet sein:

| CLI | Installation | Anmelden |
|---|---|---|
| Claude Code | `npm i -g @anthropic-ai/claude-code` oder nativer Installer | `claude` einmal im Terminal starten |
| GitHub Copilot CLI | `npm i -g @github/copilot` | `copilot` einmal im Terminal starten |

Unter **Einstellungen → KI** erkennt Copit die Pfade automatisch, mit **Testen** prüfst du sie. Dort legst du auch fest:
- Standard-Anbieter
- Modell
- globale Anweisungen
- optional die [Skript-Bibliothek](funktionen.md#customize-und-einstellungen) zum Token-Sparen

## 3. Jira und GitLab verbinden (optional)

Unter **Einstellungen**:
- **Jira:** URL, Token (Bearer oder Basic). Optional Tempo (Account-Attribut), Account-Feld, Epic-Link-Feld und Standard-Issue-Typ.
- **GitLab:** URL und Personal Access Token mit `read_api`. Für Merge Requests und Pipeline-Aktionen braucht der Token `api`.

Ohne Jira funktioniert Copit trotzdem. Die Zeitbuchung landet dann intern in `%LOCALAPPDATA%\Copit\worklogs.json`, auch ohne Ticket.

## 4. Erstes Projekt

1. **Projekte → Neues Projekt** öffnen.
2. Jira-Epic eintragen (z. B. `SHOP-100`) und mit **Prüfen** bestätigen.
3. Quellordner wählen. Das GitLab-Projekt erkennt Copit aus dem Git-Remote.
4. Optional:
   - **Ordner** in der Projektliste, z. B. `Kunde A/Backend`
   - **KI-Anweisungen** für dieses Projekt, z. B. „.NET 9, xUnit, Commits auf Englisch“

## 5. Erster Agent

1. **Agents → Neuer Agent** öffnen.
2. Ausfüllen:
   - Projekt
   - Titel und Ticket
   - Auftrag
   - Modus: *Fragen*, *Plan*, *Edit* oder *Auto*
3. Mit Branch und „eigener worktree“ arbeitet der Agent parallel, ohne deinen Arbeitsstand zu berühren.

Die Karte wandert über das Board: Inbox → In Progress → Review → Done. Ein Klick öffnet die Session mit Verlauf, Tool-Aufrufen, Sub-Agents und Kennzahlen. Unter **Änderungen** siehst du den Code des Tickets.

## Wo liegen die Daten?

Alles liegt lokal unter `%LOCALAPPDATA%\Copit`:
- Projekte, Sessions, Automationen, Budgets und Einstellungen.
- Secrets wie Tokens, verschlüsselt mit Windows DPAPI.

Den Pfad zeigt **Einstellungen** unten an.
