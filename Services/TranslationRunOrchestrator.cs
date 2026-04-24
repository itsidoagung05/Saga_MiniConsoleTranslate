using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Saga.Domain.Enums;
using Saga.DomainShared.Models;
using Saga.Infrastructure.Helpers;
using Saga.Infrastructure.Interfaces;
using Saga.Persistence.Context;
using Saga_MiniConsoleTranslate.Configuration;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class TranslationRunOrchestrator(
    SqliteMirrorService _sqliteMirrorService,
    SagaMainApplicationLauncher _mainApplicationLauncher,
    SeleniumTranslationCrawler _crawler,
    RazorTranslateCandidateExtractor _razorTranslateCandidateExtractor,
    LocalDataContext _localDataContext,
    IEnumerable<IExternalTranslationProvider> _translationProviders,
    TranslationReportWriter _reportWriter,
    IOptions<TranslationAutomationOptions> _automationOptions,
    ILogger<TranslationRunOrchestrator> _logger
)
{
    private readonly TranslationAutomationOptions _translationAutomationOptions = _automationOptions.Value;

    public async Task<TranslationRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var runStarted = DateTimeOffset.UtcNow;
        var runResult = new TranslationRunResult();

        var mirror = await _sqliteMirrorService.PrepareAsync(cancellationToken);
        await _localDataContext.Database.EnsureCreatedAsync(cancellationToken);
        _logger.LogInformation("Ensured LocalStorage schema is created for path: {WorkingPath}", mirror.WorkingPath);
        var normalizedRows = await TranslatorHelper.NormalizeEncodedColumnsAsync(_localDataContext, cancellationToken);
        if (normalizedRows > 0)
            _logger.LogInformation("Normalized encoded language values for {Count} rows.", normalizedRows);
        if (_translationAutomationOptions.CleanupNoiseLanguageRows)
        {
            var deletedNoiseWords = await TranslatorHelper.DeleteNoiseLanguageRowsAsync(_localDataContext, cancellationToken);
            runResult.DeletedNoiseWords = deletedNoiseWords.ToList();
            if (deletedNoiseWords.Count > 0)
                _logger.LogInformation("Deleted {Count} noise Language rows detected as JS/plain-literal fragments.", deletedNoiseWords.Count);
        }

        await using var appHandle = await _mainApplicationLauncher.LaunchOrAttachAsync(
            $"Data Source={mirror.WorkingPath}",
            cancellationToken);

        var crawlResult = await _crawler.CrawlAsync(appHandle.BaseUrl, cancellationToken);
        runResult.CrawlPages = crawlResult.Pages;

        var razorCandidates = await _razorTranslateCandidateExtractor.ExtractAsync(cancellationToken);
        if (razorCandidates.Count > 0)
            _logger.LogInformation("Razor candidates found: {Count}", razorCandidates.Count);
        else
            _logger.LogWarning("No Razor @Html.Translate candidates found.");

        var sourceCandidates = razorCandidates
            .Concat(crawlResult.Candidates)
            .GroupBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        _logger.LogInformation(
            "Combined translation candidates: {Total} (Razor: {RazorCount}, Crawled: {CrawlCount})",
            sourceCandidates.Count,
            razorCandidates.Count,
            crawlResult.Candidates.Count);

        crawlResult.Candidates = sourceCandidates.ToList();

        var uniqueCandidates = sourceCandidates
            .Select(x => x.Text)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var language in BuildTargetLanguages())
        {
            var ensured = await TranslatorHelper.EnsureBatchTranslatedAsync(
                _localDataContext,
                _translationProviders,
                _logger,
                uniqueCandidates,
                language,
                cancellationToken);
            runResult.TranslationDetailsByLanguage[language.ToString()] = ensured.ToList();
        }

        if (_translationAutomationOptions.EnableResidualDetection)
            runResult.ResidualFindings = BuildResidualFindings(runResult.TranslationDetailsByLanguage);

        await _sqliteMirrorService.CopyBackAsync(mirror, cancellationToken);

        runResult.Summary = BuildSummary(
            runStarted,
            DateTimeOffset.UtcNow,
            mirror,
            crawlResult,
            runResult.TranslationDetailsByLanguage,
            runResult.DeletedNoiseWords.Count);
        await _reportWriter.WriteAsync(runResult, cancellationToken);

        return runResult;
    }

    private IReadOnlyCollection<ProfileLanguage> BuildTargetLanguages()
    {
        var languages = new List<ProfileLanguage>();
        foreach (var languageRaw in _translationAutomationOptions.Languages)
        {
            if (!Enum.TryParse<ProfileLanguage>(languageRaw, true, out var language))
            {
                _logger.LogWarning("Skipping invalid language value: {LanguageValue}", languageRaw);
                continue;
            }

            if (!languages.Contains(language))
                languages.Add(language);
        }

        if (!languages.Contains(ProfileLanguage.Bahasa))
        {
            languages.Insert(0, ProfileLanguage.Bahasa);
            _logger.LogInformation("Target language 'Bahasa' was added automatically to ensure Indonesia column is backfilled.");
        }

        return languages;
    }

    private static List<ResidualTextFinding> BuildResidualFindings(Dictionary<string, List<TranslationEnsureResult>> detailsByLanguage)
    {
        var findings = new List<ResidualTextFinding>();

        foreach (var (language, items) in detailsByLanguage)
        {
            foreach (var item in items.Where(x =>
                         x.UsedFallbackText || string.Equals(x.SourceText, x.TranslatedText, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new ResidualTextFinding
                {
                    Url = string.Empty,
                    Text = item.SourceText,
                    Reason = $"Potential residual in language '{language}' because translated text equals source or fallback was used."
                });
            }
        }

        return findings;
    }

    private static TranslationRunSummary BuildSummary(
        DateTimeOffset start,
        DateTimeOffset end,
        SqliteMirrorResult mirror,
        CrawlResult crawlResult,
        Dictionary<string, List<TranslationEnsureResult>> detailsByLanguage,
        int deletedNoiseWordCount)
    {
        var allResults = detailsByLanguage.SelectMany(x => x.Value).ToList();
        var providerSuccess = allResults
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderUsed))
            .GroupBy(x => x.ProviderUsed!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var providerFailure = allResults
            .Where(x => string.IsNullOrWhiteSpace(x.ProviderUsed) || x.UsedFallbackText)
            .GroupBy(x => x.ProviderUsed ?? "Fallback", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        return new TranslationRunSummary
        {
            StartedAtUtc = start,
            EndedAtUtc = end,
            SqliteMode = mirror.Mode,
            SourceSqlitePath = mirror.SourcePath,
            WorkingSqlitePath = mirror.WorkingPath,
            VisitedUrlCount = crawlResult.Pages.Count(x => !x.IsSkipped),
            SkippedPageCount = crawlResult.Pages.Count(x => x.IsSkipped),
            PageErrorCount = crawlResult.Pages.Count(x => !string.IsNullOrWhiteSpace(x.ErrorMessage)),
            CandidateTextCount = crawlResult.Candidates.Count,
            UniqueTextCount = crawlResult.Candidates.Select(x => x.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            InsertedRowCount = allResults.Count(x => x.InsertedNewRow),
            UpdatedColumnCount = allResults.Count(x => x.UpdatedTargetColumn),
            DeletedNoiseWordCount = deletedNoiseWordCount,
            ProviderSuccessCount = providerSuccess,
            ProviderFailureCount = providerFailure,
            LanguageProcessedCount = detailsByLanguage.ToDictionary(x => x.Key, x => x.Value.Count, StringComparer.OrdinalIgnoreCase)
        };
    }
}
