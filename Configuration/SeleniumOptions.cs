namespace Saga_MiniConsoleTranslate.Configuration;

public class SeleniumOptions
{
    public string Browser { get; set; } = "Chrome";
    public bool Headless { get; set; } = true;
    public int ImplicitWaitSeconds { get; set; } = 3;
    public int PageLoadTimeoutSeconds { get; set; } = 60;
    public int CommandTimeoutSeconds { get; set; } = 90;
    public bool TakeScreenshotOnError { get; set; } = true;
}
