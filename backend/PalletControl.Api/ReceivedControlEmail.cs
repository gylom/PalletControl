using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Durable outbox: each shortage registered after EmailNotificationAnchor was initialized
// is discoverable even if preparation fails, the process crashes, or SMTP is unavailable.
public class ReceivedControlEmail
{
    public int Id { get; set; }
    public int EntryId { get; set; }
    public string Recipient { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
}

public class EmailDeliveryEvent
{
    public int Id { get; set; }
    public int? EmailId { get; set; }
    public int? TerminalId { get; set; }
    public string Kind { get; set; } = "NOTIFICATION";
    public string Outcome { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public static class ReceivedControlEmailQueue
{
    public static void EnsureSchema(AppDbContext db)
    {
        // The EF model creates these tables for fresh databases. For existing databases,
        // extend in place without replacing or rewriting any operational records.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "ReceivedControlEmails" (
              "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              "EntryId" INTEGER NOT NULL,
              "Recipient" TEXT NOT NULL DEFAULT '', "Cc" TEXT NOT NULL DEFAULT '',
              "Subject" TEXT NOT NULL DEFAULT '', "Body" TEXT NOT NULL DEFAULT '',
              "NextAttemptUtc" TEXT NOT NULL, "SentAtUtc" TEXT NULL,
              "Attempts" INTEGER NOT NULL DEFAULT 0,
              "LastError" TEXT NOT NULL DEFAULT '',
              "Status" TEXT NOT NULL DEFAULT 'PENDING',
              "CreatedAtUtc" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
              "LastAttemptUtc" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceivedControlEmails_EntryId"
              ON "ReceivedControlEmails" ("EntryId");
            """);
        EmailAdminStore.EnsureSchema(db); // also adds Cc for legacy queued records

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = new SqliteConnection(db.Database.GetDbConnection().ConnectionString))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA table_info('ReceivedControlEmails')";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("Status", StringComparer.OrdinalIgnoreCase))
            db.Database.ExecuteSqlRaw("ALTER TABLE ReceivedControlEmails ADD COLUMN Status TEXT NOT NULL DEFAULT 'PENDING'");
        if (!columns.Contains("CreatedAtUtc", StringComparer.OrdinalIgnoreCase))
            db.Database.ExecuteSqlRaw("ALTER TABLE ReceivedControlEmails ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'");
        if (!columns.Contains("LastAttemptUtc", StringComparer.OrdinalIgnoreCase))
            db.Database.ExecuteSqlRaw("ALTER TABLE ReceivedControlEmails ADD COLUMN LastAttemptUtc TEXT NULL");

        db.Database.ExecuteSqlRaw("""
            UPDATE ReceivedControlEmails SET Status = 'SENT'
             WHERE SentAtUtc IS NOT NULL AND LastError = '' AND Status = 'PENDING';
            UPDATE ReceivedControlEmails SET Status = 'SKIPPED'
             WHERE SentAtUtc IS NOT NULL AND LastError LIKE 'Skipped:%' AND Status = 'PENDING';
            UPDATE ReceivedControlEmails SET Status = 'RETRYING'
             WHERE SentAtUtc IS NULL AND Attempts > 0 AND Status = 'PENDING';
            CREATE TABLE IF NOT EXISTS EmailDeliveryEvents (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              EmailId INTEGER NULL, TerminalId INTEGER NULL,
              Kind TEXT NOT NULL, Outcome TEXT NOT NULL,
              Recipient TEXT NOT NULL DEFAULT '', Cc TEXT NOT NULL DEFAULT '',
              Detail TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_EmailDeliveryEvents_TerminalId_Id
              ON EmailDeliveryEvents (TerminalId, Id);
            CREATE INDEX IF NOT EXISTS IX_EmailDeliveryEvents_EmailId_Id
              ON EmailDeliveryEvents (EmailId, Id);
            CREATE TABLE IF NOT EXISTS EmailNotificationAnchor (
              Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
              ExistingEntryMaxId INTEGER NOT NULL
            );
            """);
        // Anchor once at first installation. Never scan pre-feature historical receipts.
        db.Database.ExecuteSqlRaw("""
            INSERT OR IGNORE INTO EmailNotificationAnchor(Id, ExistingEntryMaxId)
            VALUES (1, (SELECT COALESCE(MAX(Id), 0) FROM ReceivedControlEntries));
            """);
    }

    public static async Task DiscoverAsync(AppDbContext db, CancellationToken token)
    {
        // An INSERT ... SELECT is atomic. A unique EntryId index prevents duplicate
        // jobs when more than one IIS worker is running.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO ReceivedControlEmails
              (EntryId, Recipient, Cc, Subject, Body, NextAttemptUtc, SentAtUtc,
               Attempts, LastError, Status, CreatedAtUtc)
            SELECT r.Id, '', '', '', '', {DateTime.UtcNow}, NULL, 0, '', 'PENDING', {DateTime.UtcNow}
            FROM ReceivedControlEntries r
            WHERE r.Id > (SELECT ExistingEntryMaxId FROM EmailNotificationAnchor WHERE Id = 1)
              AND r.Result = {ReceivedControlStatus.ReceiptHigher}
              AND NOT EXISTS (SELECT 1 FROM ReceivedControlEmails e WHERE e.EntryId = r.Id)
            ORDER BY r.Id LIMIT 100;
            """, token);
    }
}

public sealed class ReceivedControlEmailWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    IDataProtectionProvider protection,
    ILogger<ReceivedControlEmailWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ReceivedControlEmailQueue.DiscoverAsync(db, stoppingToken);
                await SendNext(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Email outbox cycle failed; pending jobs remain for the next cycle."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RecordEvent(AppDbContext db, ReceivedControlEmail email,
        int? terminalId, string outcome, string detail, CancellationToken token)
    {
        // Diagnostic events never change whether an email is sent or how it is retried.
        try
        {
            db.EmailDeliveryEvents.Add(new EmailDeliveryEvent
            {
                EmailId = email.Id, TerminalId = terminalId,
                Outcome = outcome, Detail = detail,
                Recipient = email.Recipient, Cc = email.Cc,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);
        }
        catch (Exception ex)
        {
            foreach (var tracked in db.ChangeTracker.Entries<EmailDeliveryEvent>()
                         .Where(e => e.State == EntityState.Added).ToList())
                tracked.State = EntityState.Detached;
            logger.LogWarning(ex, "Could not persist email event for email {EmailId}.", email.Id);
        }
    }

    private async Task SendNext(AppDbContext db, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var email = await db.ReceivedControlEmails.AsNoTracking()
            .Where(e => e.SentAtUtc == null && e.NextAttemptUtc <= now &&
                (e.Status == "PENDING" || e.Status == "RETRYING" || e.Status == "SENDING"))
            .OrderBy(e => e.Id).FirstOrDefaultAsync(token);
        if (email is null) return;

        // Atomic five-minute lease. A crash after SMTP acceptance but before the DB update
        // may cause redelivery: this is at-least-once, not exactly-once delivery.
        var claimed = await db.ReceivedControlEmails
            .Where(e => e.Id == email.Id && e.SentAtUtc == null && e.NextAttemptUtc <= now &&
                (e.Status == "PENDING" || e.Status == "RETRYING" || e.Status == "SENDING"))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "SENDING")
                .SetProperty(e => e.LastAttemptUtc, now)
                .SetProperty(e => e.NextAttemptUtc, now.AddMinutes(5))
                .SetProperty(e => e.Attempts, e => e.Attempts + 1), token);
        if (claimed == 0) return;
        email.Attempts++;
        int? terminalId = null;
        try
        {
            var entry = await db.ReceivedControlEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == email.EntryId, token);
            terminalId = entry?.TerminalId;
            if (entry is null || entry.Status != ReceiptStatus.Active || entry.Result != ReceivedControlStatus.ReceiptHigher)
            {
                await Skip(db, email, terminalId, "Receipt was cancelled or removed.", token);
                return;
            }
            var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == entry.TerminalId, token);
            var smtp = EmailAdminStore.GetSmtp(db, config);
            var settings = EmailAdminStore.GetTerminal(db, config, entry.TerminalId, terminal.Code);
            if (!settings.Enabled || !smtp.Public.Enabled)
            {
                await Skip(db, email, terminalId, "Email notifications disabled when processed.", token);
                return;
            }
            if (string.IsNullOrWhiteSpace(email.Recipient) || string.IsNullOrWhiteSpace(email.Subject))
            {
                EmailAdminStore.ValidateAddresses(settings.To, allowEmpty: false);
                EmailAdminStore.ValidateAddresses(settings.Cc, allowEmpty: true);
                var user = await db.Users.AsNoTracking().Where(u => u.Id == entry.SubmittedByUserId)
                    .Select(u => u.DisplayName).SingleAsync(token);
                email.Recipient = settings.To;
                email.Cc = settings.Cc;
                email.Subject = EmailAdminStore.Render(settings.Subject, entry, terminal.Code, user);
                email.Body = EmailAdminStore.Render(settings.Body, entry, terminal.Code, user);
                await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Recipient, email.Recipient)
                    .SetProperty(e => e.Cc, email.Cc)
                    .SetProperty(e => e.Subject, email.Subject)
                    .SetProperty(e => e.Body, email.Body), token);
            }
            await EmailAdminStore.SendAsync(smtp.Public, smtp.EncryptedPassword,
                protection.CreateProtector("PalletControl.Email.SmtpSecret.v1"),
                email.Recipient, email.Cc, email.Subject, email.Body, token);
            await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "SENT")
                .SetProperty(e => e.SentAtUtc, DateTime.UtcNow)
                .SetProperty(e => e.LastError, ""), token);
            await RecordEvent(db, email, terminalId, "SENT", "Accepted by SMTP server (mailbox delivery not confirmed).", token);
            logger.LogInformation("Mottattkontroll email {EmailId} accepted by SMTP server.", email.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var final = email.Attempts >= MaxAttempts;
            var retryAt = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(email.Attempts, 6))));
            var detail = EmailAdminStore.SafeError(ex);
            await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, final ? "FAILED" : "RETRYING")
                .SetProperty(e => e.NextAttemptUtc, retryAt)
                .SetProperty(e => e.LastError, detail), token);
            await RecordEvent(db, email, terminalId, final ? "FAILED" : "RETRYING", detail, token);
            logger.LogError(ex, "Mottattkontroll email {EmailId} attempt {Attempt} failed ({State}).",
                email.Id, email.Attempts, final ? "final failure" : "retry scheduled");
        }
    }

    private async Task Skip(AppDbContext db, ReceivedControlEmail email, int? terminalId, string reason, CancellationToken token)
    {
        await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, "SKIPPED")
            .SetProperty(e => e.LastError, reason), token);
        await RecordEvent(db, email, terminalId, "SKIPPED", reason, token);
    }
}
