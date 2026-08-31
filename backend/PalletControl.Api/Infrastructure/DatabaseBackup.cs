using System.Globalization;
using Microsoft.Data.Sqlite;


public sealed record DatabaseStorageOptions(
    string DatabasePath,
    string ConnectionString,
    string BackupDirectory,
    int BackupIntervalHours,
    int BackupRetentionDays);

public sealed record DatabaseBackupInfo(string Path, DateTime CreatedAtUtc, long SizeBytes);
public sealed record DatabaseBackupStatus(int BackupCount, string? LatestBackupPath, DateTime? LatestBackupUtc);

public sealed class DatabaseBackupManager
{
    private readonly DatabaseStorageOptions _options;
    private readonly ILogger<DatabaseBackupManager> _logger;
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public DatabaseBackupManager(
        DatabaseStorageOptions options,
        ILogger<DatabaseBackupManager> logger)
    {
        _options = options;
        _logger = logger;
    }

    public DatabaseBackupStatus GetStatus()
    {
        Directory.CreateDirectory(_options.BackupDirectory);

        var backups = new DirectoryInfo(_options.BackupDirectory)
            .GetFiles("PalletControlBackup_*.db")
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .ToList();

        var latest = backups.FirstOrDefault();
        return new DatabaseBackupStatus(
            backups.Count,
            latest?.FullName,
            latest?.LastWriteTimeUtc);
    }

    public async Task<DatabaseBackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await _backupLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_options.BackupDirectory);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(_options.BackupDirectory, $"PalletControlBackup_{stamp}.db");

            await using var source = new SqliteConnection(_options.ConnectionString);
            await source.OpenAsync(cancellationToken);

            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
            await destination.OpenAsync(cancellationToken);

            // SQLite's native online backup API gives a consistent snapshot even while
            // the web app is being used.
            source.BackupDatabase(destination);

            var cutoff = DateTime.UtcNow.AddDays(-_options.BackupRetentionDays);
            foreach (var oldBackup in new DirectoryInfo(_options.BackupDirectory)
                         .GetFiles("PalletControlBackup_*.db")
                         .Where(x => x.LastWriteTimeUtc < cutoff))
            {
                try
                {
                    oldBackup.Delete();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete old database backup {Backup}", oldBackup.FullName);
                }
            }

            var file = new FileInfo(backupPath);
            _logger.LogInformation("SQLite backup created: {BackupPath}", backupPath);
            return new DatabaseBackupInfo(file.FullName, file.LastWriteTimeUtc, file.Length);
        }
        finally
        {
            _backupLock.Release();
        }
    }
}

public sealed class DatabaseBackupHostedService : BackgroundService
{
    private readonly DatabaseBackupManager _backupManager;
    private readonly DatabaseStorageOptions _options;
    private readonly ILogger<DatabaseBackupHostedService> _logger;

    public DatabaseBackupHostedService(
        DatabaseBackupManager backupManager,
        DatabaseStorageOptions options,
        ILogger<DatabaseBackupHostedService> logger)
    {
        _backupManager = backupManager;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = _backupManager.GetStatus();
                var due = status.LatestBackupUtc is null ||
                          DateTime.UtcNow - status.LatestBackupUtc.Value >= TimeSpan.FromHours(_options.BackupIntervalHours);

                if (due)
                    await _backupManager.CreateBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic SQLite backup failed.");
            }

            try
            {
                // Check hourly whether a backup is due. Actual backup cadence is controlled
                // by BackupIntervalHours.
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

// ---------------- TYPES ----------------

