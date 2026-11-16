using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

/// <summary>Ein Skript der Bibliothek mit den Angaben aus dem Kopfkommentar.</summary>
public record ScriptInfo(string Name, string File, string Purpose, string Usage, int Uses, DateTime? LastUsed);

/// <summary>
/// Token sparen: Agents legen für wiederkehrende, mechanische Aufgaben (Suchen, Filtern, Zählen, Auswerten) kleine Skripte an
/// und rufen sie statt vieler einzelner Tool-Aufrufe auf. Copit gibt die Bibliothek als kompakten Index in den Systemprompt,
/// erlaubt nur dort Ausführen/Schreiben und zählt die Nutzung.
/// </summary>
public static partial class ScriptLibrary
{
    private static readonly string[] Exts = [".ps1", ".py", ".sh", ".js", ".mjs"];
    private const string StatsFile = ".copit-stats.json";
    private static readonly object Lock = new();

    /// <summary>Ordner der Bibliothek: pro Projekt (.copit/scripts im Repo) oder global (%LOCALAPPDATA%\Copit\scripts).</summary>
    public static string Dir(ScriptLibrarySettings s, string? projectFolder) =>
        s.Scope == "project" && !string.IsNullOrWhiteSpace(projectFolder) && Directory.Exists(projectFolder)
            ? Path.Combine(projectFolder, ".copit", "scripts")
            : AppPaths.Dir("scripts");

    /// <summary>Pfad mit '/' – so wird er in Prompts und Berechtigungsmustern verwendet.</summary>
    public static string Slash(string path) => path.Replace('\\', '/').TrimEnd('/');

    [GeneratedRegex(@"^\s*(?:#|//|--|REM)\s*(Zweck|Purpose|Aufruf|Usage|Beispiel)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderRx();

    public static List<ScriptInfo> List(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var stats = ReadStats(dir);
        var list = new List<ScriptInfo>();
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
        {
            string head;
            try { head = string.Join('\n', File.ReadLines(f).Take(25)); } catch { continue; }
            string purpose = "", usage = "";
            foreach (Match m in HeaderRx().Matches(head))
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                if (key is "zweck" or "purpose" && purpose.Length == 0) purpose = m.Groups[2].Value.Trim();
                else if (key is "aufruf" or "usage" or "beispiel" && usage.Length == 0) usage = m.Groups[2].Value.Trim();
            }
            var name = Path.GetFileName(f);
            stats.TryGetValue(name, out var st);
            list.Add(new ScriptInfo(name, f, purpose, usage, st?.Uses ?? 0, st?.LastUsed));
        }
        return list;
    }

    /// <summary>So ruft der Agent ein Skript auf (genau dieses Präfix ist erlaubt).</summary>
    public static string Invocation(string dir, string file)
    {
        var p = $"{Slash(dir)}/{file}";
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".ps1" => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{p}\"",
            ".py" => $"python \"{p}\"",
            ".js" or ".mjs" => $"node \"{p}\"",
            _ => $"bash \"{p}\"",
        };
    }

    /// <summary>Berechtigungen: Skripte aus der Bibliothek ausführen (Präfix mit Ordnerpfad).</summary>
    public static List<string> AllowedTools(string dir)
    {
        var d = Slash(dir);
        return [
            $"Bash(powershell -NoProfile -ExecutionPolicy Bypass -File \"{d}/:*)",
            $"Bash(python \"{d}/:*)",
            $"Bash(node \"{d}/:*)",
            $"Bash(bash \"{d}/:*)",
        ];
    }

    /// <summary>Anweisungen + Index für den Systemprompt.</summary>
    public static string Instructions(string dir, IReadOnlyList<ScriptInfo> scripts, bool canWrite)
    {
        var win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var sb = new StringBuilder();
        sb.AppendLine($"TOKEN SPAREN – SKRIPT-BIBLIOTHEK: {Slash(dir)}");
        sb.AppendLine("Wiederkehrende, mechanische Aufgaben (in Dateien suchen, Logs/Ausgaben filtern, zählen, Daten extrahieren, Test-/Build-Ergebnisse zusammenfassen) erledigst du über Skripte statt über viele einzelne Tool-Aufrufe oder das Lesen ganzer Dateien. Skripte geben nur das kompakte Ergebnis aus.");
        if (scripts.Count > 0)
        {
            sb.AppendLine("Vorhandene Skripte (zuerst prüfen, ob eines passt):");
            foreach (var s in scripts.OrderByDescending(s => s.Uses).Take(40))
                sb.AppendLine($"- {s.Name}: {(s.Purpose.Length > 0 ? s.Purpose : "(ohne Beschreibung)")}{(s.Usage.Length > 0 ? " · Aufruf: " + s.Usage : "")}");
        }
        else sb.AppendLine("Die Bibliothek ist noch leer.");
        sb.AppendLine($"Aufruf immer genau so (sonst fehlt die Freigabe): {Invocation(dir, win ? "<name>.ps1" : "<name>.sh")} <Argumente>  (bzw. python \"{Slash(dir)}/<name>.py\")");
        if (canWrite)
        {
            sb.AppendLine("Fehlt ein passendes Skript und die Aufgabe kommt erkennbar wieder, lege eines in diesem Ordner an:");
            sb.AppendLine($"- {(win ? "PowerShell (.ps1, Windows PowerShell 5.1 kompatibel)" : "bash oder Python")}, parametrisiert, ohne fest eingebaute Projektpfade, nur lesend/auswertend, nichts Destruktives, keine Secrets.");
            sb.AppendLine("- Kopfkommentar in den ersten Zeilen: \"# Zweck: …\" und \"# Aufruf: …\" (daraus baut Copit den Index).");
            sb.AppendLine("- Ausgabe kurz halten (Treffer mit Datei:Zeile, Summen, höchstens ~60 Zeilen).");
            sb.AppendLine("- Ein vorhandenes Skript lieber erweitern als ein ähnliches neu anlegen. Einmalige Aufgaben brauchen kein Skript.");
        }
        return sb.ToString();
    }

