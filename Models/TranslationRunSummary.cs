namespace Saga_MiniConsoleTranslate.Models;

public class TranslationRunSummary
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public string SqliteMode { get; set; } = string.Empty;
    public string SourceSqlitePath { get; set; } = string.Empty;
    public string WorkingSqlitePath { get; set; } = string.Empty;
    public int VisitedUrlCount { get; set; }
    public int SkippedPageCount { get; set; }
    public int PageErrorCount { get; set; }
    public int CandidateTextCount { get; set; }
    public int UniqueTextCount { get; set; }
    public int InsertedRowCount { get; set; }
    public int UpdatedColumnCount { get; set; }
    public Dictionary<string, int> ProviderSuccessCount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ProviderFailureCount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> LanguageProcessedCount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
