using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class RazorTranslateCandidateExtractor(
    ILogger<RazorTranslateCandidateExtractor> _logger
)
{
    private static readonly Regex ScriptStyleBlockRegex = new(
        @"<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    private static readonly Regex TempDataLiteralRegex = new(
        @"TempData\s*\[\s*[^\]]+\s*\]\s*=\s*\$?""((?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TempDataVerbatimLiteralRegex = new(
        @"TempData\s*\[\s*[^\]]+\s*\]\s*=\s*\$?@""((?:""""|[^""])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TempDataSingleQuoteRegex = new(
        @"TempData\s*\[\s*[^\]]+\s*\]\s*=\s*'((?:\\.|[^'\\])*)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExceptionLiteralRegex = new(
        @"throw\s+new\s+\w*Exception\s*\(\s*\$?""((?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExceptionVerbatimLiteralRegex = new(
        @"throw\s+new\s+\w*Exception\s*\(\s*\$?@""((?:""""|[^""])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModelStateAddErrorLiteralRegex = new(
        @"ModelState\.AddModelError\s*\(\s*[^,]+,\s*\$?""((?:\\.|[^""\\])*)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModelStateAddErrorVerbatimLiteralRegex = new(
        @"ModelState\.AddModelError\s*\(\s*[^,]+,\s*\$?@""((?:""""|[^""])*)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModelStateAddErrorSingleQuoteRegex = new(
        @"ModelState\.AddModelError\s*\(\s*[^,]+,\s*'((?:\\.|[^'\\])*)'\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResultFailureArrayLiteralRegex = new(
        @"Result(?:<[^>]+>)?\.Failure\s*\(\s*new\[\]\s*\{[\s\S]*?\$?""((?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResultFailureArrayVerbatimRegex = new(
        @"Result(?:<[^>]+>)?\.Failure\s*\(\s*new\[\]\s*\{[\s\S]*?\$?@""((?:""""|[^""])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResultFailureCollectionLiteralRegex = new(
        @"Result(?:<[^>]+>)?\.Failure\s*\(\s*\[[\s\S]*?\$?""((?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResultFailureCollectionVerbatimRegex = new(
        @"Result(?:<[^>]+>)?\.Failure\s*\(\s*\[[\s\S]*?\$?@""((?:""""|[^""])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberRegex = new("^[\\d\\.,\\-\\+]+$", RegexOptions.Compiled);
    private static readonly Regex SymbolRegex = new("^[^\\p{L}\\p{N}]+$", RegexOptions.Compiled);

    public async Task<IReadOnlyCollection<TranslationCandidate>> ExtractAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TranslationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedRazorFiles = 0;
        var scannedCodeFiles = 0;

        foreach (var filePath in EnumerateRazorFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
                continue;

            scannedRazorFiles++;

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

        foreach (var filePath in EnumerateCodeMessageFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
                continue;

            scannedCodeFiles++;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read code file: {FilePath}", filePath);
                continue;
            }

            AddCandidates(results, seen, ExtractCodeMessageStrings(content), "code-message", filePath);
        }

        _logger.LogInformation(
            "Extracted {Count} unique translation candidates from {ScannedRazorFiles} Razor files and {ScannedCodeFiles} code files.",
            results.Count,
            scannedRazorFiles,
            scannedCodeFiles);
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

    private static IEnumerable<string> EnumerateRazorFiles()
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

    private static IEnumerable<string> EnumerateCodeMessageFiles()
    {
        var solutionRoot = PathResolver.ResolveSolutionRoot();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var moduleRoots = Directory
            .EnumerateDirectories(solutionRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(x =>
            {
                var name = Path.GetFileName(x);
                return name.StartsWith("Saga_", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Saga.MainApplication", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var moduleRoot in moduleRoots)
        {
            var controllerDir = Path.Combine(moduleRoot, "Controllers");
            if (!Directory.Exists(controllerDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(controllerDir, "*Controller.cs", SearchOption.AllDirectories))
            {
                if (!IsSkippablePath(file))
                    files.Add(file);
            }
        }

        var additionalRoots = new[]
        {
            Path.Combine(solutionRoot, "Saga_Core", "Saga.Mediator"),
            Path.Combine(solutionRoot, "Saga_Core", "Saga.Infrastructure"),
            Path.Combine(solutionRoot, "Saga_Core", "Saga.Validators")
        };

        foreach (var root in additionalRoots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!IsSkippablePath(file))
                    files.Add(file);
            }
        }

        foreach (var file in files)
            yield return file;
    }

    private static IEnumerable<string> ExtractPlainTextLiterals(string content)
    {
        var sanitizedContent = ScriptStyleBlockRegex.Replace(content, " ");

        foreach (Match match in TagTextRegex.Matches(sanitizedContent))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in AttributeTextRegex.Matches(sanitizedContent))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }
    }

    private static IEnumerable<string> ExtractCodeMessageStrings(string content)
    {
        foreach (Match match in TempDataLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in TempDataVerbatimLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var verbatim = match.Groups[1].Value.Replace("\"\"", "\"");
            var value = Normalize(verbatim);
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in TempDataSingleQuoteRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ExceptionLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ExceptionVerbatimLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var verbatim = match.Groups[1].Value.Replace("\"\"", "\"");
            var value = Normalize(verbatim);
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ModelStateAddErrorLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ModelStateAddErrorVerbatimLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var verbatim = match.Groups[1].Value.Replace("\"\"", "\"");
            var value = Normalize(verbatim);
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ModelStateAddErrorSingleQuoteRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ResultFailureArrayLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ResultFailureArrayVerbatimRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(match.Groups[1].Value.Replace("\"\"", "\""));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ResultFailureCollectionLiteralRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(SafeUnescape(match.Groups[1].Value));
            if (IsEligiblePlainText(value))
                yield return value;
        }

        foreach (Match match in ResultFailureCollectionVerbatimRegex.Matches(content))
        {
            if (!match.Success)
                continue;

            var value = Normalize(match.Groups[1].Value.Replace("\"\"", "\""));
            if (IsEligiblePlainText(value))
                yield return value;
        }
    }

    private static bool IsSkippablePath(string file)
    {
        return file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               file.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               file.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
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

        var blackList = new[]
        {
            "function(", "var ", "const ", "let ", "=>", "return ", "if(", "for(",
            " + ", " ? ", " : ", "==", "!=", "&&", "||", "$(", "item.", ".replace(", ".val("
        };
        if (blackList.Any(value.Contains))
            return false;

        if (value.Contains('`') || value.Contains("\\n", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
