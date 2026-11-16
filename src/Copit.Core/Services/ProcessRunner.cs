using System.Diagnostics;
using System.Text;

namespace Copit.Core.Services;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDir = null,
        string? stdin = null,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        IDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { outDone.TrySetResult(); return; }
            lock (stdout) stdout.AppendLine(e.Data);
            try { onStdOutLine?.Invoke(e.Data); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { errDone.TrySetResult(); return; }
            lock (stderr) stderr.AppendLine(e.Data);
            try { onStdErrLine?.Invoke(e.Data); } catch { }
        };

        if (!p.Start()) throw new InvalidOperationException($"Prozess {fileName} konnte nicht gestartet werden.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        catch { /* Prozess liest kein stdin */ }

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        await p.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(2000));
        ct.ThrowIfCancellationRequested();
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Zerlegt eine Kommandozeile in Argumente (berücksichtigt Anführungszeichen).</summary>
    public static List<string> SplitArgs(string? commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
