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
        "Optionen sind optional. Frage nur, wenn es wirklich nötig ist. Stelle alle offenen Fragen gesammelt in EINER Antwort, " +
        "jede in einem eigenen Block – der Nutzer beantwortet sie zusammen.";

    /// <summary>Zusatz für den Auto-Modus: möglichst selbst entscheiden.</summary>
    public const string AutoInstructions =
        "Du arbeitest im Auto-Modus: Triff naheliegende Entscheidungen selbst und nenne deine Annahmen kurz. " +
        "Stelle Rückfragen nur, wenn du ohne die Antwort nicht sinnvoll weiterarbeiten kannst.";

    /// <summary>Baut aus den Antworten auf mehrere Rückfragen eine Nachricht. Unbeantwortete entscheidet die KI selbst.</summary>
    public static string ComposeAnswers(IReadOnlyList<(string Question, string? Answer)> answers)
    {
        if (answers.Count == 1 && !string.IsNullOrWhiteSpace(answers[0].Answer)) return answers[0].Answer!.Trim();
        var sb = new System.Text.StringBuilder("Antworten auf deine Rückfragen:\n");
        for (var i = 0; i < answers.Count; i++)
        {
            var (q, a) = answers[i];
            sb.Append(i + 1).Append(". ").Append(q).Append("\n   → ")
              .Append(string.IsNullOrWhiteSpace(a) ? "(keine Antwort – entscheide selbst sinnvoll und nenne deine Wahl)" : a.Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

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
