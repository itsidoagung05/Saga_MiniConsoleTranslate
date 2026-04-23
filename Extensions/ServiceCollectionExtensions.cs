using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saga.Infrastructure;
using Saga.Persistence.Context;
using Saga_MiniConsoleTranslate.Configuration;
using Saga_MiniConsoleTranslate.Services;
using Serilog;

namespace Saga_MiniConsoleTranslate.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMiniConsoleServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MainApplicationRunnerOptions>(configuration.GetSection("MainApplicationRunner"));
        services.Configure<AutomationAccountOptions>(configuration.GetSection("AutomationAccount"));
        services.Configure<SeleniumOptions>(configuration.GetSection("Selenium"));
        services.Configure<TranslationAutomationOptions>(configuration.GetSection("TranslationAutomation"));

        var translationOptions = configuration.GetSection("TranslationAutomation").Get<TranslationAutomationOptions>() ?? new TranslationAutomationOptions();
        var sqlitePath = translationOptions.SqliteMode.Equals("SharedFile", StringComparison.OrdinalIgnoreCase)
            ? PathResolver.ResolveForRead(translationOptions.SourceSqlitePath)
            : PathResolver.ResolveForWrite(translationOptions.WorkingSqlitePath);
        var sqliteConnectionString = $"Data Source={sqlitePath}";

        services.AddDbContext<LocalDataContext>(opt => opt.UseSqlite(sqliteConnectionString));
        services.AddSagaTranslationAutomationServices(configuration);

        services.AddSingleton<SqliteMirrorService>();
        services.AddSingleton<SagaMainApplicationLauncher>();
        services.AddSingleton<DomTranslationCandidateExtractor>();
        services.AddSingleton<RazorTranslateCandidateExtractor>();
        services.AddSingleton<SeleniumTranslationCrawler>();
        services.AddSingleton<TranslationReportWriter>();
        services.AddScoped<TranslationRunOrchestrator>();

        services.AddLogging(logging => logging.AddSerilog(Log.Logger, dispose: false));

        return services;
    }
}
