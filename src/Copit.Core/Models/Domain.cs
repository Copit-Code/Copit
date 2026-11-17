namespace Copit.Core.Models;

public class CopitProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string EpicKey { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    /// <summary>GitLab Projekt-Pfad (group/repo) oder ID. Wird aus dem Git-Remote erkannt.</summary>
    public string GitLabProject { get; set; } = "";
    public string Color { get; set; } = "#8B5CF6";
    public AiProvider? Provider { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastOpened { get; set; } = DateTime.Now;
    /// <summary>Projektspezifische Anweisungen für die KI.</summary>
    public string Instructions { get; set; } = "";
    /// <summary>Ordner in der Projektliste ("" = ohne Ordner), Unterordner mit '/', z. B. "Kunde A/Backend".</summary>
    public string Group { get; set; } = "";

    public string JiraProjectKey => EpicKey.Contains('-') ? EpicKey[..EpicKey.LastIndexOf('-')] : EpicKey;
}

public class ProjectNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
}

public enum IssueBucket { Open, InProgress, Review, Done }

public class JiraIssue
{
    public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusCategory { get; set; } = "";
    public string IssueType { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string Priority { get; set; } = "";
    public long OriginalEstimateSeconds { get; set; }
    public long TimeSpentSeconds { get; set; }
    public long RemainingEstimateSeconds { get; set; }
    public DateTime? Updated { get; set; }
    public string? Account { get; set; }
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string ProjectKey => Key.Contains('-') ? Key[..Key.LastIndexOf('-')] : Key;

    public IssueBucket Bucket
    {
        get
        {
            var s = Status.ToLowerInvariant();
            if (StatusCategory == "done") return IssueBucket.Done;
            if (s.Contains("review") || s.Contains("prüf") || s.Contains("test") || s.Contains("qa") || s.Contains("abnahme"))
                return IssueBucket.Review;
            if (StatusCategory == "indeterminate") return IssueBucket.InProgress;
            return IssueBucket.Open;
        }
    }
}

public class JiraWorklog
{
    public string Id { get; set; } = "";
    public string IssueKey { get; set; } = "";
    public string IssueSummary { get; set; } = "";
    public DateTime Started { get; set; }
    public long TimeSpentSeconds { get; set; }
    public string Comment { get; set; } = "";
    public double Hours => TimeSpentSeconds / 3600.0;
    /// <summary>Account der Buchung (Tempo-Attribut bzw. Account-Feld des Tickets).</summary>
    public string? Account { get; set; }
    /// <summary>true = Account-Information liegt vor (auch wenn leer); false = unbekannt (z. B. Tempo nicht erreichbar).</summary>
    public bool AccountChecked { get; set; }
    /// <summary>ID des Worklogs in Tempo – zum Nachtragen des Accounts.</summary>
    public string? TempoId { get; set; }
    public bool HasAccount => !string.IsNullOrWhiteSpace(Account);
}

public class DayEntry
{
    public DateOnly Date { get; set; }
    public string Note { get; set; } = "";
    /// <summary>Optional abweichendes Stundensoll (z.B. halber Tag Urlaub).</summary>
    public double? TargetHours { get; set; }
    /// <summary>Zuletzt erzeugte Vorschläge (bleiben nach Neustart erhalten).</summary>
    public List<BookingSuggestion> LastSuggestions { get; set; } = [];
    public string LastSummary { get; set; } = "";
    /// <summary>Offene Rückfragen der KI zu diesem Tag.</summary>
    public List<TimeQuestion> Questions { get; set; } = [];
}

/// <summary>Rückfrage der KI beim Buchen (Antwort wird als Wissen gemerkt).</summary>
public class TimeQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string? Answer { get; set; }
}

/// <summary>Gelerntes Wissen aus beantworteten Rückfragen.</summary>
public class TimeFact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Gelernte Zuordnung Thema/Repo → Ticket (z. B. Weiterentwicklungsprojekte).</summary>
public class TopicMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Topic { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string? Repo { get; set; }
    public int Hits { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.Now;
    /// <summary>Vom Nutzer festgelegt – hat Vorrang.</summary>
    public bool Manual { get; set; }
}

public class TimeMemory
{
    public List<TopicMapping> Topics { get; set; } = [];
    public List<TimeFact> Facts { get; set; } = [];
}

/// <summary>Welche Quellen die Analyse nutzt.</summary>
public class BookingSources
{
    public bool Tickets { get; set; } = true;
    public bool Commits { get; set; } = true;
    public bool GitLab { get; set; } = true;
    public bool Sessions { get; set; } = true;
    public bool Note { get; set; } = true;
}

/// <summary>KI-Session eines Tages (aktive Minuten) als Signal für die Verteilung.</summary>
public record DaySession(string Title, string Project, string? Branch, string? Ticket, int Minutes);

public class BookingSuggestion
{
    public string IssueKey { get; set; } = "";
    public string IssueSummary { get; set; } = "";
    public double Hours { get; set; }
    public string Comment { get; set; } = "";
    public string Reason { get; set; } = "";
    public double Confidence { get; set; } = 0.7;
    public string? Account { get; set; }
    public bool AccountFromMemory { get; set; }
    /// <summary>Account kommt direkt aus dem Jira-Ticket (Account-Feld).</summary>
    public bool AccountFromIssue { get; set; }
    /// <summary>Account, den Tempo nach dem Buchen tatsächlich zurückgemeldet hat.</summary>
    public string? BookedAccount { get; set; }
    /// <summary>Geschätzter Aufwand einer Person ohne KI (h).</summary>
    public double HumanHours { get; set; }
    public bool Booked { get; set; }
    /// <summary>Kurzes Thema (2–4 Wörter) – Schlüssel fürs Themen-Gedächtnis.</summary>
    public string? Topic { get; set; }
    public string? Repo { get; set; }
}

public class AccountMemory
{
    /// <summary>Ticket → Account.</summary>
    public Dictionary<string, string> IssueAccounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Jira-Projekt → Account (Fallback).</summary>
    public Dictionary<string, string> ProjectAccounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KnownAccounts { get; set; } = [];
}

public record TempoAccount(string Key, string Name, string Id = "");

public record PermissionDenial(string ToolName, string ToolUseId, string InputJson);
