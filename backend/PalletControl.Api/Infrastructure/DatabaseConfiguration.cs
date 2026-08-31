using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public static class DatabaseConfiguration
{
    public static DatabaseStorageOptions AddPalletDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configuredConnectionString = configuration.GetConnectionString("Default")
                                         ?? "Data Source=palletcontrol-v5.db";
        var sqlite = new SqliteConnectionStringBuilder(configuredConnectionString);
        if (string.IsNullOrWhiteSpace(sqlite.DataSource))
            throw new InvalidOperationException("SQLite Data Source is missing.");

        var databasePath = sqlite.DataSource;
        if (!Path.IsPathRooted(databasePath))
            databasePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, databasePath));

        var configuredBackupDirectory = configuration["Database:BackupDirectory"] ?? "Backups";
        var backupDirectory = Path.IsPathRooted(configuredBackupDirectory)
            ? configuredBackupDirectory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredBackupDirectory));

        if (environment.IsProduction())
        {
            // Production data must live outside the Git checkout/publish directory.
            // Configure both paths with environment variables/IIS settings.
            var originalDataSource = new SqliteConnectionStringBuilder(configuredConnectionString).DataSource;
            if (!Path.IsPathRooted(originalDataSource))
                throw new InvalidOperationException(
                    "Production SQLite path must be absolute. Set ConnectionStrings__Default, e.g. " +
                    "Data Source=C:\\PalletControlData\\palletcontrol.db;Cache=Shared");
            if (!Path.IsPathRooted(configuredBackupDirectory))
                throw new InvalidOperationException(
                    "Production backup directory must be absolute. Set Database__BackupDirectory, e.g. " +
                    "C:\\PalletControlBackups");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? environment.ContentRootPath);
        Directory.CreateDirectory(backupDirectory);
        sqlite.DataSource = databasePath;

        var options = new DatabaseStorageOptions(
            databasePath,
            sqlite.ConnectionString,
            backupDirectory,
            Math.Clamp(configuration.GetValue<int?>("Database:BackupIntervalHours") ?? 24, 1, 168),
            Math.Clamp(configuration.GetValue<int?>("Database:BackupRetentionDays") ?? 30, 1, 3650));

        services.AddSingleton(options);
        services.AddSingleton<DatabaseBackupManager>();
        services.AddHostedService<DatabaseBackupHostedService>();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite(
                options.ConnectionString,
                sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

        return options;
    }
}
