using Saga.DomainShared.Models;

namespace Saga_MiniConsoleTranslate.Models;

public class TranslationRunResult
{
    public TranslationRunSummary Summary { get; set; } = new();
    public Dictionary<string, List<TranslationEnsureResult>> TranslationDetailsByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CrawlPageResult> CrawlPages { get; set; } = new();
    public List<ResidualTextFinding> ResidualFindings { get; set; } = new();
}
