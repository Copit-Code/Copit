using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

/// <summary>Bereits gebuchter Worklog in der Tagesansicht, mit Account-Status.</summary>
public partial class WorklogRow(JiraWorklog log, bool accountRequired, bool canFix) : ObservableObject
{
    public JiraWorklog Log { get; } = log;
    public string IssueKey => Log.IssueKey;
    public string Comment => Log.Comment;
    public double Hours => Log.Hours;
    public bool ShowAccount => accountRequired;
    public bool CanFix => accountRequired && canFix && Log.AccountChecked && !Log.HasAccount;
    public string AccountText => !Log.AccountChecked ? "ACCOUNT UNBEKANNT"
        : Log.HasAccount ? $"ACCOUNT {Log.Account}"
        : CanFix ? "KEIN ACCOUNT – TIPPEN ZUM NACHTRAGEN" : "KEIN ACCOUNT";
    public Color AccountColor => (Color)Application.Current!.Resources[!Log.AccountChecked ? "Muted" : Log.HasAccount ? "Green" : "Amber"];
    public void Refresh() { foreach (var n in new[] { nameof(CanFix), nameof(AccountText), nameof(AccountColor) }) OnPropertyChanged(n); }
}

public partial class DayCell : ObservableObject
{
    public DateOnly Date { get; init; }
    public int Day => Date.Day;
    public bool InMonth { get; init; }
    public bool IsToday => Date == DateOnly.FromDateTime(DateTime.Today);
    public bool IsWorkDay { get; init; }
    public bool IsFuture => Date > DateOnly.FromDateTime(DateTime.Today);
    public string WeekDay => Date.ToString("ddd", CultureInfo.GetCultureInfo("de-DE"));

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Fraction), nameof(StateColor), nameof(HoursText), nameof(Missing))]
    public partial double Booked { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Fraction), nameof(StateColor), nameof(HoursText), nameof(Missing))]
    public partial double Target { get; set; }
    [ObservableProperty] public partial bool HasNote { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool HasDraft { get; set; }

    // Aufteilung nach Account (nur wenn Accounts Pflicht sind: Tempo oder Account-Feld)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SplitVisible), nameof(PlainBarVisible), nameof(SplitText), nameof(SplitTooltip), nameof(HasMissingAccount))]
    public partial bool ShowSplit { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SplitText), nameof(SplitTooltip))] public partial double WithAccount { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SplitText), nameof(SplitTooltip), nameof(HasMissingAccount))] public partial double WithoutAccount { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SplitText), nameof(SplitTooltip))] public partial double UnknownAccount { get; set; }
    public bool SplitVisible => ShowSplit && Booked > 0;
    public bool PlainBarVisible => !SplitVisible;
    public bool HasMissingAccount => ShowSplit && WithoutAccount > 0.001;
    /// <summary>Balkenlänge: Soll, oder mehr, wenn mehr gebucht ist.</summary>
    public double BarScale => Math.Max(Target, Booked);
    public FormattedString SplitText
    {
        get
        {
            var fs = new FormattedString();
            if (!SplitVisible || (WithoutAccount <= 0.001 && UnknownAccount <= 0.001)) return fs;
            Color R(string k) => (Color)Application.Current!.Resources[k];
            fs.Spans.Add(new Span { Text = $"{WithAccount:0.##}", TextColor = R("Green") });
            if (WithoutAccount > 0.001) fs.Spans.Add(new Span { Text = $" · {WithoutAccount:0.##} ohne", TextColor = R("Amber") });
            if (UnknownAccount > 0.001) fs.Spans.Add(new Span { Text = $" · {UnknownAccount:0.##} ?", TextColor = R("Muted") });
            return fs;
        }
    }
    public string SplitTooltip => !SplitVisible ? "" :
        $"{WithAccount:0.##}h mit Account · {WithoutAccount:0.##}h ohne Account{(UnknownAccount > 0.001 ? $" · {UnknownAccount:0.##}h Account unbekannt" : "")}";

    public void SetHours(DayHours h, bool split)
    {
        WithAccount = h.WithAccount; WithoutAccount = h.WithoutAccount; UnknownAccount = h.Unknown;
        Booked = h.Total;
        ShowSplit = split;
        OnPropertyChanged(nameof(SplitVisible)); OnPropertyChanged(nameof(PlainBarVisible)); OnPropertyChanged(nameof(BarScale));
        OnPropertyChanged(nameof(SplitText)); OnPropertyChanged(nameof(SplitTooltip)); OnPropertyChanged(nameof(HasMissingAccount));
    }

    public double Fraction => Target > 0 ? Math.Min(1, Booked / Target) : Booked > 0 ? 1 : 0;
    public double Missing => Math.Max(0, Target - Booked);
    public string HoursText => Booked > 0 ? $"{Booked:0.##}h" : Target > 0 && !IsFuture ? "0h" : "";
    public Color StateColor => (Color)Application.Current!.Resources[
        Target > 0 && Booked + 0.01 >= Target ? "Green" :
        Booked > 0 ? "Amber" :
        Target > 0 && !IsFuture && !IsToday ? "Red" : "Muted"];
}

public partial class StepVM : ObservableObject
{
    public string Text { get; init; } = "";
    [ObservableProperty] public partial bool Done { get; set; }
}

