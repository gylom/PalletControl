using System.Globalization;
using Microsoft.EntityFrameworkCore;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/system/overview", async (
            AppDbContext db,
            DatabaseStorageOptions storage,
            DatabaseBackupManager backupManager,
            SystemTelemetryService telemetry,
            SecurityRuntimeOptions security,
            ObservabilityRuntimeOptions observability,
            IHostEnvironment environment) =>
        {
            var dbOk = await db.Database.CanConnectAsync();
            var quickCheck = "unavailable";
            if (dbOk)
            {
                await db.Database.OpenConnectionAsync();
                try
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "PRAGMA quick_check;";
                    quickCheck = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "unknown";
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }

            var dbFile = new FileInfo(storage.DatabasePath);
            var backup = backupManager.GetStatus();
            var driveRoot = Path.GetPathRoot(storage.DatabasePath) ?? storage.DatabasePath;
            long? diskFree = null;
            long? diskTotal = null;
            try
            {
                var drive = new DriveInfo(driveRoot);
                diskFree = drive.AvailableFreeSpace;
                diskTotal = drive.TotalSize;
            }
            catch
            {
                // Some container/network paths do not expose DriveInfo. Leave null rather than fail monitoring.
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            var activeUsers = await db.Users.AsNoTracking().CountAsync(x => x.Active);
            var receiptsToday = await db.Receipts.AsNoTracking().CountAsync(x => x.BusinessDate == today);
            var linehaulToday = await db.LinehaulReceipts.AsNoTracking().CountAsync(x => x.BusinessDate == today);
            var receivedToday = await db.ReceivedControlEntries.AsNoTracking().CountAsync(x => x.BusinessDate == today);

            return Results.Ok(new
            {
                version = observability.ServiceVersion,
                environment = environment.EnvironmentName,
                serverTimeUtc = DateTime.UtcNow,
                database = new
                {
                    path = storage.DatabasePath,
                    exists = dbFile.Exists,
                    sizeBytes = dbFile.Exists ? dbFile.Length : 0,
                    canConnect = dbOk,
                    quickCheck
                },
                backup = new
                {
                    directory = storage.BackupDirectory,
                    intervalHours = storage.BackupIntervalHours,
                    retentionDays = storage.BackupRetentionDays,
                    count = backup.BackupCount,
                    latestBackupUtc = backup.LatestBackupUtc
                },
                disk = new { freeBytes = diskFree, totalBytes = diskTotal },
                security = new
                {
                    requireHttps = security.RequireHttps,
                    jwtLifetimeMinutes = security.JwtLifetimeMinutes,
                    maxRequestBodyMb = security.MaxRequestBodyMb,
                    apiRequestsPerMinute = security.ApiRequestsPerMinute,
                    loginRequestsPerMinute = security.LoginRequestsPerMinute,
                    loginFailureLimit = security.LoginFailureLimit,
                    loginLockoutMinutes = security.LoginLockoutMinutes,
                    allowedOrigins = security.AllowedOrigins
                },
                openTelemetry = new
                {
                    enabled = observability.OtlpEnabled,
                    endpoint = observability.OtlpEndpoint,
                    serviceName = observability.ServiceName,
                    serviceVersion = observability.ServiceVersion
                },
                activity = new { activeUsers, receiptsToday, linehaulToday, receivedToday },
                telemetry = telemetry.Snapshot()
            });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = Roles.SuperAdmin });

        app.MapPost("/api/admin/system/backup", async (DatabaseBackupManager backupManager) =>
        {
            var result = await backupManager.CreateBackupAsync();
            return Results.Ok(new
            {
                message = "Backup created.",
                createdAtUtc = result.CreatedAtUtc,
                sizeBytes = result.SizeBytes,
                fileName = Path.GetFileName(result.Path)
            });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = Roles.SuperAdmin });
    }
}
