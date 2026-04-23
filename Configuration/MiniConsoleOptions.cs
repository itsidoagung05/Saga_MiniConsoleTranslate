namespace Saga_MiniConsoleTranslate.Configuration;

public class MiniConsoleOptions
{
    public MainApplicationRunnerOptions MainApplicationRunner { get; set; } = new();
    public AutomationAccountOptions AutomationAccount { get; set; } = new();
    public SeleniumOptions Selenium { get; set; } = new();
    public TranslationAutomationOptions TranslationAutomation { get; set; } = new();
}
