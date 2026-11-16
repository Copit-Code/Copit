using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

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
    public string Reason => Model.Reason;
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
        set { Model.Account = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsAccount)); OnPropertyChanged(nameof(AccountText)); }
    }
    public bool AccountFromMemory => Model.AccountFromMemory;
    public bool NeedsAccount => _accountRequired && string.IsNullOrWhiteSpace(Model.Account);
    public bool ShowAccount => _accountRequired;
    public string AccountText => string.IsNullOrWhiteSpace(Model.Account) ? "Account fehlt" : Model.Account + (Model.AccountFromMemory ? " · gemerkt" : "");

    [ObservableProperty] public partial bool Booked { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
}

public partial class TimeViewModel : ObservableObject, IActivatable
{
    private readonly TimeBookingService _time;
    private readonly DataService _data;
    private readonly SettingsService _settings;
    private readonly ConnectionHub _hub;
    private readonly ToastService _toast;
    private readonly SessionStore _sessions;
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _cts;
    private DayContext? _ctx;
    private TaskCompletionSource<string?>? _accountTcs;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public ObservableCollection<DayCell> Days { get; } = [];
    public ObservableCollection<JiraWorklog> Worklogs { get; } = [];
    public ObservableCollection<StepVM> Steps { get; } = [];
    public ObservableCollection<SuggestionVM> Suggestions { get; } = [];
    public ObservableCollection<RuleSet> Rules { get; } = [];
    public ObservableCollection<string> AccountOptions { get; } = [];
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

    public string MonthTitle => Month.ToString("MMMM yyyy", De);
    public string SelectedTitle => Selected?.Date.ToString("dddd, d. MMMM", De) ?? "";
    public bool HasSelection => Selected != null;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsClaude => Provider == AiProvider.Claude;
    public bool IsCopilot => Provider == AiProvider.Copilot;
    public bool CanBook => HasSuggestions && !IsWorking && !IsBooking && Suggestions.Any(s => !s.Booked);
    public string BookedText => Selected == null ? "" : $"{Selected.Booked:0.##}h von {Selected.Target:0.##}h gebucht";

    public event Action? Celebrate;