public partial class SuggestionVM : ObservableObject
{
    public BookingSuggestion Model { get; }
    private readonly bool _accountRequired;
    public SuggestionVM(BookingSuggestion m, bool accountRequired)
    {
        Model = m; _accountRequired = accountRequired;
        _hours = m.Hours.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));
    }

    public string IssueKey => Model.IssueKey;
    public string IssueSummary => Model.IssueSummary;
    public bool NeedsTicket => string.IsNullOrWhiteSpace(Model.IssueKey);
    public bool HasTicket => !NeedsTicket;
    public string Topic => Model.Topic ?? "";
    public bool HasTopic => !string.IsNullOrWhiteSpace(Model.Topic) || !string.IsNullOrWhiteSpace(Model.Repo);
    public string TopicText => string.Join(" · ", new[] { Model.Topic, Model.Repo }.Where(x => !string.IsNullOrWhiteSpace(x)));
    [ObservableProperty] public partial string TicketInput { get; set; } = "";
    public void RefreshTicket()
    {
        foreach (var n in new[] { nameof(IssueKey), nameof(IssueSummary), nameof(NeedsTicket), nameof(HasTicket) })
            OnPropertyChanged(n);
        RefreshAccount();
    }
    public string Reason => Model.Reason;
    public bool IsManual => Model.Manual;
    public double Confidence => Model.Confidence;
    public string ConfidenceText => $"{Model.Confidence * 100:0}%";

    private string _hours;
    public string Hours
    {
        get => _hours;
        set
        {
            if (!SetProperty(ref _hours, value)) return;
            if (double.TryParse(value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) Model.Hours = h;
            HoursChanged?.Invoke();
        }
    }
    public event Action? HoursChanged;

    public string Comment { get => Model.Comment; set { Model.Comment = value; OnPropertyChanged(); } }

    public string? Account
    {
        get => Model.Account;
        set { Model.Account = value; Model.AccountFromIssue = false; RefreshAccount(); }
    }
    public bool AccountFromMemory => Model.AccountFromMemory;
    public bool NeedsAccount => _accountRequired && HasTicket && string.IsNullOrWhiteSpace(Model.Account);
    public bool HasAccount => !string.IsNullOrWhiteSpace(Model.Account);
    public bool ShowAccount => (_accountRequired || HasAccount) && HasTicket;
    public string AccountText => Model.Booked && !string.IsNullOrWhiteSpace(Model.BookedAccount) ? $"ACCOUNT {Model.BookedAccount} · GEBUCHT"
        : string.IsNullOrWhiteSpace(Model.Account) ? "KEIN ACCOUNT – TIPPEN ZUM HINTERLEGEN"
        : $"ACCOUNT {Model.Account} · {(Model.AccountFromIssue ? "AUS TICKET" : Model.AccountFromMemory ? "GEMERKT" : "MANUELL")}";
    public string HumanText => Model.HumanHours > 0 ? $"Person ohne KI ≈ {Model.HumanHours:0.##} h" : "";
    public bool HasHuman => Model.HumanHours > 0;
    public void RefreshAccount() { foreach (var n in new[] { nameof(Account), nameof(AccountText), nameof(NeedsAccount), nameof(HasAccount), nameof(ShowAccount) }) OnPropertyChanged(n); }

    [ObservableProperty] public partial bool Booked { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
}

public partial class TimeQuestionVM : ObservableObject
{
    public TimeQuestion Model { get; }
    public TimeQuestionVM(TimeQuestion q) { Model = q; Answer = q.Answer ?? ""; }
    public string Question => Model.Question;
    public List<string> Options => Model.Options;
    [ObservableProperty] public partial string Answer { get; set; }
    public bool Answered => !string.IsNullOrEmpty(Model.Answer);
    public void Refresh() => OnPropertyChanged(nameof(Answered));
}

/// <summary>Ein Eintrag im Wissen: gemerkter Account (Ticket/Jira-Projekt) oder Ticket→Projekt-Zuordnung.</summary>
/// <param name="Kind">issue | project | assign</param>
public record MemoryRow(string Kind, string Key, string Value, string Title, string Detail);

public partial class TimeViewModel : ObservableObject, IActivatable
{
    private readonly TimeBookingService _time;
    private readonly DataService _data;
    private readonly SettingsService _settings;
    private readonly ConnectionHub _hub;
    private readonly ToastService _toast;
    private readonly SessionStore _sessions;
    private readonly ShellViewModel _shell;
    private readonly ActivityService _activity;
    private CancellationTokenSource? _cts;
    private DayContext? _ctx;
    private TaskCompletionSource<string?>? _accountTcs;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public ObservableCollection<DayCell> Days { get; } = [];
    public ObservableCollection<JiraWorklog> Worklogs { get; } = [];
    public ObservableCollection<WorklogRow> WorklogRows { get; } = [];
    public ObservableCollection<StepVM> Steps { get; } = [];
    public ObservableCollection<SuggestionVM> Suggestions { get; } = [];
    public ObservableCollection<RuleSet> Rules { get; } = [];
    public ObservableCollection<string> AccountOptions { get; } = [];
    public ObservableCollection<TimeQuestionVM> Questions { get; } = [];
    public ObservableCollection<TopicMapping> Topics { get; } = [];
    public ObservableCollection<TimeFact> Facts { get; } = [];
    /// <summary>Gemerkte Accounts (Kostenstellen) pro Ticket und pro Jira-Projekt.</summary>
    public ObservableCollection<MemoryRow> AccountRows { get; } = [];
    /// <summary>Ticket → Copit-Projekt (bei geteiltem Epic).</summary>
    public ObservableCollection<MemoryRow> AssignmentRows { get; } = [];
    [ObservableProperty] public partial string MemorySearch { get; set; } = "";
    partial void OnMemorySearchChanged(string value) => LoadMemory();
    public bool HasQuestions => Questions.Any(q => !q.Answered);
    public bool HasUnassigned => Suggestions.Any(s => s.NeedsTicket && !s.Booked);

    // Quellen der Analyse
    public bool SrcTickets { get => _data.Sources.Value.Tickets; set { _data.Sources.Value.Tickets = value; SourcesChanged(); } }
    public bool SrcCommits { get => _data.Sources.Value.Commits; set { _data.Sources.Value.Commits = value; SourcesChanged(); } }
    public bool SrcGitLab { get => _data.Sources.Value.GitLab; set { _data.Sources.Value.GitLab = value; SourcesChanged(); } }
    public bool SrcSessions { get => _data.Sources.Value.Sessions; set { _data.Sources.Value.Sessions = value; SourcesChanged(); } }
    public bool SrcNote { get => _data.Sources.Value.Note; set { _data.Sources.Value.Note = value; SourcesChanged(); } }
    private void SourcesChanged()
    {
        foreach (var n in new[] { nameof(SrcTickets), nameof(SrcCommits), nameof(SrcGitLab), nameof(SrcSessions), nameof(SrcNote) }) OnPropertyChanged(n);
        _ = _data.Sources.SaveAsync();
    }
    [RelayCommand]
    private void ToggleSource(string key)
    {
        switch (key)
        {
            case "tickets": SrcTickets = !SrcTickets; break;
            case "commits": SrcCommits = !SrcCommits; break;
            case "gitlab": SrcGitLab = !SrcGitLab; break;
            case "sessions": SrcSessions = !SrcSessions; break;
            case "note": SrcNote = !SrcNote; break;
        }
    }
    public string[] WeekDays { get; } = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MonthTitle))] public partial DateOnly Month { get; set; }
    [ObservableProperty] public partial DayCell? Selected { get; set; }
    [ObservableProperty] public partial bool LoadingMonth { get; set; }
    [ObservableProperty] public partial string MonthStats { get; set; } = "";
    [ObservableProperty] public partial string MonthMissing { get; set; } = "";
    [ObservableProperty] public partial double MonthFraction { get; set; }
    [ObservableProperty] public partial string HoursToBook { get; set; } = "";
    [ObservableProperty] public partial string DayNote { get; set; } = "";
    [ObservableProperty] public partial string TargetText { get; set; } = "";
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial bool IsBooking { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string TotalText { get; set; } = "";
    [ObservableProperty] public partial bool TotalOk { get; set; }
    [ObservableProperty] public partial AiProvider Provider { get; set; }
    [ObservableProperty] public partial bool ShowRules { get; set; }
    [ObservableProperty] public partial string MinHours { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    // Account-Dialog
    [ObservableProperty] public partial bool ShowAccountDialog { get; set; }
    [ObservableProperty] public partial SuggestionVM? AccountFor { get; set; }
    [ObservableProperty] public partial string AccountInput { get; set; } = "";
    [ObservableProperty] public partial bool RememberForProject { get; set; } = true;
    [ObservableProperty] public partial string AccountDialogTitle { get; set; } = "Account";

    public string MonthTitle => Month.ToString("MMMM yyyy", De);
    public string SelectedTitle => Selected?.Date.ToString("dddd, d. MMMM", De) ?? "";
    public bool HasSelection => Selected != null;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsClaude => Provider == AiProvider.Claude;
    public bool IsCopilot => Provider == AiProvider.Copilot;
    public string BookButtonText => _time.LocalMode ? "Intern buchen" : "In Jira buchen";
    public bool IsLocalMode => _time.LocalMode;

    /// <summary>Account an einer bestehenden Buchung nachtragen (Tempo).</summary>
    [RelayCommand]
    private async Task FixWorklogAccountAsync(WorklogRow row)
    {
        if (!row.CanFix) return;
        var mem = _data.Accounts.Value;
        mem.IssueAccounts.TryGetValue(row.IssueKey, out var guess);
        if (guess == null) mem.ProjectAccounts.TryGetValue(TimeBookingService.ProjectKey(row.IssueKey), out guess);
        var probe = new SuggestionVM(new BookingSuggestion { IssueKey = row.IssueKey, IssueSummary = row.Log.IssueSummary, Hours = row.Hours, Account = guess }, true);
        var acc = await AskAccountAsync(probe);
        if (string.IsNullOrWhiteSpace(acc)) return;
        try
        {
            await _time.SetWorklogAccountAsync(row.Log, acc, RememberForProject);
            row.Refresh();
            Selected?.SetHours(TimeBookingService.Split(Worklogs), _time.AccountRequired);
            UpdateMonthStats();
            _toast.Success("Account nachgetragen", $"{row.IssueKey} · {row.Hours:0.##}h → {acc}");
        }
        catch (Exception e) { _toast.Show("Nachtragen fehlgeschlagen", e.Message, NoticeLevel.Error); }
    }

    [RelayCommand]
    private async Task DeleteWorklogAsync(JiraWorklog w)
    {
        if (!_time.LocalMode || !w.Id.StartsWith("local-")) return;
        await _time.DeleteLocalAsync(w);
        if (Selected != null) await LoadMonthAsync(Selected.Date);
    }

    public bool CanBook => HasSuggestions && !IsWorking && !IsBooking && Suggestions.Any(s => !s.Booked);
    public string BookedText => Selected == null ? "" : $"{Selected.Booked:0.##}h von {Selected.Target:0.##}h gebucht";

    public event Action? Celebrate;

    public TimeViewModel(TimeBookingService time, DataService data, SettingsService settings, ConnectionHub hub,
        ToastService toast, SessionStore sessions, ShellViewModel shell, ActivityService activity)
    {
        _time = time; _data = data; _settings = settings; _hub = hub; _toast = toast; _sessions = sessions; _shell = shell; _activity = activity;
        Questions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuestions));
        Provider = settings.Settings.Ai.DefaultProvider;
        var today = DateOnly.FromDateTime(DateTime.Today);
        Month = new DateOnly(today.Year, today.Month, 1);
        Suggestions.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasSuggestions)); OnPropertyChanged(nameof(CanBook)); OnPropertyChanged(nameof(HasUnassigned)); UpdateTotal(); };
    }

    partial void OnProviderChanged(AiProvider value) { OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsCopilot)); }
    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(CanBook));
    partial void OnIsBookingChanged(bool value) => OnPropertyChanged(nameof(CanBook));

    partial void OnSelectedChanged(DayCell? oldValue, DayCell? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(BookedText)); OnPropertyChanged(nameof(WeekTitle));
    }

    private bool _loadingNote;
    partial void OnDayNoteChanged(string value)
    {
        if (_loadingNote || Selected == null) return;
        var e = _data.Day(Selected.Date);
        e.Note = value;
        Selected.HasNote = !string.IsNullOrWhiteSpace(value);
        _ = SaveDaysDebounced();
    }

    private DateTime _lastNoteChange;
    private async Task SaveDaysDebounced()
    {
        _lastNoteChange = DateTime.Now;
        await Task.Delay(800);
        if ((DateTime.Now - _lastNoteChange).TotalMilliseconds >= 750) await _data.Days.SaveAsync();
    }

    public void OnActivated(object? parameter)
    {
        OnPropertyChanged(nameof(BookButtonText)); OnPropertyChanged(nameof(IsLocalMode));
        MinHours = _settings.Settings.Time.MinHoursPerDay.ToString("0.##", De);
        Rules.Clear();
        foreach (var r in _settings.Settings.Time.RuleSets) Rules.Add(r);
        LoadMemory();
        if (parameter is DateOnly d)
        {
            Month = new DateOnly(d.Year, d.Month, 1);
            _ = LoadMonthAsync(d);
        }
        else if (Days.Count == 0) _ = LoadMonthAsync(DateOnly.FromDateTime(DateTime.Today));
        else _ = LoadMonthAsync(Selected?.Date);
    }

    [RelayCommand] private Task PrevMonthAsync() { Month = Month.AddMonths(-1); return LoadMonthAsync(null); }
    [RelayCommand] private Task NextMonthAsync() { Month = Month.AddMonths(1); return LoadMonthAsync(null); }
    [RelayCommand]
    private Task TodayAsync()
    {
        var t = DateOnly.FromDateTime(DateTime.Today);
        Month = new DateOnly(t.Year, t.Month, 1);
        return LoadMonthAsync(t);
    }

    private async Task LoadMonthAsync(DateOnly? select)
    {
        var first = Month;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        Days.Clear();
        for (int i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            var entry = _data.PeekDay(d);
            Days.Add(new DayCell
            {
                Date = d, InMonth = d.Month == first.Month, IsWorkDay = _time.IsWorkDay(d),
                Target = _time.TargetFor(d), HasNote = !string.IsNullOrWhiteSpace(entry?.Note),
                HasDraft = entry?.LastSuggestions.Any(s => !s.Booked) == true
            });
        }
        var sel = select ?? (Selected != null ? Selected.Date : (DateOnly?)null);
        var cell = Days.FirstOrDefault(c => c.Date == sel) ?? Days.FirstOrDefault(c => c.IsToday && c.InMonth);
        if (cell != null) SelectDay(cell); else Selected = null;

        LoadingMonth = true;
        try
        {
            var booked = await _time.GetBookedSplitAsync(start, start.AddDays(41));
            var split = _time.AccountRequired;
            foreach (var c in Days) c.SetHours(booked.GetValueOrDefault(c.Date) ?? new DayHours(0, 0, 0), split);
            UpdateMonthStats();
            OnPropertyChanged(nameof(BookedText));
            if (Selected != null) await LoadWorklogsAsync(Selected.Date);
        }
        catch (Exception e) { MonthStats = "Fehler: " + e.Message; }
        finally { LoadingMonth = false; }
    }

    private void UpdateMonthStats()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var inMonth = Days.Where(d => d.InMonth).ToList();
        var booked = inMonth.Sum(d => d.Booked);
        var targetSoFar = inMonth.Where(d => d.Date <= today).Sum(d => d.Target);
        var target = inMonth.Sum(d => d.Target);
        MonthStats = $"{booked:0.#}h von {target:0.#}h";
        var missingDays = inMonth.Where(d => d.Date < today && d.Missing > 0.01).ToList();
        MonthMissing = missingDays.Count == 0 ? "Keine Lücken" : $"{missingDays.Count} Tage mit Lücken · {missingDays.Sum(d => d.Missing):0.#}h offen";
        MonthFraction = targetSoFar > 0 ? Math.Min(1, booked / targetSoFar) : 0;
        var without = inMonth.Sum(d => d.WithoutAccount);
        if (_time.AccountRequired && without > 0.001)
            MonthMissing += $" · {without:0.#}h ohne Account ({inMonth.Count(d => d.HasMissingAccount)} Tage)";
    }

    [RelayCommand]
    private void SelectDay(DayCell cell)
    {
        if (IsWorking) return;
        Selected = cell;
        var entry = _data.PeekDay(cell.Date);
        _loadingNote = true;
        DayNote = entry?.Note ?? "";
        _loadingNote = false;
        TargetText = cell.Target.ToString("0.##", De);
        HoursToBook = cell.Missing > 0 ? cell.Missing.ToString("0.##", De) : "";
        Error = "";
        Steps.Clear();
        Summary = entry?.LastSummary ?? "";
        Suggestions.Clear();
        foreach (var s in entry?.LastSuggestions ?? []) AddSuggestionVm(s);
        Questions.Clear();
        foreach (var q in entry?.Questions ?? []) Questions.Add(new TimeQuestionVM(q));
        _ = LoadWorklogsAsync(cell.Date);
    }

    private async Task LoadWorklogsAsync(DateOnly d)
    {
        Worklogs.Clear(); WorklogRows.Clear();
        try
        {
            var logs = await _time.GetWorklogsAsync(d, d);
            if (Selected?.Date != d) return;
            Worklogs.Clear(); WorklogRows.Clear();
            var canFix = _settings.Settings.Jira.UseTempo && _hub.Jira != null;
            foreach (var l in logs) { Worklogs.Add(l); WorklogRows.Add(new WorklogRow(l, _time.AccountRequired, canFix)); }
            // Kalenderzelle mit dem frischen Stand abgleichen
            Selected?.SetHours(TimeBookingService.Split(logs), _time.AccountRequired);
            UpdateMonthStats();
        }
        catch { }
    }

    private void AddSuggestionVm(BookingSuggestion s)
    {
        var vm = new SuggestionVM(s, _time.AccountRequired) { Booked = s.Booked };
        vm.HoursChanged += UpdateTotal;
        Suggestions.Add(vm);
    }

    private void UpdateTotal()
    {
        var total = Suggestions.Where(s => !s.Booked).Sum(s => s.Model.Hours);
        var want = ParseHours(HoursToBook) ?? 0;
        if (_settings.Settings.Time.HumanEffort)
        {
            TotalText = $"{total:0.##}h Personen-Aufwand" + (want > 0 ? $" · max. {want:0.##}h" : "");
            TotalOk = want <= 0 || total <= want + 0.01;
        }
        else
        {
            TotalText = $"{total:0.##}h verteilt" + (want > 0 ? $" · Ziel {want:0.##}h" : "");
            TotalOk = want <= 0 || Math.Abs(total - want) < 0.01;
        }
        OnPropertyChanged(nameof(CanBook));
    }

    partial void OnHoursToBookChanged(string value) => UpdateTotal();

    private static double? ParseHours(string? s) =>
        double.TryParse((s ?? "").Replace(',', '.').Trim().TrimEnd('h'), NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ? h : null;

    [RelayCommand] private void SetProvider(string p) => Provider = Enum.Parse<AiProvider>(p);

    [RelayCommand]
    private async Task SaveTargetAsync()
    {
        if (Selected == null) return;
        var t = ParseHours(TargetText);
        var e = _data.Day(Selected.Date);
        e.TargetHours = t is { } v && Math.Abs(v - (_time.IsWorkDay(Selected.Date) ? _settings.Settings.Time.MinHoursPerDay : 0)) > 0.001 ? v : null;
        await _data.Days.SaveAsync();
        Selected.Target = _time.TargetFor(Selected.Date);
        HoursToBook = Selected.Missing > 0 ? Selected.Missing.ToString("0.##", De) : "";
        UpdateMonthStats();
        OnPropertyChanged(nameof(BookedText));
    }

    [RelayCommand]
    private async Task AnalyzeAsync() => await RunAnalysisAsync(useAi: true);

    [RelayCommand]
    private async Task HeuristicAsync() => await RunAnalysisAsync(useAi: false);

    private async Task RunAnalysisAsync(bool useAi)
    {
        if (Selected == null || IsWorking) return;
        var hours = ParseHours(HoursToBook);
        if (hours is null or <= 0) { Error = "Bitte angeben, wie viele Stunden gebucht werden sollen."; return; }
        Error = "";
        IsWorking = true;
        Steps.Clear();
        // Eigene Buchungen bleiben erhalten und zählen als bereits eingeplant
        var manual = Suggestions.Where(s => s.Model.Manual && !s.Booked).Select(s => s.Model).ToList();
        Suggestions.Clear();
        foreach (var m in manual) AddSuggestionVm(m);
        Questions.Clear();
        Summary = "";
        _cts = new CancellationTokenSource();
        var day = Selected.Date;
        void Progress(string text) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Steps.Count > 0 && Steps[^1].Text == text) return;
            if (Steps.Count > 0) Steps[^1].Done = true;
            Steps.Add(new StepVM { Text = text });
            while (Steps.Count > 7) Steps.RemoveAt(0);
        });
        try
        {
            var (plan, ctx) = await PlanDayAsync(day, hours.Value, useAi, manual, Progress, _cts.Token);
            _ctx = ctx;
            Progress("Fertig");
            if (Steps.Count > 0) Steps[^1].Done = true;

            Summary = plan.Summary;
            foreach (var b in plan.Bookings) AddSuggestionVm(b);
            foreach (var q in plan.Questions) Questions.Add(new TimeQuestionVM(q));
            if (plan.Bookings.Count == 0 && string.IsNullOrEmpty(Summary)) Summary = "Keine passenden Tickets gefunden. Trage eine Notiz zum Tag ein und versuche es erneut.";
            var entry = _data.Day(day);
            entry.LastSuggestions = [.. manual, .. plan.Bookings];
            entry.LastSummary = plan.Summary;
            entry.Questions = plan.Questions;
            await _data.Days.SaveAsync();
            Selected.HasDraft = entry.LastSuggestions.Count > 0;
        }
        catch (OperationCanceledException) { Summary = "Abgebrochen."; }
        catch (Exception e) { Error = e.Message; }
        finally { IsWorking = false; }
    }

    /// <summary>
    /// Plan für einen Tag. Eigene Buchungen (<paramref name="manual"/>) gehen als „schon eingeplant“ in die Analyse ein –
    /// die KI verteilt nur noch den Rest und schlägt sie nicht doppelt vor.
    /// </summary>
    private async Task<(BookingPlan Plan, DayContext Ctx)> PlanDayAsync(DateOnly day, double hours, bool useAi, List<BookingSuggestion> manual,
        Action<string> progress, CancellationToken ct)
    {
        var from = day.ToDateTime(TimeOnly.MinValue);
        var daySessions = await Task.Run(() => _activity.Build(from, from.AddDays(1), _sessions.All, id => _data.Project(id)?.SourceFolder)
            .Select(a => new DaySession(a.Title, a.Project, a.Branch, a.Ticket, (int)ActivityService.Union(a.Segments).TotalMinutes)).ToList(), ct);
        if (_data.Sources.Value.Sessions && daySessions.Count > 0) progress($"KI-Sessions · {daySessions.Count} Sessions, {daySessions.Sum(x => x.Minutes)} min aktiv");
        var ctx = await _time.GatherAsync(day, 0, daySessions, progress, ct, _data.Sources.Value);
        ctx.TargetHours = ctx.BookedHours + hours;
        foreach (var w in ctx.Warnings) progress("Hinweis: " + w);
        foreach (var m in manual.Where(m => m.Hours > 0))
            ctx.ExistingWorklogs.Add(new JiraWorklog
            {
                Id = "planned-" + Guid.NewGuid().ToString("N")[..8], IssueKey = m.IssueKey, IssueSummary = m.IssueSummary,
                Started = from.AddHours(8), TimeSpentSeconds = (long)Math.Round(m.Hours * 3600),
                Comment = "(eigene Buchung, schon eingeplant) " + m.Comment,
            });
        if (manual.Count > 0) progress($"Eigene Buchungen · {manual.Sum(m => m.Hours):0.##}h schon eingeplant");

        BookingPlan plan;
        if (ctx.RemainingHours <= 0.01)
            plan = new BookingPlan { Summary = manual.Count > 0 ? "Deine eigenen Buchungen decken die Stunden schon ab." : "Für diesen Tag ist nichts mehr zu buchen." };
        else if (useAi)
        {
            try { plan = await _time.SuggestAsync(ctx, Provider, progress, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _toast.Show("KI nicht verfügbar – nutze Heuristik", e.Message, NoticeLevel.Warning);
                plan = _time.HeuristicPlan(ctx);
                await _time.ResolveAccountsAsync(plan.Bookings, ctx.Issues, progress, ct);
            }
        }
        else
        {
            plan = _time.HeuristicPlan(ctx);
            await _time.ResolveAccountsAsync(plan.Bookings, ctx.Issues, progress, ct);
        }
        return (plan, ctx);
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    // --------------- Ganze Woche ---------------

    /// <summary>Arbeitstage der Woche des gewählten Tags (Mo–So), ohne Tage in der Zukunft.</summary>
    private List<DateOnly> WeekDaysOf(DateOnly d)
    {
        var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        var today = DateOnly.FromDateTime(DateTime.Today);
        return [.. Enumerable.Range(0, 7).Select(monday.AddDays).Where(x => x <= today && _time.IsWorkDay(x))];
    }

    public string WeekTitle => Selected == null ? "" : $"KW {ISOWeek.GetWeekOfYear(Selected.Date.ToDateTime(TimeOnly.MinValue))}";

    /// <summary>Für alle Arbeitstage der Woche mit fehlenden Stunden Vorschläge erzeugen (eigene Buchungen bleiben).</summary>
    [RelayCommand]
    private async Task SuggestWeekAsync(string? mode)
    {
        if (Selected == null || IsWorking) return;
        var useAi = mode != "heuristic";
        var days = WeekDaysOf(Selected.Date);
        if (days.Count == 0) { _toast.Show("Keine Arbeitstage", "In dieser Woche gibt es (noch) keine Arbeitstage zu buchen.", NoticeLevel.Warning); return; }

        List<JiraWorklog> logs;
        try { logs = await _time.GetWorklogsAsync(days[0], days[^1]); }
        catch (Exception e) { _toast.Error(e, "Buchungen der Woche konnten nicht geladen werden"); return; }
        var todo = new List<(DateOnly Day, double Hours, List<BookingSuggestion> Manual)>();
        var skipped = new List<string>();
        foreach (var d in days)
        {
            var booked = logs.Where(l => DateOnly.FromDateTime(l.Started) == d).Sum(l => l.TimeSpentSeconds) / 3600.0;
            var missing = Math.Round(_time.TargetFor(d) - booked, 2);
            var entry = _data.PeekDay(d);
            var open = entry?.LastSuggestions.Where(s => !s.Booked).ToList() ?? [];
            if (missing <= 0.01) continue;
            if (open.Any(s => !s.Manual)) { skipped.Add(d.ToString("ddd dd.MM.", De)); continue; }   // Vorschläge schon vorhanden → nicht überschreiben
            todo.Add((d, missing, open.Where(s => s.Manual).ToList()));
        }
        if (todo.Count == 0)
        {
            _toast.Show("Nichts vorzuschlagen", skipped.Count > 0 ? $"Für {string.Join(", ", skipped)} gibt es schon Vorschläge – „Woche buchen“ bucht sie." : "Alle Arbeitstage der Woche sind gebucht.", NoticeLevel.Info);
            return;
        }
        var list = string.Join("\n", todo.Select(t => $"{t.Day.ToString("ddd dd.MM.", De)}: {t.Hours:0.##}h" + (t.Manual.Count > 0 ? $" (davon {t.Manual.Sum(m => m.Hours):0.##}h eigene)" : "")));
        if (!await Dialogs.Confirm($"Woche vorschlagen ({WeekTitle})", $"{(useAi ? "Mit KI" : "Ohne KI")} für {todo.Count} Tag(e) vorschlagen:\n{list}" +
                (skipped.Count > 0 ? $"\n\nÜbersprungen (hat schon Vorschläge): {string.Join(", ", skipped)}" : ""), "Vorschlagen")) return;

        Error = ""; IsWorking = true; Steps.Clear();
        _cts = new CancellationTokenSource();
        var done = 0; var questions = 0;
        try
        {
            foreach (var (d, hours, manual) in todo)
            {
                var label = d.ToString("ddd dd.MM.", De);
                void Progress(string text) => MainThread.BeginInvokeOnMainThread(() =>
                {
                    var t = $"{label} · {text}";
                    if (Steps.Count > 0 && Steps[^1].Text == t) return;
                    if (Steps.Count > 0) Steps[^1].Done = true;
                    Steps.Add(new StepVM { Text = t });
                    while (Steps.Count > 7) Steps.RemoveAt(0);
                });
                var (plan, _) = await PlanDayAsync(d, hours, useAi, manual, Progress, _cts.Token);
                var entry = _data.Day(d);
                entry.LastSuggestions = [.. manual, .. plan.Bookings];
                entry.LastSummary = plan.Summary;
                entry.Questions = plan.Questions;
                questions += plan.Questions.Count;
                await _data.Days.SaveAsync();
                if (Days.FirstOrDefault(c => c.Date == d) is { } cell) cell.HasDraft = entry.LastSuggestions.Count > 0;
                done++;
            }
            if (Steps.Count > 0) Steps[^1].Done = true;
            _toast.Success($"Vorschläge für {done} Tag(e) erstellt",
                (questions > 0 ? $"{questions} Rückfrage(n) offen – an den Tagen mit Punkt prüfen. " : "") + "Danach mit „Woche buchen“ alles auf einmal buchen.");
        }
        catch (OperationCanceledException) { _toast.Show("Abgebrochen", $"{done} von {todo.Count} Tagen vorgeschlagen.", NoticeLevel.Warning); }
        catch (Exception e) { Error = e.Message; }
        finally
        {
            IsWorking = false;
            if (Selected != null) SelectDay(Selected);   // Panel des gewählten Tags mit den neuen Vorschlägen füllen
        }
    }

    /// <summary>Alle offenen Vorschläge der Woche auf einmal buchen.</summary>
    [RelayCommand]
    private async Task BookWeekAsync()
    {
        if (Selected == null || IsWorking || IsBooking) return;
        var days = WeekDaysOf(Selected.Date);
        var perDay = days.Select(d => (Day: d, Items: (_data.PeekDay(d)?.LastSuggestions ?? [])
                .Where(s => !s.Booked && s.Hours > 0 && (!string.IsNullOrWhiteSpace(s.IssueKey) || _time.LocalMode)).ToList()))
            .Where(x => x.Items.Count > 0).ToList();
        if (perDay.Count == 0) { _toast.Show("Nichts zu buchen", "In dieser Woche gibt es keine offenen Vorschläge mit Ticket. Erst „Woche vorschlagen“.", NoticeLevel.Info); return; }
        var withoutTicket = days.Sum(d => (_data.PeekDay(d)?.LastSuggestions ?? []).Count(s => !s.Booked && s.Hours > 0 && string.IsNullOrWhiteSpace(s.IssueKey)));

        // Fehlende Accounts einmal pro Ticket/Projekt erfragen
        if (_time.AccountRequired)
            foreach (var s in perDay.SelectMany(x => x.Items).Where(s => string.IsNullOrWhiteSpace(s.Account)).ToList())
            {
                if (!string.IsNullOrWhiteSpace(s.Account)) continue;
                var acc = await AskAccountAsync(new SuggestionVM(s, true));
                if (acc == null) { _toast.Show("Buchung abgebrochen", $"Für {s.IssueKey} ist kein Account hinterlegt.", NoticeLevel.Warning); return; }
                foreach (var o in perDay.SelectMany(x => x.Items).Where(o => string.IsNullOrWhiteSpace(o.Account) &&
                             (o.IssueKey == s.IssueKey || (RememberForProject && TimeBookingService.ProjectKey(o.IssueKey) == TimeBookingService.ProjectKey(s.IssueKey)))))
                    o.Account = acc;
                s.Account = acc;
            }

        var lines = string.Join("\n", perDay.Select(x => $"{x.Day.ToString("ddd dd.MM.", De)}: {x.Items.Count} Buchung(en), {x.Items.Sum(i => i.Hours):0.##}h"));
        var total = perDay.Sum(x => x.Items.Sum(i => i.Hours));
        if (!await Dialogs.Confirm($"Woche buchen ({WeekTitle})", $"{lines}\n\nInsgesamt {total:0.##}h {(_time.LocalMode ? "intern speichern (ohne Jira)" : "in Jira eintragen")}?" +
                (withoutTicket > 0 ? $"\n\n{withoutTicket} Block/Blöcke ohne Ticket werden nicht gebucht." : ""), "Buchen")) return;

        IsBooking = true;
        int ok = 0, failed = 0; double hoursOk = 0;
        var errors = new List<string>();
        try
        {
            foreach (var (d, items) in perDay)
            {
                var existing = await _time.GetWorklogsAsync(d, d);
                var results = await _time.BookAsync(d, items, existing, _ => { });
                foreach (var (item, err) in results)
                {
                    if (err == null) { ok++; hoursOk += item.Hours; }
                    else { failed++; errors.Add($"{d:dd.MM.} {item.IssueKey}: {err}"); }
                }
                await _data.Days.SaveAsync();
                if (Days.FirstOrDefault(c => c.Date == d) is { } cell) cell.HasDraft = (_data.PeekDay(d)?.LastSuggestions ?? []).Any(s => !s.Booked);
            }
            if (ok > 0) { Celebrate?.Invoke(); _toast.Success($"{ok} Buchung(en) in der Woche gespeichert", $"{hoursOk:0.##}h · {WeekTitle}"); }
            if (failed > 0) _toast.Show($"{failed} Buchung(en) fehlgeschlagen", string.Join("\n", errors.Take(8)), NoticeLevel.Error);
        }
        catch (Exception e) { _toast.Error(e, "Wochenbuchung fehlgeschlagen"); }
        finally
        {
            IsBooking = false;
            await LoadMonthAsync(Selected?.Date);
            _ = _shell.RefreshHoursAsync();
        }
    }

    [RelayCommand]
    private void RemoveSuggestion(SuggestionVM s)
    {
        Suggestions.Remove(s);
        if (Selected != null) _data.Day(Selected.Date).LastSuggestions.Remove(s.Model);
        _ = _data.Days.SaveAsync();
    }

    /// <summary>Eigene Buchung anlegen – bleibt beim Vorschlagen erhalten und wird von der KI als eingeplant berücksichtigt.</summary>
    [RelayCommand]
    private async Task AddSuggestionAsync()
    {
        if (Selected == null) return;
        var day = Selected.Date;
        var key = await Dialogs.Prompt("Eigene Buchung", _time.LocalMode ? "Ticket-Key (z. B. ABC-123) oder Thema" : "Ticket-Key (z. B. ABC-123)");
        if (string.IsNullOrWhiteSpace(key)) return;
        key = key.Trim();
        var isKey = TimeBookingService.IssueKeyRegex().IsMatch(key.ToUpperInvariant());
        if (!isKey && !_time.LocalMode) { _toast.Show("Ungültiger Ticket-Key", key, NoticeLevel.Warning); return; }
        var planned = Suggestions.Where(s => !s.Booked).Sum(s => s.Model.Hours);
        var rest = Math.Max(0, (ParseHours(HoursToBook) ?? Selected.Missing) - planned);
        var hoursText = await Dialogs.Prompt("Eigene Buchung", "Stunden", rest > 0 ? rest.ToString("0.##", De) : "1");
        if (ParseHours(hoursText) is not { } h || h <= 0) return;
        var comment = await Dialogs.Prompt("Eigene Buchung", "Buchungstext (optional)", "") ?? "";
        var s = new BookingSuggestion
        {
            IssueKey = isKey ? key.ToUpperInvariant() : "", Topic = isKey ? null : key, Hours = h, Comment = comment.Trim(),
            Confidence = 1, Manual = true, Reason = "Eigene Buchung",
        };
        if (isKey && _hub.Jira != null)
        {
            var issue = await _hub.Jira.GetIssueAsync(s.IssueKey);
            if (issue == null) { _toast.Show("Ticket nicht gefunden", s.IssueKey, NoticeLevel.Warning); return; }
            s.IssueSummary = issue.Summary;
            if (string.IsNullOrWhiteSpace(s.Comment)) s.Comment = issue.Summary;
            await _time.ResolveAccountsAsync([s], [issue], _ => { });
        }
        _data.Day(day).LastSuggestions.Add(s);
        await _data.Days.SaveAsync();
        if (Selected?.Date != day) return;
        AddSuggestionVm(s);
        Selected.HasDraft = true;
        UpdateTotal();
    }

    [RelayCommand]
    private async Task EditAccountAsync(SuggestionVM s)
    {
        var acc = await AskAccountAsync(s);
        if (acc != null) s.Account = acc;
    }

    private async Task<string?> AskAccountAsync(SuggestionVM s)
    {
        AccountOptions.Clear();
        foreach (var a in _data.Accounts.Value.KnownAccounts.Take(12)) AccountOptions.Add(a);
        if (_settings.Settings.Jira.UseTempo && _hub.Jira != null)
        {
            try
            {
                foreach (var a in (await _hub.Jira.GetTempoAccountsAsync()).Take(80))
                    if (!AccountOptions.Contains(a.Key)) AccountOptions.Add(a.Key);
            }
            catch { }
        }
        AccountFor = s;
        AccountInput = s.Account ?? "";
        ShowAccountDialog = true;
        AccountDialogTitle = s.HasTicket ? $"Account für {s.IssueKey}" : "Account für den neuen Task";
        _accountTcs = new TaskCompletionSource<string?>();
        var result = await _accountTcs.Task;
        ShowAccountDialog = false;
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (s.HasTicket)
            {
                await _time.RememberAccountAsync(s.IssueKey, result.Trim(), RememberForProject);
                s.Model.AccountFromMemory = true;
            }
        }
        return result?.Trim();
    }

    [RelayCommand] private void PickAccount(string a) => AccountInput = a;
    [RelayCommand] private void ConfirmAccount() => _accountTcs?.TrySetResult(string.IsNullOrWhiteSpace(AccountInput) ? null : AccountInput);
    [RelayCommand] private void SkipAccount() => _accountTcs?.TrySetResult(null);

    [RelayCommand]
    private async Task BookAsync()
    {
        if (Selected == null) return;
        var unassigned = Suggestions.Where(s => !s.Booked && s.NeedsTicket && s.Model.Hours > 0).ToList();
        if (unassigned.Count > 0 && !_time.LocalMode && !await Dialogs.Confirm("Blöcke ohne Ticket", $"{unassigned.Count} Block/Blöcke ({unassigned.Sum(u => u.Model.Hours):0.##}h) haben noch kein Ticket und werden nicht gebucht. Trotzdem fortfahren?", "Weiter")) return;
        var open = Suggestions.Where(s => !s.Booked && (s.HasTicket || _time.LocalMode) && s.Model.Hours > 0).ToList();
        if (open.Count == 0) return;

        // Fehlende Accounts erfragen – Antworten werden gemerkt
        foreach (var s in open.Where(s => s.NeedsAccount).ToList())
        {
            var acc = await AskAccountAsync(s);
            if (acc == null) { _toast.Show("Buchung abgebrochen", $"Für {s.IssueKey} ist kein Account hinterlegt.", NoticeLevel.Warning); return; }
            s.Account = acc;
            // gleiche Tickets/Projekte direkt mit befüllen
            foreach (var o in open.Where(o => o.NeedsAccount && (o.IssueKey == s.IssueKey || (RememberForProject && TimeBookingService.ProjectKey(o.IssueKey) == TimeBookingService.ProjectKey(s.IssueKey)))))
                o.Account = acc;
        }

        var total = open.Sum(s => s.Model.Hours);
        if (!await Dialogs.Confirm("Jetzt buchen?", $"{open.Count} Buchung(en) mit insgesamt {total:0.##}h am {Selected.Date:dd.MM.yyyy} {(_time.LocalMode ? "intern speichern (ohne Jira)" : "in Jira eintragen")}?", "Buchen")) return;

        IsBooking = true;
        try
        {
            var existing = Worklogs.ToList();
            var results = await _time.BookAsync(Selected.Date, open.Select(o => o.Model), existing, _ => { });
            foreach (var (item, err) in results)
            {
                var vm = Suggestions.First(s => s.Model == item);
                vm.Booked = err == null;
                vm.Error = err ?? "";
                vm.RefreshAccount();
            }
            await _data.Days.SaveAsync();
            var ok = results.Count(r => r.Error == null);
            if (ok > 0)
            {
                Celebrate?.Invoke();
                _toast.Success($"{ok} Buchung(en) gespeichert", $"{results.Where(r => r.Error == null).Sum(r => r.Item.Hours):0.##}h am {Selected.Date:dd.MM.}");
            }
            if (ok < results.Count) _toast.Show("Teilweise fehlgeschlagen", string.Join("\n", results.Where(r => r.Error != null).Select(r => $"{r.Item.IssueKey}: {r.Error}")), NoticeLevel.Error);
            Selected.HasDraft = Suggestions.Any(s => !s.Booked);
            await LoadMonthAsync(Selected.Date);
            _ = _shell.RefreshHoursAsync();
        }
        catch (Exception e) { _toast.Error(e, "Buchung fehlgeschlagen"); }
        finally { IsBooking = false; }
    }

    // --------------- Ticket zuordnen / Rückfragen / Gedächtnis ---------------
    [RelayCommand]
    private async Task AssignTicketAsync(SuggestionVM s)
    {
        var key = (s.TicketInput ?? "").Trim().ToUpperInvariant();
        if (!TimeBookingService.IssueKeyRegex().IsMatch(key)) { _toast.Show("Ungültiger Ticket-Key", key, NoticeLevel.Warning); return; }
        await _time.CorrectTicketAsync(s.Model, key);
        if (_hub.Jira != null)
        {
            try
            {
                var issue = await _hub.Jira.GetIssueAsync(key);
                if (issue != null) { s.Model.IssueSummary = issue.Summary; await _time.ResolveAccountsAsync([s.Model], [issue], _ => { }); }
            }
            catch { }
        }
        s.TicketInput = "";
        s.RefreshTicket();
        OnPropertyChanged(nameof(HasUnassigned)); OnPropertyChanged(nameof(CanBook));
        await _data.Days.SaveAsync();
        LoadMemory();
        if (s.HasTopic) _toast.Success("Zuordnung gemerkt", $"{s.TopicText} → {key}");
    }

    /// <summary>Für einen Block ohne Ticket im Epic einen Task anlegen – mit dem Account des Epics.</summary>
    [RelayCommand]
    private async Task CreateTaskAsync(SuggestionVM s)
    {
        if (_hub.Jira == null) { _toast.Show("Jira nicht konfiguriert", "", NoticeLevel.Warning); return; }
        var guessProject = _data.Projects.Value.Projects.FirstOrDefault(p => !string.IsNullOrEmpty(s.Model.Repo) && !string.IsNullOrEmpty(p.EpicKey) && !string.IsNullOrEmpty(p.SourceFolder)
                        && (p.SourceFolder.Contains(s.Model.Repo!, StringComparison.OrdinalIgnoreCase) || s.Model.Repo!.Contains(System.IO.Path.GetFileName(p.SourceFolder.TrimEnd('\\')), StringComparison.OrdinalIgnoreCase)));
        var guess = guessProject?.EpicKey
                    ?? _data.Projects.Value.Projects.FirstOrDefault(p => !string.IsNullOrEmpty(p.EpicKey))?.EpicKey ?? "";
        var epic = await Dialogs.Prompt("Task im Epic anlegen", "Epic-Key (z. B. WEI-1 für das Weiterentwicklungsprojekt)", guess);
        if (string.IsNullOrWhiteSpace(epic)) return;
        epic = epic.Trim().ToUpperInvariant();
        var defaultTitle = !string.IsNullOrWhiteSpace(s.Model.Topic) ? s.Model.Topic! : s.Comment.Split('\n')[0];
        var title = await Dialogs.Prompt("Task im Epic anlegen", $"Titel des Tasks in {epic}", defaultTitle.Length > 120 ? defaultTitle[..120] : defaultTitle);
        if (string.IsNullOrWhiteSpace(title)) return;
        var (account, source) = await _time.EpicAccountAsync(epic);
        if (account == null && _time.AccountRequired)
        {
            account = await AskAccountAsync(s);
            if (account == null) { _toast.Show("Abgebrochen", "Ohne Account wird kein Task angelegt.", NoticeLevel.Warning); return; }
            await _time.RememberAccountAsync(epic, account, RememberForProject);
            source = "gewählt";
        }
        if (!await Dialogs.Confirm("Task anlegen?", $"Task \"{title}\" im Epic {epic}{(account != null ? $"\nAccount: {account} ({source})" : "\nohne Account")}\nGeschätzt: {s.Model.Hours:0.##} h", "Anlegen")) return;
        try
        {
            var issue = await _time.CreateTaskInEpicAsync(epic, title, s.Comment, s.Model.Hours, account, default, guessProject);
            await _time.CorrectTicketAsync(s.Model, issue.Key);
            s.Model.IssueSummary = issue.Summary;
            s.Model.Account = issue.Account ?? account;
            s.Model.AccountFromIssue = issue.Account != null;
            s.RefreshTicket();
            OnPropertyChanged(nameof(HasUnassigned)); OnPropertyChanged(nameof(CanBook));
            await _data.Days.SaveAsync();
            LoadMemory();
            var info = $"Task in {epic}{(issue.Account != null ? $" · Account {issue.Account}" : "")}";
            var notes = _hub.Jira?.LastCreateNotes ?? [];
            if (notes.Count > 0) _toast.Show($"{issue.Key} angelegt", info + " · " + string.Join(" · ", notes), NoticeLevel.Warning);
            else _toast.Success($"{issue.Key} angelegt", info);
        }
        catch (Exception e) { _toast.Error(e, "Task anlegen"); }
    }

    [RelayCommand]
    private async Task ChangeTicketAsync(SuggestionVM s)
    {
        var key = await Dialogs.Prompt("Ticket ändern", s.HasTopic ? $"Copit merkt sich: {s.TopicText} → neues Ticket" : "Ticket-Key", s.IssueKey);
        if (string.IsNullOrWhiteSpace(key)) return;
        s.TicketInput = key;
        await AssignTicketAsync(s);
    }

    [RelayCommand]
    private async Task AnswerQuestionAsync(object? arg)
    {
        if (arg is not object[] { Length: 2 } pair || pair[0] is not TimeQuestionVM q || pair[1] is not string answer) return;
        await AnswerAsync(q, answer);
    }

    [RelayCommand] private Task AnswerTypedAsync(TimeQuestionVM q) => AnswerAsync(q, q.Answer);

    private async Task AnswerAsync(TimeQuestionVM q, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return;
        await _time.AnswerAsync(q.Model, answer.Trim());
        q.Answer = answer.Trim();
        q.Refresh();
        OnPropertyChanged(nameof(HasQuestions));
        await _data.Days.SaveAsync();
        LoadMemory();
        _toast.Success("Antwort gemerkt", "Gilt ab jetzt auch für künftige Tage.");
    }

    [RelayCommand]
    private async Task ReanalyzeAsync()
    {
        foreach (var q in Questions.Where(q => !q.Answered && !string.IsNullOrWhiteSpace(q.Answer)).ToList()) await AnswerAsync(q, q.Answer);
        await RunAnalysisAsync(useAi: true);
    }

    private void LoadMemory()
    {
        var q = (MemorySearch ?? "").Trim();
        bool Hit(params string?[] s) => q.Length == 0 || s.Any(x => x?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

        Topics.Clear();
        foreach (var t in _data.TimeMemory.Value.Topics.OrderByDescending(t => t.Manual).ThenByDescending(t => t.Hits).Where(t => Hit(t.Topic, t.Repo, t.Ticket))) Topics.Add(t);
        Facts.Clear();
        foreach (var f in _data.TimeMemory.Value.Facts.AsEnumerable().Reverse().Where(f => Hit(f.Question, f.Answer))) Facts.Add(f);

        var acc = _data.Accounts.Value;
        AccountRows.Clear();
        foreach (var (k, v) in acc.ProjectAccounts.OrderBy(x => x.Key).Where(x => Hit(x.Key, x.Value)))
            AccountRows.Add(new MemoryRow("project", k, v, $"Jira-Projekt {k}", "gilt für alle Tickets des Projekts ohne eigenen Account"));
        foreach (var (k, v) in acc.IssueAccounts.OrderBy(x => x.Key).Where(x => Hit(x.Key, x.Value)))
            AccountRows.Add(new MemoryRow("issue", k, v, k, "Ticket"));

        AssignmentRows.Clear();
        var projects = _data.Projects.Value;
        foreach (var (k, pid) in projects.TicketAssignments.OrderBy(x => x.Key))
        {
            var p = projects.Projects.FirstOrDefault(x => x.Id == pid);
            if (Hit(k, p?.Name, p?.EpicKey)) AssignmentRows.Add(new MemoryRow("assign", k, pid, k, p == null ? "Projekt gelöscht" : $"{p.Name} · Epic {p.EpicKey}"));
        }
        OnPropertyChanged(nameof(HasAccountRows)); OnPropertyChanged(nameof(HasAssignmentRows));
    }

    public bool HasAccountRows => AccountRows.Count > 0;
    public bool HasAssignmentRows => AssignmentRows.Count > 0;

    /// <summary>Gemerkten Account ändern (Ticket oder Jira-Projekt).</summary>
    [RelayCommand]
    private async Task EditMemoryAsync(MemoryRow r)
    {
        if (r.Kind == "assign") { await ReassignTicketAsync(r); return; }
        var v = await Dialogs.Prompt(r.Kind == "project" ? $"Account für Jira-Projekt {r.Key}" : $"Account für {r.Key}", "Account (Key, z. B. ACC-12) – leer lassen zum Entfernen", r.Value);
        if (v == null) return;
        var map = r.Kind == "project" ? _data.Accounts.Value.ProjectAccounts : _data.Accounts.Value.IssueAccounts;
        if (string.IsNullOrWhiteSpace(v)) map.Remove(r.Key);
        else
        {
            map[r.Key] = v.Trim();
            if (!_data.Accounts.Value.KnownAccounts.Contains(v.Trim())) _data.Accounts.Value.KnownAccounts.Insert(0, v.Trim());
        }
        await _data.Accounts.SaveAsync();
        LoadMemory();
    }

    [RelayCommand]
    private async Task DeleteMemoryAsync(MemoryRow r)
    {
        switch (r.Kind)
        {
            case "project": _data.Accounts.Value.ProjectAccounts.Remove(r.Key); await _data.Accounts.SaveAsync(); break;
            case "issue": _data.Accounts.Value.IssueAccounts.Remove(r.Key); await _data.Accounts.SaveAsync(); break;
            case "assign": _data.Projects.Value.TicketAssignments.Remove(r.Key); await _data.Projects.SaveAsync(); break;
        }
        LoadMemory();
    }

    /// <summary>Account fürs Ticket oder fürs ganze Jira-Projekt neu hinterlegen.</summary>
    [RelayCommand]
    private async Task AddAccountMemoryAsync()
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
            if (!_data.Accounts.Value.KnownAccounts.Contains(acc.Trim())) _data.Accounts.Value.KnownAccounts.Insert(0, acc.Trim());
            await _data.Accounts.SaveAsync();
        }
        LoadMemory();
    }

    /// <summary>Ticket einem anderen Copit-Projekt zuordnen (geteiltes Epic).</summary>
    private async Task ReassignTicketAsync(MemoryRow r)
    {
        var projects = _data.Projects.Value.Projects.Where(p => !string.IsNullOrWhiteSpace(p.EpicKey)).ToList();
        var current = projects.FirstOrDefault(p => p.Id == r.Value);
        var siblings = current != null ? TicketScope.Siblings(projects, current) : projects;
        var options = siblings.Select(p => p.Name).Append("Zuordnung entfernen").ToArray();
        var pick = await Dialogs.Choose($"{r.Key} zuordnen", options);
        if (pick == null || pick == "Abbrechen") return;
        if (pick == "Zuordnung entfernen") _data.Projects.Value.TicketAssignments.Remove(r.Key);
        else _data.Projects.Value.TicketAssignments[r.Key] = siblings.First(p => p.Name == pick).Id;
        await _data.Projects.SaveAsync();
        LoadMemory();
    }

    /// <summary>Gelernte Zuordnung Thema/Repo → Ticket ändern.</summary>
    [RelayCommand]
    private async Task EditTopicAsync(TopicMapping t)
    {
        var topic = await Dialogs.Prompt("Zuordnung bearbeiten", "Thema", t.Topic);
        if (topic == null) return;
        var ticket = await Dialogs.Prompt("Zuordnung bearbeiten", $"Ticket für \"{topic}\"", t.Ticket);
        if (string.IsNullOrWhiteSpace(ticket)) return;
        t.Topic = topic.Trim(); t.Ticket = ticket.Trim().ToUpperInvariant(); t.Manual = true; t.LastUsed = DateTime.Now;
        await _data.TimeMemory.SaveAsync();
        LoadMemory();
    }

    /// <summary>Gelerntes Wissen (Antwort auf eine Rückfrage) ändern.</summary>
    [RelayCommand]
    private async Task EditFactAsync(TimeFact f)
    {
        var a = await Dialogs.Prompt("Wissen bearbeiten", f.Question, f.Answer);
        if (a == null) return;
        if (string.IsNullOrWhiteSpace(a)) _data.TimeMemory.Value.Facts.Remove(f); else f.Answer = a.Trim();
        await _data.TimeMemory.SaveAsync();
        LoadMemory();
    }

    [RelayCommand]
    private async Task AddTopicAsync()
    {
        var topic = await Dialogs.Prompt("Zuordnung anlegen", "Thema oder Repo (z. B. \"Weiterentwicklung Shop\" oder \"ai-backend\")");
        if (string.IsNullOrWhiteSpace(topic)) return;
        var ticket = await Dialogs.Prompt("Zuordnung anlegen", $"Auf welches Ticket gehört \"{topic}\"?");
        if (string.IsNullOrWhiteSpace(ticket)) return;
        TimeBookingService.LearnTopic(_data.TimeMemory.Value.Topics, topic, null, ticket.Trim().ToUpperInvariant(), 3);
        await _data.TimeMemory.SaveAsync();
        LoadMemory();
    }

    [RelayCommand]
    private async Task DeleteTopicAsync(TopicMapping t)
    {
        _data.TimeMemory.Value.Topics.Remove(t);
        await _data.TimeMemory.SaveAsync();
        LoadMemory();
    }

    [RelayCommand]
    private async Task DeleteFactAsync(TimeFact f)
    {
        _data.TimeMemory.Value.Facts.Remove(f);
        await _data.TimeMemory.SaveAsync();
        LoadMemory();
    }

    // --------------- Regeln ---------------
    [RelayCommand] private void ToggleRules() { ShowRules = !ShowRules; if (ShowRules) LoadMemory(); }

    [RelayCommand]
    private void AddRule()
    {
        var r = new RuleSet { Name = "Neue Regel", Text = "" };
        _settings.Settings.Time.RuleSets.Add(r);
        Rules.Add(r);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(RuleSet r)
    {
        _settings.Settings.Time.RuleSets.Remove(r);
        Rules.Remove(r);
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        if (ParseHours(MinHours) is { } h && h >= 0) _settings.Settings.Time.MinHoursPerDay = h;
        await _settings.SaveAsync();
        _toast.Success("Regeln gespeichert");
        await LoadMonthAsync(Selected?.Date);
    }

    [RelayCommand] private Task OpenIssueAsync(string key) => Dialogs.OpenUrl(_hub.Jira?.IssueUrl(key));
}
