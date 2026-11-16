using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Extensions: aus GitHub installieren (Release oder Repo-Ordner), lokal entwickeln, aktualisieren, entfernen.</summary>
public class ExtensionsView : ContentView, IActivatable
{
    private readonly ExtensionService _ext;
    private readonly PermissionService _perms;
    private readonly ToastService _toast;
    private readonly Grid _root = new();
    private string _source = "";
    private bool _busy;
    private string _status = "";

    public ExtensionsView(ExtensionService ext, PermissionService perms, ToastService toast)
    {
        _ext = ext; _perms = perms; _toast = toast;
        BackgroundColor = C("Bg");
        Content = _root;
        ext.Changed += () => MainThread.BeginInvokeOnMainThread(Render);
    }

    public void OnActivated(object? parameter) => Render();

    private void Render()
    {
        var page = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 1100, HorizontalOptions = LayoutOptions.Start };
        page.Add(Row((Stack(3, Caption("Extensions"), H1("Erweiterungen"),
            Wrap("Erweiterungen kommen aus GitHub-Repositories: Agent-Vorlagen, Automations-Aktionen (Skripte), Automations-Rezepte und Permission-Regeln. Copit hält sie automatisch aktuell.", 12, "Muted")), Star),
            (Ghost("Nach Updates suchen", async () =>
            {
                _status = "Prüfe Updates…"; Render();
                try { var n = await _ext.CheckUpdatesAsync(); _status = n == 0 ? "Alle Extensions sind aktuell." : $"{n} Update(s) verfügbar."; }
                catch (Exception e) { _status = e.Message; }
                Render();
            }), Auto)));

        // Installieren
        page.Add(Ui.Card(Stack(10,
            Caption("Aus GitHub installieren"),
            Row((Input(_source, "owner/repo · owner/repo/ordner · owner/repo@v1.2.0 · https://github.com/…", v => _source = v), Star),
                (_busy ? new ActivityIndicator { IsRunning = true, Color = C("Accent"), WidthRequest = 20, HeightRequest = 20 } : Primary("Installieren", () => _ = InstallAsync(_source)), Auto),
                (Ghost("Lokaler Ordner…", () => _ = InstallLocalAsync()), Auto)),
            Text(_status, 12, "Text2"),
            Wrap("Öffentliche Repos funktionieren ohne Anmeldung, private brauchen den GitHub-Token aus Customize → Updates. Beispiel: Copit-Code/Copit/extensions/copit-dotnet", 11, "Muted"))));

        // Installiert
        page.Add(Caption($"Installiert ({_ext.Installed.Count})"));
        if (_ext.Installed.Count == 0) page.Add(Ui.Card(Text("Noch keine Extensions installiert.", 12.5, "Muted")));
        foreach (var e in _ext.Installed.OrderBy(x => x.Manifest.Name)) page.Add(Card(e));

