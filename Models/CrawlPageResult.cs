namespace Saga_MiniConsoleTranslate.Models;

public class CrawlPageResult
{
    public string Url { get; set; } = string.Empty;
    public int Depth { get; set; }
    public bool IsSkipped { get; set; }
    public string? ErrorMessage { get; set; }
    public int CandidateCount { get; set; }
    public string? SnapshotPath { get; set; }
    public string? ScreenshotPath { get; set; }
}
