namespace Saga_MiniConsoleTranslate.Configuration;

public class MainApplicationRunnerOptions
{
    public bool UseExistingRunningApp { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string ProjectPath { get; set; } = "../Saga.MainApplication/Saga.MainApplication.csproj";
    public string WorkingDirectory { get; set; } = "../Saga.MainApplication";
    public string HealthUrl { get; set; } = "/Authorization/Login";
    public int StartTimeoutSeconds { get; set; } = 120;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}