    /// <summary>Zählt Aufrufe von Bibliotheks-Skripten in einem Bash-Befehl.</summary>
    public static bool RecordUse(string dir, string command)
    {
        var d = Slash(dir);
        var cmd = command.Replace('\\', '/');
        if (!cmd.Contains(d, StringComparison.OrdinalIgnoreCase)) return false;
        var names = List(dir).Select(s => s.Name).Where(n => cmd.Contains($"{d}/{n}", StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) return false;
        lock (Lock)
        {
            var stats = ReadStats(dir);
            foreach (var n in names)
            {
                stats.TryGetValue(n, out var st);
                stats[n] = new ScriptStat { Uses = (st?.Uses ?? 0) + 1, LastUsed = DateTime.Now };
            }
            try { File.WriteAllText(Path.Combine(dir, StatsFile), JsonSerializer.Serialize(stats, Json.Options)); } catch { }
        }
        return true;
    }

    public class ScriptStat { public int Uses { get; set; } public DateTime? LastUsed { get; set; } }

    private static Dictionary<string, ScriptStat> ReadStats(string dir)
    {
        try
        {
            var f = Path.Combine(dir, StatsFile);
            return File.Exists(f) ? JsonSerializer.Deserialize<Dictionary<string, ScriptStat>>(File.ReadAllText(f), Json.Options) ?? [] : [];
        }
        catch { return []; }
    }

    /// <summary>Legt Startskripte an, wenn die Bibliothek leer ist.</summary>
    public static void Seed(string dir)
    {
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFiles(dir).Any(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))) return;
        var utf8Bom = new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(dir, "suche.ps1"), """
            # Zweck: Text/Regex in Dateien suchen (ignoriert bin, obj, node_modules, .git), Treffer als Datei:Zeile mit Kontext
            # Aufruf: suche.ps1 -Muster "TODO|FIXME" [-Ordner .] [-Filter *.cs] [-Kontext 1] [-Max 60]
            param([Parameter(Mandatory)][string]$Muster, [string]$Ordner = '.', [string]$Filter = '*', [int]$Kontext = 0, [int]$Max = 60)
            $skip = '[\\/](bin|obj|node_modules|\.git|dist|out|packages)[\\/]'
            $files = Get-ChildItem -Path $Ordner -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch $skip -and $_.Length -lt 2MB }
            $n = 0
            foreach ($m in ($files | Select-String -Pattern $Muster -Context $Kontext, $Kontext -ErrorAction SilentlyContinue)) {
                if ($n -ge $Max) { Write-Output "… weitere Treffer (Max $Max erreicht)"; break }
                $rel = Resolve-Path -Relative $m.Path
                foreach ($c in $m.Context.PreContext) { Write-Output "  $c" }
                Write-Output ("{0}:{1}: {2}" -f $rel, $m.LineNumber, $m.Line.Trim())
                foreach ($c in $m.Context.PostContext) { Write-Output "  $c" }
                $n++
            }
            if ($n -eq 0) { Write-Output 'Keine Treffer.' }
            """, utf8Bom);
        File.WriteAllText(Path.Combine(dir, "dateien.ps1"), """
            # Zweck: Dateien nach Muster auflisten mit Zeilenzahl, sortiert nach Größe (Überblick ohne Dateien zu lesen)
            # Aufruf: dateien.ps1 [-Ordner .] [-Filter *.cs] [-Top 40]
            param([string]$Ordner = '.', [string]$Filter = '*', [int]$Top = 40)
            $skip = '[\\/](bin|obj|node_modules|\.git|dist|out|packages)[\\/]'
            $files = Get-ChildItem -Path $Ordner -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch $skip }
            $rows = foreach ($f in $files) { $lines = 0; try { $lines = [IO.File]::ReadAllLines($f.FullName).Length } catch { }; [pscustomobject]@{ Zeilen = $lines; Datei = (Resolve-Path -Relative $f.FullName) } }
            $rows | Sort-Object Zeilen -Descending | Select-Object -First $Top | ForEach-Object { '{0,6}  {1}' -f $_.Zeilen, $_.Datei }
            Write-Output ("{0} Dateien, {1} Zeilen gesamt" -f @($rows).Count, ($rows | Measure-Object Zeilen -Sum).Sum)
            """, utf8Bom);
    }
}
