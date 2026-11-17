using System.Globalization;
using System.Text.RegularExpressions;

namespace Copit.Core.Ai;

/// <summary>
/// Liest die Nutzungs-Zusammenfassung, die die Copilot CLI im Prompt-Modus (-p) am Ende auf stderr ausgibt.
/// Aktuelles Format (Copilot CLI 1.x):
/// <code>
/// Changes    +12 -3
/// Requests   1 Premium (8s)
/// Tokens     ↑ 12.6k (10.2k cached, 1k written) • ↓ 297 (50 reasoning)
/// </code>
/// Für Konten mit AI Credits steht statt „Requests“ die Zeile <c>AI Credits 0.12 (8s)</c>.
/// Ältere Formate („Total usage est … / Usage by model:“) werden weiterhin erkannt.
/// Zeilen der Zusammenfassung gehören nicht zur Antwort (<see cref="Feed"/> liefert dann true).
/// </summary>
public sealed partial class CopilotUsageParser
{
    private bool _inLegacySummary;
    private long _in, _out, _cacheRead, _cacheWrite;
    private bool _hasTokens;

    public double PremiumRequests { get; private set; }
    /// <summary>AI Credits (nur Konten mit Credit-Abrechnung).</summary>
    public double AiCredits { get; private set; }
    public long LinesAdded { get; private set; }
    public long LinesRemoved { get; private set; }
    public bool Found => _hasTokens || PremiumRequests > 0 || AiCredits > 0;
    public TokenUsage? Usage => _hasTokens ? new TokenUsage(_in, _out, _cacheRead, _cacheWrite) : null;

    /// <summary>Verarbeitet eine Ausgabezeile. true = Zeile gehört zur Zusammenfassung.</summary>
    public bool Feed(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return _inLegacySummary;

        // ---- aktuelles Format
        if (TokensRx().Match(line) is { Success: true } t)
        {
            var input = Num(t.Groups["in"].Value);
            var cached = Part(t.Groups["inx"].Value, "cached");
            var written = Part(t.Groups["inx"].Value, "written");
            // „↑“ ist meist die gesamte Eingabe inkl. Cache → frische Eingabe = Rest.
            // Ist der Cache größer als „↑“, zählt „↑“ den Cache nicht mit → unverändert übernehmen.
            _in += input >= cached + written ? input - cached - written : input;
            _cacheRead += cached;
            _cacheWrite += written;
            _out += Num(t.Groups["out"].Value);
            _hasTokens = true;
            return true;
        }
        if (RequestsRx().Match(line) is { Success: true } r) { PremiumRequests += Dbl(r.Groups[1].Value); return true; }
        if (CreditsRx().Match(line) is { Success: true } c) { AiCredits += Dbl(c.Groups[1].Value.TrimStart('<')); return true; }
        if (ChangesRx().Match(line) is { Success: true } ch) { LinesAdded += Num(ch.Groups[1].Value); LinesRemoved += Num(ch.Groups[2].Value); return true; }
        if (DurationRx().IsMatch(line)) return true;

        // ---- ältere Formate
        if (LegacyStartRx().IsMatch(line))
        {
            _inLegacySummary = true;
            var m = PremiumRx().Match(line);
            if (line.StartsWith("Total usage", StringComparison.OrdinalIgnoreCase) && m.Success)
                PremiumRequests = Dbl(m.Groups[1].Value);
            return true;
        }
        if (!_inLegacySummary) return false;
        if (LegacyKeyRx().IsMatch(line)) return true;
        if (LegacyModelLine(line)) return true;
        return line.Contains(':');
    }

    private bool LegacyModelLine(string line)
    {
        long? input = Tok(line, @"(?:input|in)"), output = Tok(line, @"(?:output|out)");
        if (input == null && output == null) return false;
        var cacheRead = Tok(line, @"cache\s*read") ?? 0;
        var cacheWrite = Tok(line, @"cache\s*write") ?? 0;
        var cached = Tok(line, @"cached") ?? 0;
        var i = input ?? 0;
        if (cached > 0) { cacheRead += cached; i = Math.Max(0, i - cached); }
        _in += i; _out += output ?? 0; _cacheRead += cacheRead; _cacheWrite += cacheWrite;
        _hasTokens = true;
        return true;
    }

    /// <summary>„10.2k cached, 1k written“ → Wert zum Stichwort.</summary>
    private static long Part(string text, string label)
    {
        var m = Regex.Match(text, @"([\d][\d.,]*\s*[kKmM]?)\s+" + label, RegexOptions.IgnoreCase);
        return m.Success ? Num(m.Groups[1].Value) : 0;
    }

    private static long? Tok(string line, string label)
    {
        var m = Regex.Match(line, @"([\d][\d.,]*\s*[kKmM]?)\s+" + label + @"\b", RegexOptions.IgnoreCase);
        return m.Success ? Num(m.Groups[1].Value) : null;
    }

    /// <summary>„12.6k“, „1.2m“, „1,234“, „297“ → Zahl.</summary>
    public static long Num(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        var mult = 1d;
        switch (char.ToLowerInvariant(s[^1]))
        {
            case 'k': mult = 1_000; s = s[..^1].Trim(); break;
            case 'm': mult = 1_000_000; s = s[..^1].Trim(); break;
        }
        return (long)Math.Round(Dbl(s) * mult);
    }

    private static double Dbl(string s)
    {
        s = s.Trim();
        // „1,234“ = Tausender, „1.2“ = Dezimal
        if (s.Contains(',') && !s.Contains('.') && Regex.IsMatch(s, @",\d{3}$")) s = s.Replace(",", "");
        s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // Pfeile/Trenner tolerant: bei falscher Konsolen-Codierung kommen „↑ • ↓“ als andere Zeichen an
    [GeneratedRegex(@"^Tokens\s+[^\d\s]*\s*(?<in>[\d.,]+\s*[kKmM]?)\s*(?:\((?<inx>[^)]*)\))?\s*[^\d\s(]+\s*[^\d\s]*\s*(?<out>[\d.,]+\s*[kKmM]?)", RegexOptions.IgnoreCase)]
    private static partial Regex TokensRx();
    [GeneratedRegex(@"^Requests\s+([\d.,]+)\s+Premium\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequestsRx();
    [GeneratedRegex(@"^AI Credits\s+(<?[\d.,]+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsRx();
    [GeneratedRegex(@"^Changes\s+\+(\d+)\s+-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangesRx();
    [GeneratedRegex(@"^Duration\s+(\d+h\s*)?(\d+m\s*)?\d+s$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRx();

    [GeneratedRegex(@"^(Total usage est|Usage by model|Breakdown by AI model)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyStartRx();
    [GeneratedRegex(@"([\d.,]+)\s+Premium request", RegexOptions.IgnoreCase)]
    private static partial Regex PremiumRx();
    [GeneratedRegex(@"^(Total |API time|Session time|Total session)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyKeyRx();
}
