using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Ai;

public abstract class CliRunnerBase : IAgentRunner
{
    protected readonly SettingsService Settings;
    protected CliRunnerBase(SettingsService settings) => Settings = settings;

    public abstract AiProvider Provider { get; }
    protected abstract string ExeName { get; }
    protected abstract string ConfiguredPath { get; }

    public CliCommand? Resolve() => CliLocator.Resolve(ExeName, ConfiguredPath);

    public async Task<(bool Ok, string Info)> CheckAsync(CancellationToken ct = default)
    {
        var cmd = Resolve();
        if (cmd == null) return (false, $"{ExeName} wurde nicht gefunden. Pfad in den Einstellungen hinterlegen.");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await ProcessRunner.RunAsync(cmd.FileName, [.. cmd.PrefixArgs, "--version"], ct: cts.Token);
            var v = (r.StdOut + r.StdErr).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            return (r.Ok, r.Ok ? $"{v}  ·  {cmd.DisplayPath}" : v);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    public abstract IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct);

    /// <summary>Startet einen Prozess und überführt Zeilen-Callbacks in einen async Stream.</summary>
    protected async IAsyncEnumerable<AgentEvent> Pump(CliCommand cmd, List<string> args, string? cwd, string? stdin,
        Func<string, IEnumerable<AgentEvent>> mapStdout, Func<string, AgentEvent?> mapStderr,
        Func<ProcessResult, StringBuilder, AgentEvent?> onExit, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var collected = new StringBuilder();
        var run = Task.Run(async () =>
        {
            try
            {
                var result = await ProcessRunner.RunAsync(cmd.FileName, [.. cmd.PrefixArgs, .. args], cwd, stdin,
                    line => { foreach (var e in mapStdout(line)) channel.Writer.TryWrite(e); },
                    line => { lock (collected) collected.AppendLine(line); var e = mapStderr(line); if (e != null) channel.Writer.TryWrite(e); },
                    ct: ct);
                var final = onExit(result, collected);
                if (final != null) channel.Writer.TryWrite(final);
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryWrite(new AgentResult(true, "Abgebrochen.", 0, 0, 0, [], null, "cancelled"));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new AgentResult(true, ex.Message, 0, 0, 0, [], null, "error"));
            }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return e;
        await run;
    }
}

/// <summary>Claude Code CLI im Print-Modus mit stream-json.</summary>
public class ClaudeCliRunner(SettingsService settings) : CliRunnerBase(settings)
{
    public override AiProvider Provider => AiProvider.Claude;
    protected override string ExeName => "claude";
    protected override string ConfiguredPath => Settings.Settings.Ai.ClaudePath;

    public static string PermissionMode(AgentMode mode) => mode switch
    {
        AgentMode.Plan => "plan",
        AgentMode.Edit => "acceptEdits",
        AgentMode.Auto => "bypassPermissions",
        _ => "default"
    };

    public static List<string> BuildArgs(AgentRequest r, string? extraArgs)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-mode", PermissionMode(r.Mode) };
        if (!string.IsNullOrWhiteSpace(r.ResumeSessionId)) { args.Add("--resume"); args.Add(r.ResumeSessionId); }
        if (!string.IsNullOrWhiteSpace(r.Model)) { args.Add("--model"); args.Add(r.Model); }
        if (r.AllowedTools.Count > 0) { args.Add("--allowedTools"); args.Add(string.Join(",", r.AllowedTools)); }
        if (r.DisallowedTools.Count > 0) { args.Add("--disallowedTools"); args.Add(string.Join(",", r.DisallowedTools)); }
        if (!string.IsNullOrWhiteSpace(r.AppendSystemPrompt)) { args.Add("--append-system-prompt"); args.Add(r.AppendSystemPrompt); }
        foreach (var d in r.AdditionalDirs) { args.Add("--add-dir"); args.Add(d); }
        if (!string.IsNullOrWhiteSpace(r.McpConfigPath)) { args.Add("--mcp-config"); args.Add(r.McpConfigPath); }
        if (r.StrictMcpConfig) args.Add("--strict-mcp-config");
        args.AddRange(ProcessRunner.SplitArgs(extraArgs));
        return args;
    }

