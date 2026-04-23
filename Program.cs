using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Saga_MiniConsoleTranslate.Extensions;
using Saga_MiniConsoleTranslate.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddMiniConsoleServices(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

using var host = builder.Build();

try
{
    using var scope = host.Services.CreateScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<TranslationRunOrchestrator>();
    await orchestrator.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Translation mini console run failed.");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
