using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Copit.Core.Services;

/// <summary>Holt JSON aus einer KI-Antwort (```json Blöcke oder erstes balanciertes Objekt/Array).</summary>
public static partial class JsonExtract
{
    public static JsonNode? Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in FenceRegex().Matches(text))
        {
            var n = TryParse(m.Groups[1].Value);
            if (n != null) return n;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('{' or '[')) continue;
            var end = FindBalancedEnd(text, i);
            if (end < 0) continue;
            var n = TryParse(text[i..(end + 1)]);
            if (n != null) return n;
        }
        return null;
    }

    private static JsonNode? TryParse(string s)
    {
        try { return JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch { return null; }
    }

    private static int FindBalancedEnd(string s, int start)
    {
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    public static double Num(JsonNode? n, string prop, double fallback = 0)
    {
        var v = n?[prop];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var s) &&
                double.TryParse(s.Replace(',', '.').TrimEnd('h', ' '), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;
        }
        return fallback;
    }

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();
}
