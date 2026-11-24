using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>
/// Wissen: alles, was Copit sich für die Zeitbuchung merkt – Accounts/Kostenstellen pro Ticket und Jira-Projekt,
/// Ticket→Projekt-Zuordnungen bei geteiltem Epic, gelernte Thema→Ticket-Zuordnungen und Antworten auf Rückfragen.
/// Alles lässt sich ansehen, durchsuchen, ändern und löschen.
/// </summary>
public class KnowledgeView : ContentView, IActivatable
{
    private readonly DataService _data;
    private readonly TimeBookingService _time;
    private readonly ToastService _toast;
    private readonly VerticalStackLayout _list = new() { Spacing = 18 };
    private string _search = "";

    public KnowledgeView(DataService data, TimeBookingService time, ToastService toast)
    {
        _data = data; _time = time; _toast = toast;
        BackgroundColor = C("Bg");
        var page = new VerticalStackLayout { Spacing = 18, Padding = new Thickness(26, 22, 26, 30) };
        page.Add(Row(
            (Stack(3, Caption("Zeitbuchung"), H1("Wissen"),
                Wrap("Was Copit sich beim Buchen merkt. Antippen zum Bearbeiten, Papierkorb zum Löschen. Gilt für alle künftigen Tage.", 12.5, "Muted")), Star),
            (IconBtn("refresh", Render, "Neu laden"), Auto)));
        page.Add(Input("", "Durchsuchen: Ticket, Account, Projekt, Thema…", s => { _search = s.Trim(); Render(); }));
        page.Add(_list);
        Content = new ScrollView { Content = page };
    }

    public void OnActivated(object? parameter) => Render();

    private bool Hit(params string?[] s) => _search.Length == 0 || s.Any(x => x?.Contains(_search, StringComparison.OrdinalIgnoreCase) == true);

    private void Render()
    {
        _list.Children.Clear();
        var acc = _data.Accounts.Value;
        var mem = _data.TimeMemory.Value;
        var projects = _data.Projects.Value;

        // ---- Accounts / Kostenstellen
        var accRows = new List<View>();
        foreach (var (k, v) in acc.ProjectAccounts.OrderBy(x => x.Key).Where(x => Hit(x.Key, x.Value)).ToList())
            accRows.Add(Item($"Jira-Projekt {k}", "gilt für alle Tickets des Projekts ohne eigenen Account", v,
                () => _ = EditAccountAsync(acc.ProjectAccounts, k, $"Account für Jira-Projekt {k}"),
                () => _ = RemoveAsync(() => acc.ProjectAccounts.Remove(k), _data.Accounts.SaveAsync)));
        foreach (var (k, v) in acc.IssueAccounts.OrderBy(x => x.Key).Where(x => Hit(x.Key, x.Value)).ToList())
            accRows.Add(Item(k, "Ticket", v,
                () => _ = EditAccountAsync(acc.IssueAccounts, k, $"Account für {k}"),
                () => _ = RemoveAsync(() => acc.IssueAccounts.Remove(k), _data.Accounts.SaveAsync)));
        _list.Add(Section("Accounts / Kostenstellen",
            "Gemerkt pro Ticket und pro Jira-Projekt – gilt, wenn am Ticket selbst kein Account steht.",
            Chip("+ Account", () => _ = AddAccountAsync()), accRows,
            "Noch keine Accounts gemerkt. Copit merkt sie sich, wenn du beim Buchen einen Account wählst."));

        // ---- Ticket → Projekt (geteiltes Epic)
        var asgRows = new List<View>();
        foreach (var (k, pid) in projects.TicketAssignments.OrderBy(x => x.Key).ToList())
        {
            var p = projects.Projects.FirstOrDefault(x => x.Id == pid);
            if (!Hit(k, p?.Name, p?.EpicKey)) continue;
            asgRows.Add(Item(k, p == null ? "Projekt gelöscht" : $"Epic {p.EpicKey}", p?.Name ?? "?",
                () => _ = ReassignAsync(k, pid),
                () => _ = RemoveAsync(() => projects.TicketAssignments.Remove(k), _data.Projects.SaveAsync)));
        }
        _list.Add(Section("Ticket → Projekt",
            "Manuelle Zuordnung, wenn sich mehrere Projekte ein Epic teilen (Projekt → Ticket antippen → „Projekt zuordnen…“).",
            null, asgRows, "Keine manuellen Zuordnungen."));

        // ---- Thema / Repo → Ticket
        var topicRows = new List<View>();
        foreach (var t in mem.Topics.OrderByDescending(t => t.Manual).ThenByDescending(t => t.Hits).Where(t => Hit(t.Topic, t.Repo, t.Ticket)).ToList())
            topicRows.Add(Item(t.Topic, string.Join(" · ", new[] { t.Repo, t.Manual ? "fest" : $"{t.Hits}× gebucht" }.Where(x => !string.IsNullOrWhiteSpace(x))), t.Ticket,
                () => _ = EditTopicAsync(t),
                () => _ = RemoveAsync(() => mem.Topics.Remove(t), _data.TimeMemory.SaveAsync)));
        _list.Add(Section("Gelernte Zuordnungen",
            "Thema oder Repo → Ticket. Copit lernt sie beim Buchen und wenn du ein Ticket korrigierst.",
            Chip("+ Zuordnung", () => _ = AddTopicAsync()), topicRows, "Noch nichts gelernt."));

        // ---- Antworten auf Rückfragen
        var factRows = new List<View>();
        foreach (var f in mem.Facts.AsEnumerable().Reverse().Where(f => Hit(f.Question, f.Answer)).ToList())
            factRows.Add(Item(f.Answer, f.Question, null,
                () => _ = EditFactAsync(f),
                () => _ = RemoveAsync(() => mem.Facts.Remove(f), _data.TimeMemory.SaveAsync), wrap: true));
        _list.Add(Section("Gelerntes Wissen",
            "Deine Antworten auf Rückfragen der KI.", null, factRows, "Noch keine Antworten gespeichert."));
    }

