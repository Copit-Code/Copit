using System.Text.RegularExpressions;

namespace Copit.Core.Ai;

public record AgentQuestion(string Text, IReadOnlyList<string> Options);

/// <summary>
/// Copit bittet die KI, Rückfragen in einem &lt;rueckfrage&gt;-Block zu stellen.
/// Die UI zeigt diese als klickbare Auswahl an.
/// </summary>
public static partial class QuestionParser
{
    public const string Instructions =
        "Wenn du vom Nutzer eine Entscheidung oder fehlende Information brauchst, stelle die Rückfrage am ENDE deiner Antwort " +
        "in genau diesem Format (mehrere Blöcke erlaubt):\n<rueckfrage>\nFrage: <deine Frage>\n- <Option 1>\n- <Option 2>\n</rueckfrage>\n" +
        "Optionen sind optional. Frage nur, wenn es wirklich nötig ist.";

    public static (string CleanText, List<AgentQuestion> Questions) Extract(string text)
    {
        var questions = new List<AgentQuestion>();
        if (string.IsNullOrEmpty(text)) return (text, questions);
        foreach (Match m in BlockRegex().Matches(text))
        {
            var lines = m.Groups[1].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var q = "";
            var options = new List<string>();
            foreach (var l in lines)
            {
                if (l.StartsWith("- ") || l.StartsWith("* ")) options.Add(l[2..].Trim());
                else if (l.StartsWith("Frage:", StringComparison.OrdinalIgnoreCase)) q = l[6..].Trim();
                else q = (q + " " + l).Trim();
            }
            if (q.Length > 0) questions.Add(new AgentQuestion(q, options));
        }
        var clean = BlockRegex().Replace(text, "").TrimEnd();
        return (clean, questions);
    }

    [GeneratedRegex(@"<rueckfrage>(.*?)</rueckfrage>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BlockRegex();
}
