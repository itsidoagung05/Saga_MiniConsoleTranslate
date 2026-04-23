namespace Saga_MiniConsoleTranslate.Models;

public class CrawlResult
{
    public List<CrawlPageResult> Pages { get; set; } = new();
    public List<TranslationCandidate> Candidates { get; set; } = new();
}
