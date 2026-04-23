using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class RazorTranslateCandidateExtractor(
    ILogger<RazorTranslateCandidateExtractor> _logger
)
{
    private static readonly Regex DoubleQuoteRegex = new(
        @"@Html\.Translate\(\s*""((?:\\.|[^""\\])*)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SingleQuoteRegex = new(
        @"@Html\.Translate\(\s*'((?:\\.|[^'\\])*)'\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagTextRegex = new(
        @">([^<>@]+)<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeTextRegex = new(
        @"(?:placeholder|title|aria-label|value)\s*=\s*[""']([^""'@][^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NumberRegex = new("^[\\d\\.,\\-\\+]+$", RegexOptions.Compiled);
    private static readonly Regex SymbolRegex = new("^[^\\p{L}\\p{N}]+$", RegexOptions.Compiled);

    public async Task<IReadOnlyCollection<TranslationCandidate>> ExtractAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TranslationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = 0;

        foreach (var filePath in EnumerateTargetFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
                continue;

            scannedFiles++;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Razor view file: {FilePath}", filePath);
                continue;
            }

            AddCandidates(results, seen, ExtractTranslateStrings(content), "html-translate", filePath);
            AddCandidates(results, seen, ExtractPlainTextLiterals(content), "view-literal", filePath);
        }

        _logger.LogInformation(
            "Extracted {Count} unique translation candidates from {ScannedFiles} Razor files.",
            results.Count,
            scannedFiles);
        return results;
    }

    private static IEnumerable<string> ExtractTranslateStrings(string content)
    {
        foreach (Match match in DoubleQuoteRegex.Matches(content))
        {
            if (match.Success)
                yield return SafeUnescape(match.Groups[1].Value);
        }

        foreach (Match match in SingleQuoteRegex.Matches(content))
        {
            if (match.Success)
                yield return SafeUnescape(match.Groups[1].Value);
        }
    }

    private static string Normalize(string value)
        => Regex.Replace(value.Trim(), "\\s+", " ");

    private static IEnumerable<string> EnumerateTargetFiles()
    {
        var solutionRoot = PathResolver.ResolveSolutionRoot();
        var viewRoots = Directory
            .EnumerateDirectories(solutionRoot, "Views", SearchOption.AllDirectories)
            .Where(Directory.Exists)
            .Where(x =>
                !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !x.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !x.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in viewRoots)
        foreach (var file in Directory.EnumerateFiles(root, "*.cshtml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static IEnumerable<string> ExtractPlainTextLiterals(string content)
    {
        foreach (Match match in TagTextRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in AttributeTextRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }
    }

    private static string SafeUnescape(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\'))
            return value ?? string.Empty;

        try
        {
            return Regex.Unescape(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static void AddCandidates(
        List<TranslationCandidate> results,
        HashSet<string> seen,
        IEnumerable<string> texts,
        string sourceType,
        string filePath)
    {
        foreach (var text in texts)
        {
            var normalized = Normalize(text);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!seen.Add(normalized))
                continue;

            results.Add(new TranslationCandidate
            {
                Text = normalized,
                SourceType = sourceType,
                Url = filePath
            });
        }
    }

    private static bool IsEligiblePlainText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Length < 2 || value.Length > 120)
            return false;

        if (value.Contains("{") || value.Contains("}") || value.Contains("@"))
            return false;

        if (NumberRegex.IsMatch(value) || SymbolRegex.IsMatch(value))
            return false;

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            return false;

        var blackList = new[] { "function(", "var ", "const ", "let ", "=>", "return ", "if(", "for(" };
        if (blackList.Any(value.Contains))
            return false;

        return true;
    }
}