        // Veröffentlichen
        page.Add(Ui.Card(Stack(8, Caption("Eigene Extension veröffentlichen"),
            Wrap("1. Ordner mit copit-extension.json anlegen (id, name, version, actions, agentTemplates, automations, permissions).\n" +
                 "2. In ein GitHub-Repo pushen – Installation dann über owner/repo bzw. owner/repo/ordner.\n" +
                 "3. Für Versionen: Tag vX.Y.Z pushen; der Workflow aus extensions/template baut <id>.copit.zip als Release-Asset. Copit erkennt neue Releases und aktualisiert.\n" +
                 "Ausführliche Anleitung: docs/extensions.md im Copit-Repo.", 12, "Text2"))));
        _root.Children.Clear();
        _root.Add(new ScrollView { Content = page });
    }

    private View Card(InstalledExtension e)
    {
        var m = e.Manifest;
        var chips = new HorizontalStackLayout { Spacing = 6 };
        if (m.AgentTemplates.Count > 0) chips.Add(Badge($"{m.AgentTemplates.Count} Agent-Vorlagen", C("Cyan")));
        if (m.Actions.Count > 0) chips.Add(Badge($"{m.Actions.Count} Aktionen", C("Accent")));
        if (m.Automations.Count > 0) chips.Add(Badge($"{m.Automations.Count} Automationen", C("Green")));
        if (m.Permissions.Count > 0) chips.Add(Badge($"{m.Permissions.Count} Regeln", C("Amber")));
        var src = e.IsLocal ? $"lokal · {e.Source}" : $"{e.Source}{(e.Ref != null ? " @ " + e.Ref : "")}{(e.FromRelease ? " (Release)" : e.Revision != null ? $" ({e.Revision[..Math.Min(7, e.Revision.Length)]})" : "")}";
        var head = Row(
            (Toggle(e.Enabled, v => _ = _ext.SetEnabledAsync(e, v)), Auto),
            (Stack(2, Row((Text(m.Name, 14, "Text", true), Auto), (Mono(m.Version, 11, "Muted"), Auto), (new BoxView(), Star)), Text(src, 11, "Muted")), Star),
            (e.UpdateAvailable != null || e.IsLocal ? Btn(e.IsLocal ? "Neu laden" : "Aktualisieren", () => _ = UpdateAsync(e), e.IsLocal ? "Btn" : "Primary") : new BoxView { WidthRequest = 0 }, Auto),
            (!e.IsLocal && ExtensionService.ParseSource(e.Source) is { } s ? IconBtn("external", () => _ = Dialogs.OpenUrl($"https://github.com/{s.Owner}/{s.Repo}"), "Auf GitHub öffnen") : new BoxView { WidthRequest = 0 }, Auto),
            (IconBtn("folder", () => Dialogs.OpenFolder(e.Dir), "Ordner öffnen"), Auto),
            (IconBtn("trash", async () =>
            {
                if (!await Dialogs.Confirm($"{m.Name} entfernen?", "Die Extension wird gelöscht. Ihre Automationen ebenfalls entfernen?", "Entfernen")) return;
                await _ext.UninstallAsync(e, true);
                _toast.Success("Extension entfernt", m.Name);
            }, "Entfernen"), Auto));
        var body = Stack(10, head);
        if (m.Description.Length > 0) body.Add(Wrap(m.Description, 12, "Text2"));
        if (chips.Children.Count > 0) body.Add(chips);
        foreach (var t in m.AgentTemplates) body.Add(Row((Glyph("bot", 12, "Muted"), Auto), (Text($"{t.Title}{(t.Description.Length > 0 ? " – " + t.Description : "")}", 12, "Text2"), Star)));
        foreach (var a in m.Actions) body.Add(Row((Glyph("terminal", 12, "Muted"), Auto), (Text(a.Label.Length > 0 ? a.Label : a.Id, 12, "Text2"), Auto), (Mono(a.Run, 10.5, "Muted"), Star)));
        if (!e.Enabled) body.Opacity = 0.6;
        return Ui.Card(body, stroke: e.UpdateAvailable != null ? "Accent" : "Line");
    }

    private async Task<bool> ConfirmInstall(Core.Services.ExtensionManifest m, bool update)
    {
        var text = ExtensionService.Describe(m) + (m.Actions.Count > 0 ? "\n\nAktionen führen Befehle auf deinem Rechner aus – nur Extensions aus vertrauenswürdigen Quellen installieren." : "");
        return await Dialogs.Confirm(update ? $"{m.Name} aktualisieren?" : $"{m.Name} installieren?", text, update ? "Aktualisieren" : "Installieren");
    }

    private async Task InstallAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _busy) return;
        _busy = true; _status = "Lade aus GitHub…"; Render();
        try
        {
            var p = await _ext.PrepareAsync(input);
            var existing = _ext.Installed.Any(x => x.Manifest.Id == p.Manifest.Id);
            if (!await ConfirmInstall(p.Manifest, existing)) { _status = "Abgebrochen."; return; }
            var applyPerms = p.Manifest.Permissions.Count > 0 && await Dialogs.Confirm("Regeln übernehmen?", string.Join("\n", p.Manifest.Permissions.Select(x => $"{x.Level}  {x.Pattern}")), "Übernehmen", "Nein");
            await _ext.CommitAsync(p.Manifest, p.StagedDir, p.Source.Display.Split('@')[0] + (p.Source.Ref != null ? "@" + p.Source.Ref : ""), false, p.Ref, p.Revision, p.Release, _perms, applyPerms);
            _status = $"{p.Manifest.Name} {p.Manifest.Version} installiert.";
            _source = "";
            _toast.Success("Extension installiert", p.Manifest.Name);
        }
        catch (Exception e) { _status = e.Message; }
        finally { _busy = false; Render(); }
    }

    private async Task InstallLocalAsync()
    {
        var dir = await Dialogs.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            var (m, d) = ExtensionService.PrepareLocal(dir);
            if (!await ConfirmInstall(m, _ext.Installed.Any(x => x.Manifest.Id == m.Id))) return;
            var applyPerms = m.Permissions.Count > 0 && await Dialogs.Confirm("Regeln übernehmen?", string.Join("\n", m.Permissions.Select(x => $"{x.Level}  {x.Pattern}")), "Übernehmen", "Nein");
            await _ext.CommitAsync(m, d, dir, true, null, null, false, _perms, applyPerms);
            _toast.Success("Extension installiert", $"{m.Name} (lokal)");
        }
        catch (Exception e) { _status = e.Message; Render(); }
    }

    private async Task UpdateAsync(InstalledExtension e)
    {
        _busy = true; _status = $"Aktualisiere {e.Manifest.Name}…"; Render();
        try { var n = await _ext.UpdateAsync(e, _perms); _status = $"{n.Manifest.Name} {n.Manifest.Version} ist aktuell."; }
        catch (Exception ex) { _status = ex.Message; }
        finally { _busy = false; Render(); }
    }
}
