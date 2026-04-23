using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saga_MiniConsoleTranslate.Configuration;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class TranslationReportWriter(
    IOptions<TranslationAutomationOptions> _optionsAccessor,
    ILogger<TranslationReportWriter> _logger
)
{
    private readonly TranslationAutomationOptions _options = _optionsAccessor.Value;

    public async Task WriteAsync(TranslationRunResult runResult, CancellationToken cancellationToken = default)
    {
        var reportsDirectory = ResolvePath(_options.ReportsDirectory);
        Directory.CreateDirectory(reportsDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var summaryPath = Path.Combine(reportsDirectory, $"summary-{timestamp}.json");
        var detailPath = Path.Combine(reportsDirectory, $"detail-{timestamp}.json");
        var textLogPath = Path.Combine(reportsDirectory, $"report-{timestamp}.txt");
        var residualPath = Path.Combine(reportsDirectory, $"residual-{timestamp}.json");

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(runResult.Summary, jsonOptions), cancellationToken);
        await File.WriteAllTextAsync(detailPath, JsonSerializer.Serialize(runResult.TranslationDetailsByLanguage, jsonOptions), cancellationToken);
        await File.WriteAllTextAsync(residualPath, JsonSerializer.Serialize(runResult.ResidualFindings, jsonOptions), cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine("Saga Mini Console Translate Report");
        builder.AppendLine($"Started UTC: {runResult.Summary.StartedAtUtc:O}");
        builder.AppendLine($"Ended UTC: {runResult.Summary.EndedAtUtc:O}");
        builder.AppendLine($"Visited URLs: {runResult.Summary.VisitedUrlCount}");
        builder.AppendLine($"Skipped Pages: {runResult.Summary.SkippedPageCount}");
        builder.AppendLine($"Page Errors: {runResult.Summary.PageErrorCount}");
        builder.AppendLine($"Candidate Texts: {runResult.Summary.CandidateTextCount}");
        builder.AppendLine($"Unique Texts: {runResult.Summary.UniqueTextCount}");
        builder.AppendLine($"Inserted Rows: {runResult.Summary.InsertedRowCount}");
        builder.AppendLine($"Updated Columns: {runResult.Summary.UpdatedColumnCount}");

        await File.WriteAllTextAsync(textLogPath, builder.ToString(), cancellationToken);

        _logger.LogInformation("Report files generated:");
        _logger.LogInformation("Summary: {SummaryPath}", summaryPath);
        _logger.LogInformation("Detail: {DetailPath}", detailPath);
        _logger.LogInformation("Text Log: {TextLogPath}", textLogPath);
        _logger.LogInformation("Residual: {ResidualPath}", residualPath);
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }
}
