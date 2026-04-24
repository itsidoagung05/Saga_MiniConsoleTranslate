using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.RegularExpressions;
using Saga_MiniConsoleTranslate.Configuration;
using Saga_MiniConsoleTranslate.Models;

namespace Saga_MiniConsoleTranslate.Services;

public class SeleniumTranslationCrawler(
    IOptions<SeleniumOptions> _seleniumOptionsAccessor,
    IOptions<TranslationAutomationOptions> _automationOptionsAccessor,
    IOptions<AutomationAccountOptions> _accountOptionsAccessor,
    DomTranslationCandidateExtractor _extractor,
    ILogger<SeleniumTranslationCrawler> _logger
)
{
    private static readonly Regex RoutePattern = new(
        @"(?:(?:href|url|location|window\.open|navigate)\s*[:=]\s*)?['""](?<path>/(?:[A-Za-z0-9_\-]+/?){1,6}(?:\?[^'""]*)?)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SeleniumOptions _seleniumOptions = _seleniumOptionsAccessor.Value;
    private readonly TranslationAutomationOptions _automationOptions = _automationOptionsAccessor.Value;
    private readonly AutomationAccountOptions _accountOptions = _accountOptionsAccessor.Value;

    public async Task<CrawlResult> CrawlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var result = new CrawlResult();
        var accounts = BuildAccounts();
        var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(playwright, cancellationToken);

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                IgnoreHTTPSErrors = true
            });

            var tracePath = GetTracePath(account.Email);
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true,
                Title = $"crawl-{account.Email}"
            });

            var page = await context.NewPageAsync();
            try
            {
                await LoginAsync(page, baseUrl, account.Email, account.Password, cancellationToken);
                await CrawlForAccountAsync(page, baseUrl, result, cancellationToken);
            }
            finally
            {
                await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
            }
        }

        return result;
    }

    private async Task CrawlForAccountAsync(
        IPage page,
        string baseUrl,
        CrawlResult result,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Url, int Depth)>();

        foreach (var seed in GetSeedUrls(page, baseUrl))
            queue.Enqueue((seed, 0));

        while (queue.Count > 0 && visited.Count < _automationOptions.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (url, depth) = queue.Dequeue();
            if (depth > _automationOptions.MaxDepth)
                continue;

            if (!visited.Add(url))
                continue;

            if (!IsAllowedUrl(url))
            {
                result.Pages.Add(new CrawlPageResult { Url = url, Depth = depth, IsSkipped = true });
                continue;
            }

            var pageResult = new CrawlPageResult { Url = url, Depth = depth };
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = Math.Max(30000, _seleniumOptions.PageLoadTimeoutSeconds * 1000)
                });
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await Task.Delay(_automationOptions.DelayAfterNavigationMs, cancellationToken);

                if (_automationOptions.ClickSafeTabsAndModals)
                    await OpenSafeInteractiveElementsAsync(page, cancellationToken);
                await ExpandNavigationAsync(page, cancellationToken);

                var html = await page.ContentAsync();
                var candidates = _extractor.Extract(html, url);
                pageResult.CandidateCount = candidates.Count;

                result.Candidates.AddRange(candidates);
                pageResult.SnapshotPath = await WriteSnapshotAsync(url, html, cancellationToken);

                foreach (var link in await GetSafeLinksAsync(page, baseUrl))
                {
                    if (!visited.Contains(link))
                        queue.Enqueue((link, depth + 1));
                }

                foreach (var link in GetSafeLinksFromHtml(html, baseUrl))
                {
                    if (!visited.Contains(link))
                        queue.Enqueue((link, depth + 1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed crawling page {Url}", url);
                pageResult.ErrorMessage = ex.Message;
                if (_seleniumOptions.TakeScreenshotOnError)
                    pageResult.ScreenshotPath = await TakeScreenshotAsync(page, url, cancellationToken);
            }

            result.Pages.Add(pageResult);
        }
    }

    private async Task LoginAsync(IPage page, string baseUrl, string email, string password, CancellationToken cancellationToken)
    {
        var loginUrl = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "Authorization/Login").ToString();
        await page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#username").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await page.Locator("#username").FillAsync(email);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#btnSubmit").ClickAsync();

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Task.Delay(_automationOptions.DelayAfterNavigationMs, cancellationToken);
    }

    private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var browserName = (_seleniumOptions.Browser ?? "chromium").Trim().ToLowerInvariant();
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = _seleniumOptions.Headless,
            Timeout = Math.Max(30000, _seleniumOptions.CommandTimeoutSeconds * 1000),
            Args = new[] { "--disable-dev-shm-usage", "--no-sandbox" }
        };

        return browserName switch
        {
            "firefox" => await playwright.Firefox.LaunchAsync(launchOptions),
            "webkit" => await playwright.Webkit.LaunchAsync(launchOptions),
            _ => await playwright.Chromium.LaunchAsync(launchOptions)
        };
    }

    private async Task<IReadOnlyCollection<string>> GetSafeLinksAsync(IPage page, string baseUrl)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var anchors = page.Locator("a[href]");
        var count = await anchors.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var href = await anchors.Nth(i).GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var uri))
                continue;

            if (!uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            var normalized = NormalizeUrl(uri);
            if (ContainsDangerousPattern(normalized))
                continue;

            links.Add(normalized);
        }

        var domLinks = await page.EvaluateAsync<string[]>(@"
() => {
  const values = [];
  const attrs = ['href','data-href','data-url','formaction','onclick'];
  document.querySelectorAll('a,button,input[type=""button""],input[type=""submit""],form,[data-url],[data-href]').forEach(el => {
    attrs.forEach(attr => {
      const value = el.getAttribute(attr);
      if (value) values.push(value);
    });
  });
  return values;
}");

        foreach (var raw in domLinks ?? Array.Empty<string>())
        {
            foreach (var extracted in ExtractPossibleUrls(raw))
            {
                if (!Uri.TryCreate(baseUri, extracted, out var uri))
                    continue;

                if (!uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
                    continue;

                var normalized = NormalizeUrl(uri);
                if (!ContainsDangerousPattern(normalized))
                    links.Add(normalized);
            }
        }

        return links;
    }

    private async Task OpenSafeInteractiveElementsAsync(IPage page, CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            "[data-bs-toggle='tab']",
            "[data-bs-toggle='pill']",
            "[data-bs-toggle='collapse']",
            "[data-bs-toggle='modal']",
            "[data-bs-toggle='offcanvas']",
            "[data-toggle='tab']",
            "[data-toggle='collapse']",
            "[data-toggle='modal']",
            ".nav-link",
            ".dropdown-item",
            ".menu-item",
            ".btn[data-bs-target]",
            ".btn[data-target]"
        };

        foreach (var selector in selectors)
        {
            var elements = page.Locator(selector);
            var count = await elements.CountAsync();
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var element = elements.Nth(i);

                try
                {
                    var text = (await element.InnerTextAsync()).Trim();
                    if (ContainsDangerousPattern(text))
                        continue;

                    await element.ClickAsync(new LocatorClickOptions { Timeout = 1000 });
                    await Task.Delay(Math.Max(50, _automationOptions.DelayAfterNavigationMs / 2), cancellationToken);
                }
                catch
                {
                }
            }
        }
    }

    private async Task ExpandNavigationAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.EvaluateAsync(@"() => {
                const elements = document.querySelectorAll('.treeview > a, .menu-item > a, [data-toggle=""collapse""], [data-bs-toggle=""collapse""]');
                elements.forEach(el => {
                    const text = (el.textContent || '').toLowerCase();
                    if (['save','submit','delete','approve','reject','process','confirm','logout'].some(x => text.includes(x))) return;
                    el.click();
                });
                window.scrollTo(0, document.body.scrollHeight);
            }");
            await Task.Delay(Math.Max(100, _automationOptions.DelayAfterNavigationMs / 2), cancellationToken);
        }
        catch
        {
        }
    }

    private IReadOnlyCollection<(string Email, string Password)> BuildAccounts()
    {
        var accounts = new List<(string Email, string Password)>();
        if (!string.IsNullOrWhiteSpace(_accountOptions.Email) && !string.IsNullOrWhiteSpace(_accountOptions.Password))
            accounts.Add((_accountOptions.Email.Trim(), _accountOptions.Password));

        if (!string.IsNullOrWhiteSpace(_accountOptions.EmployeeEmail) &&
            !string.IsNullOrWhiteSpace(_accountOptions.EmployeePassword))
        {
            var employeeEmail = _accountOptions.EmployeeEmail.Trim();
            if (!accounts.Any(x => x.Email.Equals(employeeEmail, StringComparison.OrdinalIgnoreCase)))
                accounts.Add((employeeEmail, _accountOptions.EmployeePassword));
        }

        if (accounts.Count == 0)
            throw new InvalidOperationException("No automation account credentials configured.");

        return accounts;
    }

    private static IEnumerable<string> GetSeedUrls(IPage page, string baseUrl)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new Uri(baseUri, "Main/Index").ToString(),
            new Uri(baseUri, "Main/Dashboard").ToString(),
            new Uri(baseUri, "Main/AdministratorDashboard").ToString(),
            new Uri(baseUri, "AdminDashboard/Index").ToString(),
            new Uri(baseUri, "EmployeeDashboard/Index").ToString()
        };

        if (Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUri) &&
            currentUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            seeds.Add(NormalizeUrl(currentUri));
        }

        return seeds;
    }

    private static IEnumerable<string> ExtractPossibleUrls(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            yield break;

        if (trimmed.StartsWith("/") || trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            yield return trimmed;

        var parts = trimmed.Split('\'', '"', '(', ')', ';', ',', ' ');
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.StartsWith("/") || token.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                yield return token;
            else if (token.Contains('/') && token.All(ch => char.IsLetterOrDigit(ch) || ch is '/' or '-' or '_' or '?' or '&' or '='))
                yield return "/" + token.TrimStart('/');
        }
    }

    private static IEnumerable<string> GetSafeLinksFromHtml(string html, string baseUrl)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RoutePattern.Matches(html ?? string.Empty))
        {
            if (!match.Success)
                continue;

            var path = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (!Uri.TryCreate(baseUri, path, out var uri))
                continue;

            if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            var normalized = NormalizeUrl(uri);
            if (!ContainsDangerousPattern(normalized))
                links.Add(normalized);
        }

        return links;
    }

    private bool IsAllowedUrl(string url)
    {
        if (ContainsDangerousPattern(url))
            return false;

        if (_automationOptions.SkipUrlPatterns.Any(x => url.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (_automationOptions.AllowUrlPatterns.Count == 0)
            return true;

        return _automationOptions.AllowUrlPatterns.Any(x => url.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsDangerousPattern(string input)
    {
        var patterns = new[]
        {
            "delete", "save", "submit", "approve", "reject", "process", "reset", "confirm",
            "logout", "signout", "export", "download", "print", "report"
        };

        return patterns.Any(x => input.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> WriteSnapshotAsync(string url, string html, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ResolvePath(_automationOptions.SnapshotsDirectory));
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Sanitize(url)}.html";
        var path = Path.Combine(ResolvePath(_automationOptions.SnapshotsDirectory), fileName);
        await File.WriteAllTextAsync(path, html, cancellationToken);
        return path;
    }

    private async Task<string?> TakeScreenshotAsync(IPage page, string url, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(ResolvePath(_automationOptions.SnapshotsDirectory));
            var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Sanitize(url)}.png";
            var path = Path.Combine(ResolvePath(_automationOptions.SnapshotsDirectory), fileName);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            cancellationToken.ThrowIfCancellationRequested();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private string GetTracePath(string email)
    {
        Directory.CreateDirectory(ResolvePath(_automationOptions.SnapshotsDirectory));
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-trace-{Sanitize(email)}.zip";
        return Path.Combine(ResolvePath(_automationOptions.SnapshotsDirectory), fileName);
    }

    private static string NormalizeUrl(Uri uri)
    {
        var left = uri.GetLeftPart(UriPartial.Path);
        if (string.IsNullOrWhiteSpace(uri.Query))
            return left;

        return left + uri.Query;
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((text ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }
}
