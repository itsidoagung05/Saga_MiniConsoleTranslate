namespace Saga_MiniConsoleTranslate.Configuration;

public class TranslationAutomationOptions
{
    public List<string> Languages { get; set; } = new();
    public int MaxPages { get; set; } = 200;
    public int MaxDepth { get; set; } = 4;
    public int DelayAfterNavigationMs { get; set; } = 600;
    public bool RefreshAfterBackfill { get; set; } = false;
    public bool ClickSafeTabsAndModals { get; set; } = true;
    public bool CopyBackToSourceSqlite { get; set; } = false;
    public string SqliteMode { get; set; } = "IsolatedCopy";
    public string SourceSqlitePath { get; set; } = "../Saga.MainApplication/LocalStorage.db";
    public string WorkingSqlitePath { get; set; } = "./runtime/LocalStorage.working.db";
    public string ReportsDirectory { get; set; } = "./reports";
    public string SnapshotsDirectory { get; set; } = "./snapshots";
    public bool EnableResidualDetection { get; set; } = true;
    public List<string> SkipUrlPatterns { get; set; } = new();
    public List<string> AllowUrlPatterns { get; set; } = new();
}
