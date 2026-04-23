using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saga_MiniConsoleTranslate.Configuration;

namespace Saga_MiniConsoleTranslate.Services;

public class SqliteMirrorResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

public class SqliteMirrorService(
    IOptions<TranslationAutomationOptions> _optionsAccessor,
    ILogger<SqliteMirrorService> _logger
)
{
    private readonly TranslationAutomationOptions _options = _optionsAccessor.Value;

    public async Task<SqliteMirrorResult> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var sourcePath = PathResolver.ResolveForRead(_options.SourceSqlitePath);
        var workingPath = PathResolver.ResolveForWrite(_options.WorkingSqlitePath);
        var mode = string.IsNullOrWhiteSpace(_options.SqliteMode) ? "IsolatedCopy" : _options.SqliteMode.Trim();

        Directory.CreateDirectory(Path.GetDirectoryName(workingPath)!);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source SQLite file was not found: {sourcePath}", sourcePath);

        if (mode.Equals("SharedFile", StringComparison.OrdinalIgnoreCase))
        {
            workingPath = sourcePath;
        }
        else
        {
            File.Copy(sourcePath, workingPath, true);
            CopyIfExists(sourcePath + "-wal", workingPath + "-wal");
            CopyIfExists(sourcePath + "-shm", workingPath + "-shm");
        }

        await ApplyPragmasAsync(workingPath, cancellationToken);

        _logger.LogInformation("SQLite source path: {SourcePath}", sourcePath);
        _logger.LogInformation("SQLite working path: {WorkingPath}", workingPath);

        return new SqliteMirrorResult
        {
            SourcePath = sourcePath,
            WorkingPath = workingPath,
            Mode = mode
        };
    }

    public Task CopyBackAsync(SqliteMirrorResult mirrorResult, CancellationToken cancellationToken = default)
    {
        if (!_options.CopyBackToSourceSqlite)
            return Task.CompletedTask;

        if (mirrorResult.Mode.Equals("SharedFile", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        File.Copy(mirrorResult.WorkingPath, mirrorResult.SourcePath, true);
        CopyIfExists(mirrorResult.WorkingPath + "-wal", mirrorResult.SourcePath + "-wal");
        CopyIfExists(mirrorResult.WorkingPath + "-shm", mirrorResult.SourcePath + "-shm");

        _logger.LogInformation("SQLite working copy has been copied back to source file.");
        return Task.CompletedTask;
    }

    private static void CopyIfExists(string source, string target)
    {
        if (File.Exists(source))
            File.Copy(source, target, true);
    }

    private static async Task ApplyPragmasAsync(string sqlitePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 60
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=60000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