    public override async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var cmd = Resolve();
        if (cmd == null)
        {
            yield return new AgentResult(true, "Claude CLI nicht gefunden. Installiere sie (`npm i -g @anthropic-ai/claude-code` oder nativer Installer) oder hinterlege den Pfad in den Einstellungen.", 0, 0, 0, [], null, "error");
            yield break;
        }
        var args = BuildArgs(request, Settings.Settings.Ai.ClaudeExtraArgs);
        bool gotResult = false;
        await foreach (var e in Pump(cmd, args, request.WorkingDirectory, request.Prompt,
                           ClaudeStreamParser.Parse,
                           line => line.Length > 0 ? new AgentStatusLine(line) : null,
                           (res, err) => null, ct))
        {
            if (e is AgentResult) gotResult = true;
            yield return e;
        }
        if (!gotResult)
        {
            yield return new AgentResult(true, "Claude CLI wurde ohne Ergebnis beendet. Ist die CLI angemeldet (`claude` einmal im Terminal starten)?",
                0, 0, 0, [], null, "error");
        }
    }
}

/// <summary>GitHub Copilot CLI im Prompt-Modus (Plain-Text-Ausgabe).</summary>
public class CopilotCliRunner(SettingsService settings) : CliRunnerBase(settings)
{
    private const int MaxPromptChars = 24_000;
    public override AiProvider Provider => AiProvider.Copilot;
    protected override string ExeName => "copilot";
    protected override string ConfiguredPath => Settings.Settings.Ai.CopilotPath;

    public static string ComposePrompt(AgentRequest r)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.AppendSystemPrompt))
            sb.AppendLine("[Anweisungen]").AppendLine(r.AppendSystemPrompt).AppendLine();
        if (r.Mode is AgentMode.Ask or AgentMode.Plan)
            sb.AppendLine(r.Mode == AgentMode.Plan
                ? "[Modus: PLAN] Ändere keine Dateien. Erstelle nur einen nummerierten Umsetzungsplan."
                : "[Modus: FRAGEN] Ändere keine Dateien und führe keine verändernden Befehle aus.").AppendLine();
        var history = r.History.Where(m => m.Role is ChatRole.User or ChatRole.Assistant).TakeLast(10).ToList();
        if (history.Count > 0)
        {
            sb.AppendLine("[Bisheriger Gesprächsverlauf]");
            foreach (var m in history)
            {
                var t = m.Text.Length > 1500 ? m.Text[..1500] + " …" : m.Text;
                sb.AppendLine($"{(m.Role == ChatRole.User ? "Nutzer" : "Assistent")}: {t}");
            }
            sb.AppendLine().AppendLine("[Neue Nachricht]");
        }
        sb.Append(r.Prompt);
        var s = sb.ToString();
        return s.Length > MaxPromptChars ? s[^MaxPromptChars..] : s;
    }

    public override async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var cmd = Resolve();
        if (cmd == null)
        {
            yield return new AgentResult(true, "Copilot CLI nicht gefunden. Installiere sie mit `npm i -g @github/copilot` oder hinterlege den Pfad in den Einstellungen.", 0, 0, 0, [], null, "error");
            yield break;
        }
        var args = new List<string> { "-p", ComposePrompt(request) };
        if (request.Mode is AgentMode.Auto) args.Add("--allow-all-tools");
        else { var allow = request.AllowedTools.Select(Services.PermissionService.ToCopilotTool).Where(t => t != null).Cast<string>().ToHashSet(); if (request.Mode == AgentMode.Edit) allow.Add("write"); foreach (var t in allow) { args.Add("--allow-tool"); args.Add(t); } }
        foreach (var t in request.DisallowedTools.Select(Services.PermissionService.ToCopilotTool).Where(t => t != null).Distinct()) { args.Add("--deny-tool"); args.Add(t!); }
        var model = request.Model;
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
        foreach (var d in request.AdditionalDirs) { args.Add("--add-dir"); args.Add(d); }
        args.AddRange(ProcessRunner.SplitArgs(Settings.Settings.Ai.CopilotExtraArgs));

        yield return new AgentInit("", string.IsNullOrWhiteSpace(model) ? "copilot (Standard)" : model, [], request.Mode.ToString());
        var full = new StringBuilder();
        var usage = new CopilotUsageParser();
        var started = DateTime.Now;
        await foreach (var e in Pump(cmd, args, request.WorkingDirectory, null,
                           line =>
                           {
                               var clean = GitLabClient.StripAnsi(line);
                               lock (usage) if (usage.Feed(clean)) return [];
                               lock (full) full.AppendLine(clean);
                               return [new AgentTextDelta(clean + "\n")];
                           },
                           line =>
                           {
                               var clean = GitLabClient.StripAnsi(line);
                               lock (usage) if (usage.Feed(clean)) return null;
                               return clean.Length > 0 ? new AgentStatusLine(clean) : null;
                           },
                           (res, err) =>
                           {
                               string text;
                               lock (full) text = full.ToString().Trim();
                               var isError = !res.Ok;
                               if (isError && text.Length == 0) text = err.ToString().Trim();
                               TokenUsage? tokens; double premium;
                               lock (usage) { tokens = usage.Usage; premium = usage.PremiumRequests; }
                               // Kosten = Premium Requests × Preis (Copilot meldet keine Dollar-Beträge)
                               var cost = premium * Math.Max(0, Settings.Settings.Ai.CopilotPremiumRequestUsd);
                               return new AgentResult(isError, text, cost, (long)(DateTime.Now - started).TotalMilliseconds, 1, [], null,
                                   isError ? "error" : "success", tokens, premium);
                           }, ct))
            yield return e;
    }
}