    public TimeViewModel(TimeBookingService time, DataService data, SettingsService settings, ConnectionHub hub,
        ToastService toast, SessionStore sessions, ShellViewModel shell)
    {
        _time = time; _data = data; _settings = settings; _hub = hub; _toast = toast; _sessions = sessions; _shell = shell;
        Provider = settings.Settings.Ai.DefaultProvider;
        var today = DateOnly.FromDateTime(DateTime.Today);
        Month = new DateOnly(today.Year, today.Month, 1);
        Suggestions.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasSuggestions)); OnPropertyChanged(nameof(CanBook)); UpdateTotal(); };
    }

    partial void OnProviderChanged(AiProvider value) { OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsCopilot)); }
    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(CanBook));
    partial void OnIsBookingChanged(bool value) => OnPropertyChanged(nameof(CanBook));

    partial void OnSelectedChanged(DayCell? oldValue, DayCell? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(BookedText));
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
        MinHours = _settings.Settings.Time.MinHoursPerDay.ToString("0.##", De);
        Rules.Clear();
        foreach (var r in _settings.Settings.Time.RuleSets) Rules.Add(r);
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

        if (_hub.Jira == null) { MonthStats = "Jira nicht konfiguriert"; return; }
        LoadingMonth = true;
        try
        {
            var booked = await _time.GetBookedHoursAsync(start, start.AddDays(41));
            foreach (var c in Days) c.Booked = booked.GetValueOrDefault(c.Date);
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
        _ = LoadWorklogsAsync(cell.Date);
    }

    private async Task LoadWorklogsAsync(DateOnly d)
    {
        Worklogs.Clear();
        if (_hub.Jira == null) return;
        try
        {
            var logs = await _hub.Jira.GetMyWorklogsAsync(d, d);
            if (Selected?.Date != d) return;
            Worklogs.Clear();
            foreach (var l in logs) Worklogs.Add(l);
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
        TotalText = $"{total:0.##}h verteilt" + (want > 0 ? $" · Ziel {want:0.##}h" : "");
        TotalOk = want <= 0 || Math.Abs(total - want) < 0.01;
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
        Suggestions.Clear();
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
            _ctx = await _time.GatherAsync(day, 0, _sessions.DescribeDay(day, _data), Progress, _cts.Token);
            _ctx.TargetHours = _ctx.BookedHours + hours.Value;
            foreach (var w in _ctx.Warnings) Progress("Hinweis: " + w);

            BookingPlan plan;
            if (useAi)
            {
                try { plan = await _time.SuggestAsync(_ctx, Provider, Progress, _cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    _toast.Show("KI nicht verfügbar – nutze Heuristik", e.Message, NoticeLevel.Warning);
                    plan = _time.HeuristicPlan(_ctx);
                    await _time.ResolveAccountsAsync(plan.Bookings, _ctx.Issues, Progress, _cts.Token);
                }
            }
            else
            {
                plan = _time.HeuristicPlan(_ctx);
                await _time.ResolveAccountsAsync(plan.Bookings, _ctx.Issues, Progress, _cts.Token);
            }
            Progress("Fertig");
            if (Steps.Count > 0) Steps[^1].Done = true;

            Summary = plan.Summary;
            foreach (var b in plan.Bookings) AddSuggestionVm(b);
            if (plan.Bookings.Count == 0 && string.IsNullOrEmpty(Summary)) Summary = "Keine passenden Tickets gefunden. Trage eine Notiz zum Tag ein und versuche es erneut.";
            var entry = _data.Day(day);
            entry.LastSuggestions = plan.Bookings;
            entry.LastSummary = plan.Summary;
            await _data.Days.SaveAsync();
            Selected.HasDraft = plan.Bookings.Count > 0;
        }
        catch (OperationCanceledException) { Summary = "Abgebrochen."; }
        catch (Exception e) { Error = e.Message; }
        finally { IsWorking = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RemoveSuggestion(SuggestionVM s)
    {
        Suggestions.Remove(s);
        if (Selected != null) _data.Day(Selected.Date).LastSuggestions.Remove(s.Model);
        _ = _data.Days.SaveAsync();
    }

    [RelayCommand]
    private async Task AddSuggestionAsync()
    {
        if (Selected == null) return;
        var key = await Dialogs.Prompt("Buchung hinzufügen", "Ticket-Key (z.B. ABC-123)");
        if (string.IsNullOrWhiteSpace(key)) return;
        var s = new BookingSuggestion { IssueKey = key.Trim().ToUpperInvariant(), Hours = 1, Comment = "", Confidence = 1 };
        if (_hub.Jira != null)
        {
            var issue = await _hub.Jira.GetIssueAsync(s.IssueKey);
            if (issue == null) { _toast.Show("Ticket nicht gefunden", s.IssueKey, NoticeLevel.Warning); return; }
            s.IssueSummary = issue.Summary;
            await _time.ResolveAccountsAsync([s], [issue], _ => { });
        }
        _data.Day(Selected.Date).LastSuggestions.Add(s);
        AddSuggestionVm(s);
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
                foreach (var a in (await _hub.Jira.GetTempoAccountsAsync()).Take(40))
                    if (!AccountOptions.Contains(a.Key)) AccountOptions.Add(a.Key);
            }
            catch { }
        }
        AccountFor = s;
        AccountInput = s.Account ?? "";
        ShowAccountDialog = true;
        _accountTcs = new TaskCompletionSource<string?>();
        var result = await _accountTcs.Task;
        ShowAccountDialog = false;
        if (!string.IsNullOrWhiteSpace(result))
        {
            await _time.RememberAccountAsync(s.IssueKey, result.Trim(), RememberForProject);
            s.Model.AccountFromMemory = true;
        }
        return result?.Trim();
    }

    [RelayCommand] private void PickAccount(string a) => AccountInput = a;
    [RelayCommand] private void ConfirmAccount() => _accountTcs?.TrySetResult(string.IsNullOrWhiteSpace(AccountInput) ? null : AccountInput);
    [RelayCommand] private void SkipAccount() => _accountTcs?.TrySetResult(null);

    [RelayCommand]
    private async Task BookAsync()
    {
        if (Selected == null || _ctx == null && _hub.Jira == null) return;
        var open = Suggestions.Where(s => !s.Booked && s.Model.Hours > 0).ToList();
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
        if (!await Dialogs.Confirm("Jetzt buchen?", $"{open.Count} Buchung(en) mit insgesamt {total:0.##}h am {Selected.Date:dd.MM.yyyy} in Jira eintragen?", "Buchen")) return;

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

    // --------------- Regeln ---------------
    [RelayCommand] private void ToggleRules() => ShowRules = !ShowRules;

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
