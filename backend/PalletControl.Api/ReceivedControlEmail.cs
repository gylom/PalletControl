using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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
}

public static class ReceivedControlEmailQueue
{
    public static void EnsureSchema(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "ReceivedControlEmails" (
              "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              "EntryId" INTEGER NOT NULL,
              "Recipient" TEXT NOT NULL, "Cc" TEXT NOT NULL DEFAULT '',
              "Subject" TEXT NOT NULL, "Body" TEXT NOT NULL,
              "NextAttemptUtc" TEXT NOT NULL, "SentAtUtc" TEXT NULL,
              "Attempts" INTEGER NOT NULL DEFAULT 0, "LastError" TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceivedControlEmails_EntryId"
              ON "ReceivedControlEmails" ("EntryId");
            """);
        EmailAdminStore.EnsureSchema(db);
    }

    // Called only for newly registered shortages; never historical imports.
    public static async Task Enqueue(AppDbContext db, IConfiguration config, ReceivedControlEntry entry)
    {
        if (entry.Result != ReceivedControlStatus.ReceiptHigher) return;
        var smtp = EmailAdminStore.GetSmtp(db, config).Public;
        if (!smtp.Enabled) return;
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.Id == entry.TerminalId);
        var settings = EmailAdminStore.GetTerminal(db, config, entry.TerminalId, terminal.Code);
        if (!settings.Enabled) return;
        EmailAdminStore.ValidateAddresses(settings.To, allowEmpty: false);
        EmailAdminStore.ValidateAddresses(settings.Cc, allowEmpty: true);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == entry.SubmittedByUserId)
            .Select(u => u.DisplayName).SingleAsync();
        db.ReceivedControlEmails.Add(new ReceivedControlEmail
        {
            EntryId = entry.Id,
            Recipient = settings.To,
            Cc = settings.Cc,
            Subject = EmailAdminStore.Render(settings.Subject, entry, terminal.Code, user),
            Body = EmailAdminStore.Render(settings.Body, entry, terminal.Code, user),
            NextAttemptUtc = DateTime.UtcNow
        });
    }
}

public sealed class ReceivedControlEmailWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    IDataProtectionProvider protection,
    ILogger<ReceivedControlEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendNext(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Mottattkontroll email queue failed; will retry."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SendNext(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var smtpSettings = EmailAdminStore.GetSmtp(db, config);
        if (!smtpSettings.Public.Enabled) return;
        var now = DateTime.UtcNow;
        var email = await db.ReceivedControlEmails.AsNoTracking()
            .Where(e => e.SentAtUtc == null && e.NextAttemptUtc <= now)
            .OrderBy(e => e.Id).FirstOrDefaultAsync(token);
        if (email == null) return;
        // Lease protects against overlapping IIS workers. A crash releases it after five minutes.
        var claimed = await db.ReceivedControlEmails
            .Where(e => e.Id == email.Id && e.SentAtUtc == null && e.NextAttemptUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptUtc, now.AddMinutes(5))
                .SetProperty(e => e.Attempts, e => e.Attempts + 1), token);
        if (claimed == 0) return;
        var active = await db.ReceivedControlEntries.AsNoTracking().AnyAsync(e =>
            e.Id == email.EntryId && e.Status == ReceiptStatus.Active &&
            e.Result == ReceivedControlStatus.ReceiptHigher, token);
        if (!active)
        {
            await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.SentAtUtc, DateTime.UtcNow)
                 .SetProperty(e => e.LastError, "Skipped: entry cancelled or deleted."), token);
            return;
        }
        try
        {
            await EmailAdminStore.SendAsync(smtpSettings.Public, smtpSettings.EncryptedPassword,
                protection.CreateProtector("PalletControl.Email.SmtpSecret.v1"),
                email.Recipient, email.Cc, email.Subject, email.Body, token);
            await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.SentAtUtc, DateTime.UtcNow)
                 .SetProperty(e => e.LastError, ""), token);
            logger.LogInformation("Mottattkontroll email {EmailId} accepted by SMTP relay.", email.Id);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var retryAt = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(email.Attempts, 6))));
            await db.ReceivedControlEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.NextAttemptUtc, retryAt)
                 .SetProperty(e => e.LastError, ex.Message), token);
            logger.LogError(ex, "Mottattkontroll email {EmailId} failed; retry scheduled.", email.Id);
        }
    }
}
