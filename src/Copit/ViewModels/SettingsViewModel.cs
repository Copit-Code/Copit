using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Core.Storage;
using Copit.Services;

namespace Copit.ViewModels;

public partial class WorkDayToggle : ObservableObject
{
    public DayOfWeek Day { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] public partial bool On { get; set; }
}

public partial class SettingsViewModel : ObservableObject, IActivatable
{
    private readonly SettingsService _s;
    private readonly ConnectionHub _hub;
    private readonly AgentRunnerFactory _runners;
    private readonly ToastService _toast;
    private readonly ShellViewModel _shell;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public List<WorkDayToggle> WorkDays { get; } = [];

    // Jira
    [ObservableProperty] public partial string JiraUrl { get; set; } = "";
    [ObservableProperty] public partial string JiraToken { get; set; } = "";
    [ObservableProperty] public partial bool JiraBasic { get; set; }
    [ObservableProperty] public partial string JiraUser { get; set; } = "";
    [ObservableProperty] public partial string AccountField { get; set; } = "";
    [ObservableProperty] public partial string EpicLinkField { get; set; } = "";
    [ObservableProperty] public partial bool UseTempo { get; set; }
    [ObservableProperty] public partial string TempoAttribute { get; set; } = "";
    [ObservableProperty] public partial string IssueType { get; set; } = "";
    [ObservableProperty] public partial bool JiraIgnoreCert { get; set; }
    [ObservableProperty] public partial string JiraTest { get; set; } = "";
    // GitLab
    [ObservableProperty] public partial string GitLabUrl { get; set; } = "";
    [ObservableProperty] public partial string GitLabToken { get; set; } = "";
    [ObservableProperty] public partial bool GitLabIgnoreCert { get; set; }
    [ObservableProperty] public partial string GitLabTest { get; set; } = "";
    // KI
    [ObservableProperty] public partial bool DefaultClaude { get; set; } = true;
    [ObservableProperty] public partial string ClaudePath { get; set; } = "";
    [ObservableProperty] public partial string ClaudeModel { get; set; } = "";
    [ObservableProperty] public partial string ClaudeArgs { get; set; } = "";
    [ObservableProperty] public partial string ClaudeTest { get; set; } = "";
    [ObservableProperty] public partial string CopilotPath { get; set; } = "";
    [ObservableProperty] public partial string CopilotModel { get; set; } = "";
    [ObservableProperty] public partial string CopilotArgs { get; set; } = "";
    [ObservableProperty] public partial string CopilotRequestPrice { get; set; } = "0.04";
    [ObservableProperty] public partial string CopilotCreditPrice { get; set; } = "0";
    [ObservableProperty] public partial string CopilotTest { get; set; } = "";
    [ObservableProperty] public partial string Instructions { get; set; } = "";
    // Token sparen: Skript-Bibliothek
    [ObservableProperty] public partial bool ScriptsEnabled { get; set; }
    [ObservableProperty] public partial bool ScriptsPerProject { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<ScriptInfo> Scripts { get; } = [];
    [ObservableProperty] public partial string ScriptsInfo { get; set; } = "";
    // Zeit
    [ObservableProperty] public partial string MinHours { get; set; } = "";
    [ObservableProperty] public partial string DayStart { get; set; } = "";
    [ObservableProperty] public partial string Rounding { get; set; } = "";
    [ObservableProperty] public partial bool Busy { get; set; }

    public string DataFolder => AppPaths.Root;

    public SettingsViewModel(SettingsService s, ConnectionHub hub, AgentRunnerFactory runners, ToastService toast, ShellViewModel shell)
    {
        _s = s; _hub = hub; _runners = runners; _toast = toast; _shell = shell;
        foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            WorkDays.Add(new WorkDayToggle { Day = d, Label = De.DateTimeFormat.GetAbbreviatedDayName(d) });
    }

    public void OnActivated(object? parameter)
    {
        var st = _s.Settings;
        JiraUrl = st.Jira.BaseUrl; JiraToken = _s.GetSecret(SecretKeys.JiraToken); JiraBasic = st.Jira.AuthMode == JiraAuthMode.Basic;
        JiraUser = st.Jira.Username; AccountField = st.Jira.AccountFieldId; EpicLinkField = st.Jira.EpicLinkFieldId;
        UseTempo = st.Jira.UseTempo; TempoAttribute = st.Jira.TempoAccountAttributeKey; IssueType = st.Jira.DefaultIssueType; JiraIgnoreCert = st.Jira.IgnoreCertificateErrors;
        GitLabUrl = st.GitLab.BaseUrl; GitLabToken = _s.GetSecret(SecretKeys.GitLabToken); GitLabIgnoreCert = st.GitLab.IgnoreCertificateErrors;
        DefaultClaude = st.Ai.DefaultProvider == AiProvider.Claude;
        ClaudePath = st.Ai.ClaudePath; ClaudeModel = st.Ai.ClaudeModel; ClaudeArgs = st.Ai.ClaudeExtraArgs;
        CopilotPath = st.Ai.CopilotPath; CopilotModel = st.Ai.CopilotModel; CopilotArgs = st.Ai.CopilotExtraArgs; CopilotRequestPrice = st.Ai.CopilotPremiumRequestUsd.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        CopilotCreditPrice = st.Ai.CopilotAiCreditUsd.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        Instructions = st.Ai.GlobalInstructions;
        ScriptsEnabled = st.Ai.Scripts.Enabled; ScriptsPerProject = st.Ai.Scripts.Scope == "project";
        LoadScripts();
        MinHours = st.Time.MinHoursPerDay.ToString("0.##", De); DayStart = st.Time.DayStart; Rounding = st.Time.RoundingMinutes.ToString();
        foreach (var w in WorkDays) w.On = st.Time.WorkDays.Contains(w.Day);
        if (string.IsNullOrEmpty(ClaudeTest)) _ = DetectAsync();
    }

    private async Task DetectAsync()
    {
        var c = _runners.Claude.Resolve();
        var p = _runners.Copilot.Resolve();
        ClaudeTest = c != null ? $"Gefunden: {c.DisplayPath}" : "Nicht gefunden";
        CopilotTest = p != null ? $"Gefunden: {p.DisplayPath}" : "Nicht gefunden – npm i -g @github/copilot";
        await Task.CompletedTask;
    }

    private void Apply()
    {
        var st = _s.Settings;
        st.Jira.BaseUrl = JiraUrl.Trim().TrimEnd('/'); st.Jira.AuthMode = JiraBasic ? JiraAuthMode.Basic : JiraAuthMode.BearerToken;
        st.Jira.Username = JiraUser.Trim(); st.Jira.AccountFieldId = AccountField.Trim(); st.Jira.EpicLinkFieldId = EpicLinkField.Trim();
        st.Jira.UseTempo = UseTempo; st.Jira.TempoAccountAttributeKey = string.IsNullOrWhiteSpace(TempoAttribute) ? "_Account_" : TempoAttribute.Trim();
        st.Jira.DefaultIssueType = string.IsNullOrWhiteSpace(IssueType) ? "Task" : IssueType.Trim(); st.Jira.IgnoreCertificateErrors = JiraIgnoreCert;
        st.GitLab.BaseUrl = GitLabUrl.Trim().TrimEnd('/'); st.GitLab.IgnoreCertificateErrors = GitLabIgnoreCert;
        st.Ai.DefaultProvider = DefaultClaude ? AiProvider.Claude : AiProvider.Copilot;
        st.Ai.ClaudePath = ClaudePath.Trim(); st.Ai.ClaudeModel = ClaudeModel.Trim(); st.Ai.ClaudeExtraArgs = ClaudeArgs.Trim();
        st.Ai.CopilotPath = CopilotPath.Trim(); st.Ai.CopilotModel = CopilotModel.Trim(); st.Ai.CopilotExtraArgs = CopilotArgs.Trim();
        if (double.TryParse(CopilotRequestPrice.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reqPrice) && reqPrice >= 0)
            st.Ai.CopilotPremiumRequestUsd = reqPrice;
        if (double.TryParse(CopilotCreditPrice.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var creditPrice) && creditPrice >= 0)
            st.Ai.CopilotAiCreditUsd = creditPrice;
        st.Ai.GlobalInstructions = Instructions;
        st.Ai.Scripts.Enabled = ScriptsEnabled; st.Ai.Scripts.Scope = ScriptsPerProject ? "project" : "global";
        if (double.TryParse(MinHours.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) st.Time.MinHoursPerDay = h;
        if (TimeOnly.TryParse(DayStart, out _)) st.Time.DayStart = DayStart.Trim();
        if (int.TryParse(Rounding, out var r) && r > 0) st.Time.RoundingMinutes = r;
        st.Time.WorkDays = WorkDays.Where(w => w.On).Select(w => w.Day).ToList();
        _s.SetSecret(SecretKeys.JiraToken, JiraToken.Trim());
        _s.SetSecret(SecretKeys.GitLabToken, GitLabToken.Trim());
    }

    /// <summary>Globale Bibliothek anzeigen (bei „pro Projekt“ liegt sie in .copit/scripts des jeweiligen Repos).</summary>
    private void LoadScripts()
    {
        Scripts.Clear();
        var dir = AppPaths.Dir("scripts");
        foreach (var x in ScriptLibrary.List(dir).OrderByDescending(x => x.Uses)) Scripts.Add(x);
        ScriptsInfo = ScriptsPerProject
            ? "Pro Projekt: Skripte liegen in .copit/scripts des jeweiligen Repos (können mit eingecheckt werden). Unten die globale Bibliothek."
            : $"Global für alle Projekte: {dir} · {Scripts.Count} Skripte, {Scripts.Sum(x => x.Uses)} Aufrufe";
    }

    partial void OnScriptsPerProjectChanged(bool value) => LoadScripts();

    [RelayCommand]
    private void OpenScripts()
    {
        var dir = AppPaths.Dir("scripts");
        ScriptLibrary.Seed(dir);
        LoadScripts();
        Dialogs.OpenFolder(dir);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Apply();
        _s.Settings.OnboardingDone = true;
        await _s.SaveAsync();
        _toast.Success("Einstellungen gespeichert");
    }

    [RelayCommand]
    private async Task TestJiraAsync()
    {
        Apply(); await _s.SaveAsync();
        JiraTest = "Teste…";
        try
        {
            var me = await _hub.RequireJira.GetMyselfAsync();
            JiraTest = $"✓ Verbunden als {me.DisplayName} ({me.Name})";
        }
        catch (Exception e) { JiraTest = "✗ " + e.Message; }
    }

    [RelayCommand]
    private async Task TestGitLabAsync()
    {
        Apply(); await _s.SaveAsync();
        GitLabTest = "Teste…";
        try
        {
            var me = await _hub.RequireGitLab.GetMeAsync();
            GitLabTest = $"✓ Verbunden als {me.Name} (@{me.Username})";
        }
        catch (Exception e) { GitLabTest = "✗ " + e.Message; }
    }

    [RelayCommand]
    private async Task TestClaudeAsync()
    {
        Apply(); await _s.SaveAsync();
        ClaudeTest = "Teste…";
        var (ok, info) = await _runners.Claude.CheckAsync();
        ClaudeTest = (ok ? "✓ " : "✗ ") + info;
    }

    [RelayCommand]
    private async Task TestCopilotAsync()
    {
        Apply(); await _s.SaveAsync();
        CopilotTest = "Teste…";
        var (ok, info) = await _runners.Copilot.CheckAsync();
        CopilotTest = (ok ? "✓ " : "✗ ") + info;
    }

    [RelayCommand]
    private async Task PickClaudeAsync()
    {
        var r = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "claude.exe / claude.cmd wählen" });
        if (r != null) ClaudePath = r.FullPath;
    }

    [RelayCommand]
    private async Task PickCopilotAsync()
    {
        var r = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "copilot.cmd wählen" });
        if (r != null) CopilotPath = r.FullPath;
    }

    [RelayCommand] private void SetDefault(string p) => DefaultClaude = p == "Claude";
    [RelayCommand] private void OpenData() => Dialogs.OpenFolder(AppPaths.Root);
}