public class AgentRunnerFactory(ClaudeCliRunner claude, CopilotCliRunner copilot)
{
    public IAgentRunner Get(AiProvider p) => p == AiProvider.Copilot ? copilot : claude;
    public ClaudeCliRunner Claude => claude;
    public CopilotCliRunner Copilot => copilot;
}

/// <summary>Einmal-Aufrufe (Analyse, Schätzung, Review) ohne Session.</summary>
public class AiService(AgentRunnerFactory factory, SettingsService settings)
{
    public async Task<string> AskAsync(string prompt, AiProvider? provider = null, string? workingDir = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var p = provider ?? settings.Settings.Ai.DefaultProvider;
        var runner = factory.Get(p);
        var model = p == AiProvider.Claude ? settings.Settings.Ai.ClaudeModel : settings.Settings.Ai.CopilotModel;
        var req = new AgentRequest
        {
            Prompt = prompt,
            WorkingDirectory = workingDir ?? Path.GetTempPath(),
            Mode = AgentMode.Ask,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
        };
        var text = new StringBuilder();
        AgentResult? result = null;
        await foreach (var e in runner.RunAsync(req, ct))
        {
            switch (e)
            {
                case AgentToolUse t: onProgress?.Invoke($"Tool: {t.Name}"); break;
                case AgentThinking: onProgress?.Invoke("denkt nach…"); break;
                case AgentTextDelta d: text.Append(d.Text); onProgress?.Invoke("schreibt Antwort…"); break;
                case AgentResult r: result = r; break;
            }
        }
        if (result == null) throw new InvalidOperationException("Keine Antwort von der KI.");
        if (result.Subtype == "cancelled") throw new OperationCanceledException();
        if (result.IsError) throw new InvalidOperationException(result.Text);
        return string.IsNullOrWhiteSpace(result.Text) ? text.ToString() : result.Text;
    }
}
