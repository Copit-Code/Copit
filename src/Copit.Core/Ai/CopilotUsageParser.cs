using System.Globalization;
using System.Text.RegularExpressions;

namespace Copit.Core.Ai;

/// <summary>
/// Liest die Nutzungs-Zusammenfassung, die die Copilot CLI im Prompt-Modus (-p) am Ende ausgibt, z. B.
/// <code>
/// Total usage est:       1 Premium request
/// Usage by model:
///     gpt-5    12.6k input, 297 output, 0 cache read, 0 cache write (Est. 1 Premium request)
/// </code>
/// oder im neueren Format
/// <code>
/// Breakdown by AI model:
///  claude-sonnet-4.5   18.8k in, 240 out, 15.4k cached (Est. 0.33 Premium requests)
/// </code>
/// Zeilen der Zusammenfassung werden erkannt (<see cref="Feed"/> liefert dann true) und gehören nicht zur Antwort.
/// </summary>
public sealed partial class CopilotUsageParser
{
    private bool _inSummary;
    private long _in, _out, _cacheRead, _cacheWrite;
    private bool _hasTokens;

    public double PremiumRequests { get; private set; }
    public bool Found => _hasTokens || PremiumRequests > 0;
    public TokenUsage? Usage => _hasTokens ? new TokenUsage(_in, _out, _cacheRead, _cacheWrite) : null;

    /// <summary>Verarbeitet eine Ausgabezeile. true = Zeile gehört zur Zusammenfassung.</summary>
    public bool Feed(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return _inSummary;

        if (StartRx().IsMatch(line))
        {
            _inSummary = true;
            var m = PremiumRx().Match(line);
            if (line.StartsWith("Total usage", StringComparison.OrdinalIgnoreCase) && m.Success)
                PremiumRequests = Num(m.Groups[1].Value);
            return true;
        }
        if (!_inSummary) return false;

        if (SummaryKeyRx().IsMatch(line)) return true;   // Dauer, Code-Änderungen …
        if (ModelLine(line)) return true;
        // Unbekannte Zeile mitten in der Zusammenfassung: noch dazurechnen, solange sie wie „Schlüssel: Wert“ aussieht
        return line.Contains(':');
    }

    private bool ModelLine(string line)
    {
        long? input = Tok(line, @"(?:input|in)"), output = Tok(line, @"(?:output|out)");
        if (input == null && output == null) return false;
        var cacheRead = Tok(line, @"cache\s*read") ?? 0;
        var cacheWrite = Tok(line, @"cache\s*write") ?? 0;
        var cached = Tok(line, @"cached") ?? 0;
        var i = input ?? 0;
        if (cached > 0)
        {
            // Neueres Format: „in“ enthält die gecachten Tokens bereits
            cacheRead += cached;
            i = Math.Max(0, i - cached);
        }
        _in += i; _out += output ?? 0; _cacheRead += cacheRead; _cacheWrite += cacheWrite;
        _hasTokens = true;
        return true;
    }

    private static long? Tok(string line, string label)
    {
        var m = Regex.Match(line, @"([\d][\d.,]*)\s*([kKmM]?)\s+" + label + @"\b", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var v = Num(m.Groups[1].Value);
        v *= m.Groups[2].Value.ToLowerInvariant() switch { "k" => 1_000, "m" => 1_000_000, _ => 1 };
        return (long)Math.Round(v);
    }

    private static double Num(string s)
    {
        s = s.Trim();
        // „1,234“ = Tausender, „1.2“ = Dezimal
        if (s.Contains(',') && !s.Contains('.') && Regex.IsMatch(s, @",\d{3}$")) s = s.Replace(",", "");
        s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    [GeneratedRegex(@"^(Total usage est|Usage by model|Breakdown by AI model)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartRx();
    [GeneratedRegex(@"([\d.,]+)\s+Premium request", RegexOptions.IgnoreCase)]
    private static partial Regex PremiumRx();
    [GeneratedRegex(@"^(Total |API time|Session time|Total session)", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryKeyRx();
}
