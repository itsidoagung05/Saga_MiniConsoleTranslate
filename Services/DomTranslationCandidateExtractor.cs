using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class DomTranslationCandidateExtractor
{
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new("^[\\d\\.,\\-\\+]+$", RegexOptions.Compiled);
    private static readonly Regex SymbolRegex = new("^[^\\p{L}\\p{N}]+$", RegexOptions.Compiled);

    public IReadOnlyCollection<TranslationCandidate> Extract(string html, string url)
    {
        var results = new List<TranslationCandidate>();
        if (string.IsNullOrWhiteSpace(html))
            return results;

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var markedNodes = document.DocumentNode.SelectNodes("//*[@data-saga-translate-source or @data-saga-translate-key]")
                         ?? Enumerable.Empty<HtmlNode>();

        foreach (var node in markedNodes)
        {
            var source = node.GetAttributeValue("data-saga-translate-source", string.Empty)
                         ?? node.GetAttributeValue("data-saga-translate-key", string.Empty)
                         ?? node.InnerText;

            AddCandidate(results, seen, source, "marker", url);
        }

        var textNodes = document.DocumentNode
            .Descendants()
            .Where(x => x.NodeType == HtmlNodeType.Text)
            .Where(x => x.ParentNode != null)
            .Where(x => !new[] { "script", "style", "noscript" }.Contains(x.ParentNode.Name, StringComparer.OrdinalIgnoreCase));

        foreach (var node in textNodes)
            AddCandidate(results, seen, node.InnerText, "visible", url);

        return results;
    }

    private static void AddCandidate(
        List<TranslationCandidate> results,
        HashSet<string> seen,
        string? source,
        string sourceType,
        string url)
    {
        var normalized = NormalizeText(source);
        if (!IsEligible(normalized))
            return;

        if (!seen.Add(normalized))
            return;

        results.Add(new TranslationCandidate
        {
            Text = normalized,
            SourceType = sourceType,
            Url = url
        });
    }

    private static bool IsEligible(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (NumberRegex.IsMatch(text))
            return false;

        if (SymbolRegex.IsMatch(text))
            return false;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
            return false;

        return true;
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var collapsed = WhitespaceRegex.Replace(text.Trim(), " ");
        return collapsed;
    }
}