    private static View Section(string title, string sub, View? action, List<View> rows, string empty)
    {
        View head = action == null
            ? Stack(2, Caption(title.ToUpperInvariant()), Wrap(sub, 11.5, "Muted"))
            : Row((Stack(2, Caption(title.ToUpperInvariant()), Wrap(sub, 11.5, "Muted")), Star), (action, Auto));
        var body = new VerticalStackLayout { Spacing = 4 };
        if (rows.Count == 0) body.Add(Wrap(empty, 12, "Muted"));
        foreach (var r in rows) body.Add(r);
        return Card(Stack(10, head, body));
    }

    private static View Item(string title, string detail, string? value, Action edit, Action delete, bool wrap = false)
    {
        var text = Stack(1,
            wrap ? Wrap(title, 13, "Text") : Text(title, 13, "Text", bold: true),
            wrap ? Wrap(detail, 11, "Muted") : Text(detail, 11, "Muted"));
        var row = Row(
            (text, Star),
            (value == null ? new BoxView { WidthRequest = 0 } : (View)Mono(value, 12, "Cyan", bold: true), Auto),
            (IconBtn("pencil", edit, "Bearbeiten", 13), Auto),
            (IconBtn("trash", delete, "Löschen", 13), Auto));
        row.ColumnSpacing = 10;
        row.Padding = new Thickness(10, 7);
        row.BackgroundColor = C("Panel");
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(edit) });
        return row;
    }

    // ---------------- Bearbeiten ----------------

    private async Task RemoveAsync(Action remove, Func<Task> save)
    {
        remove();
        await save();
        Render();
    }

    private async Task EditAccountAsync(Dictionary<string, string> map, string key, string title)
    {
        var v = await Dialogs.Prompt(title, "Account (Key, z. B. ACC-12) – leer lassen zum Entfernen", map.GetValueOrDefault(key, ""));
        if (v == null) return;
        if (string.IsNullOrWhiteSpace(v)) map.Remove(key);
        else { map[key] = v.Trim(); Known(v.Trim()); }
        await _data.Accounts.SaveAsync();
        Render();
    }

    private void Known(string account)
    {
        var known = _data.Accounts.Value.KnownAccounts;
        if (!known.Contains(account)) known.Insert(0, account);
    }

    private async Task AddAccountAsync()
    {
        var key = await Dialogs.Prompt("Account merken", "Ticket (z. B. ABC-12) oder Jira-Projekt (z. B. ABC)");
        if (string.IsNullOrWhiteSpace(key)) return;
        key = key.Trim().ToUpperInvariant();
        var isIssue = TimeBookingService.IssueKeyRegex().IsMatch(key);
        var acc = await Dialogs.Prompt("Account merken", isIssue ? $"Account für Ticket {key}" : $"Account für alle Tickets im Jira-Projekt {key}");
        if (string.IsNullOrWhiteSpace(acc)) return;
        if (isIssue) await _time.RememberAccountAsync(key, acc.Trim(), false);
        else
        {
            _data.Accounts.Value.ProjectAccounts[key] = acc.Trim();
            Known(acc.Trim());
            await _data.Accounts.SaveAsync();
        }
        _toast.Success("Account gemerkt", $"{key} → {acc.Trim()}");
        Render();
    }

    private async Task ReassignAsync(string key, string projectId)
    {
        var all = _data.Projects.Value.Projects.Where(p => !string.IsNullOrWhiteSpace(p.EpicKey)).ToList();
        var current = all.FirstOrDefault(p => p.Id == projectId);
        var options = (current != null ? TicketScope.Siblings(all, current) : all);
        var pick = await Dialogs.Choose($"{key} zuordnen", [.. options.Select(p => p.Name), "Zuordnung entfernen"]);
        if (pick == null || pick == "Abbrechen") return;
        if (pick == "Zuordnung entfernen") _data.Projects.Value.TicketAssignments.Remove(key);
        else _data.Projects.Value.TicketAssignments[key] = options.First(p => p.Name == pick).Id;
        await _data.Projects.SaveAsync();
        Render();
    }

    private async Task AddTopicAsync()
    {
        var topic = await Dialogs.Prompt("Zuordnung anlegen", "Thema oder Repo (z. B. \"Weiterentwicklung Shop\" oder \"ai-backend\")");
        if (string.IsNullOrWhiteSpace(topic)) return;
        var ticket = await Dialogs.Prompt("Zuordnung anlegen", $"Auf welches Ticket gehört \"{topic}\"?");
        if (string.IsNullOrWhiteSpace(ticket)) return;
        TimeBookingService.LearnTopic(_data.TimeMemory.Value.Topics, topic, null, ticket.Trim().ToUpperInvariant(), 3);
        await _data.TimeMemory.SaveAsync();
        Render();
    }

    private async Task EditTopicAsync(TopicMapping t)
    {
        var topic = await Dialogs.Prompt("Zuordnung bearbeiten", "Thema", t.Topic);
        if (topic == null) return;
        var ticket = await Dialogs.Prompt("Zuordnung bearbeiten", $"Ticket für \"{topic}\"", t.Ticket);
        if (string.IsNullOrWhiteSpace(ticket)) return;
        t.Topic = topic.Trim(); t.Ticket = ticket.Trim().ToUpperInvariant(); t.Manual = true; t.LastUsed = DateTime.Now;
        await _data.TimeMemory.SaveAsync();
        Render();
    }

    private async Task EditFactAsync(TimeFact f)
    {
        var a = await Dialogs.Prompt("Wissen bearbeiten", f.Question, f.Answer);
        if (a == null) return;
        if (string.IsNullOrWhiteSpace(a)) _data.TimeMemory.Value.Facts.Remove(f); else f.Answer = a.Trim();
        await _data.TimeMemory.SaveAsync();
        Render();
    }
}
