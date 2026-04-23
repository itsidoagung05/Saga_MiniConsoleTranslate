using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saga_MiniConsoleTranslate.Configuration;

namespace Saga_MiniConsoleTranslate.Services;

public class SagaMainApplicationHandle : IAsyncDisposable
{
    private readonly MainApplicationRunnerOptions _options;
    public Process? Process { get; }
    public string BaseUrl { get; }

    public SagaMainApplicationHandle(Process? _process, string _baseUrl, MainApplicationRunnerOptions _optionsValue)
    {
        Process = _process;
        BaseUrl = _baseUrl;
        _options = _optionsValue;
    }

    public async ValueTask DisposeAsync()
    {
        if (Process == null || Process.HasExited)
            return;

        try
        {
            Process.CloseMainWindow();
            if (!Process.WaitForExit(_options.ShutdownTimeoutSeconds * 1000))
                Process.Kill(true);
        }
        catch
        {
            if (!Process.HasExited)
                Process.Kill(true);
        }

        await Task.CompletedTask;
    }
}

public class SagaMainApplicationLauncher(
    IOptions<MainApplicationRunnerOptions> _optionsAccessor,
    ILogger<SagaMainApplicationLauncher> _logger
)
{
    private readonly MainApplicationRunnerOptions _options = _optionsAccessor.Value;

    public async Task<SagaMainApplicationHandle> LaunchOrAttachAsync(
        string sqliteConnectionString,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');

        if (_options.UseExistingRunningApp)
        {
            await WaitUntilHealthyAsync(null, baseUrl, cancellationToken);
            return new SagaMainApplicationHandle(null, baseUrl, _options);
        }

        baseUrl = NormalizeLoopbackBaseUrl(EnsureAvailableBaseUrl(baseUrl));

        var workingDirectory = PathResolver.ResolveForRead(_options.WorkingDirectory);
        var projectPath = PathResolver.ResolveForRead(_options.ProjectPath);
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Main application project file was not found: {projectPath}", projectPath);

        if (!Directory.Exists(workingDirectory) ||
            !Path.GetFullPath(workingDirectory).StartsWith(Path.GetDirectoryName(projectPath)!, StringComparison.OrdinalIgnoreCase))
        {
            workingDirectory = Path.GetDirectoryName(projectPath)!;
        }

        var healthUrl = BuildHealthUrl(baseUrl);
        _logger.LogInformation("Main app working directory: {WorkingDirectory}", workingDirectory);
        _logger.LogInformation("Main app project path: {ProjectPath}", projectPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseUrl);
        startInfo.EnvironmentVariables["ConnectionStrings__LocalStorage"] = sqliteConnectionString;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Saga.MainApplication process.");

        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
                return;

            if (args.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                args.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("MainApp: {Line}", args.Data);
                return;
            }

            _logger.LogDebug("MainApp: {Line}", args.Data);
        };
        process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) _logger.LogWarning("MainApp: {Line}", args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Started Saga.MainApplication process. PID: {Pid}", process.Id);

        await WaitUntilHealthyAsync(process, baseUrl, cancellationToken);
        _logger.LogInformation("Saga.MainApplication is ready. Health url: {HealthUrl}", healthUrl);

        return new SagaMainApplicationHandle(process, baseUrl, _options);
    }

    private async Task WaitUntilHealthyAsync(Process? process, string baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var healthUrls = BuildHealthUrls(baseUrl).ToArray();
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.StartTimeoutSeconds));
        var until = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process is { HasExited: true })
                throw new InvalidOperationException($"Saga.MainApplication exited before healthy check passed. ExitCode: {process.ExitCode}.");

            try
            {
                foreach (var healthUrl in healthUrls)
                {
                    using var response = await client.GetAsync(healthUrl, cancellationToken);
                    if ((int)response.StatusCode is >= 200 and < 500)
                        return;
                }
            }
            catch
            {
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"Saga.MainApplication was not healthy within {_options.StartTimeoutSeconds} seconds. URL candidates: {string.Join(", ", healthUrls)}");
    }

    private string BuildHealthUrl(string baseUrl)
    {
        var healthPath = string.IsNullOrWhiteSpace(_options.HealthUrl) ? "/Authorization/Login" : _options.HealthUrl.Trim();
        if (!healthPath.StartsWith('/'))
            healthPath = "/" + healthPath;

        return baseUrl + healthPath;
    }

    private IEnumerable<string> BuildHealthUrls(string baseUrl)
    {
        var healthPath = string.IsNullOrWhiteSpace(_options.HealthUrl) ? "/Authorization/Login" : _options.HealthUrl.Trim();
        if (!healthPath.StartsWith('/'))
            healthPath = "/" + healthPath;

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            baseUrl + healthPath
        };

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            var localhost = new UriBuilder(uri) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);
            urls.Add(localhost + healthPath);
        }

        return urls;
    }

    private static string NormalizeLoopbackBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return baseUrl;

        if (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        var builder = new UriBuilder(uri) { Host = "127.0.0.1" };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private string EnsureAvailableBaseUrl(string configuredBaseUrl)
    {
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var uri))
            return configuredBaseUrl;

        var host = uri.Host;
        if (!IsLoopbackHost(host))
            return configuredBaseUrl;

        if (!IsPortInUse(host, uri.Port))
            return configuredBaseUrl;

        var nextPort = FindAvailablePort(host, uri.Port + 1);
        var newUriBuilder = new UriBuilder(uri) { Port = nextPort };
        var updatedBaseUrl = newUriBuilder.Uri.GetLeftPart(UriPartial.Authority);

        _logger.LogWarning("Configured base URL {BaseUrl} is in use. Switching to {UpdatedBaseUrl}.", configuredBaseUrl, updatedBaseUrl);
        return updatedBaseUrl;
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortInUse(string host, int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Parse(MapHost(host)), port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static int FindAvailablePort(string host, int startPort)
    {
        var ip = IPAddress.Parse(MapHost(host));
        for (var port = startPort; port <= 65535; port++)
        {
            try
            {
                using var listener = new TcpListener(ip, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException($"Unable to find available TCP port for host {host} starting from {startPort}.");
    }

    private static string MapHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return "127.0.0.1";

        if (host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return "::1";

        return host;
    }
}
