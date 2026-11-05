using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;

public class ReceivedControlEmail
{
    public int Id { get; set; }
    public int EntryId { get; set; }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
}

public static class ReceivedControlEmailQueue
{
    public static void EnsureSchema(AppDbContext db) => db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "ReceivedControlEmails" (
          "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
          "EntryId" INTEGER NOT NULL,
          "Recipient" TEXT NOT NULL, "Subject" TEXT NOT NULL, "Body" TEXT NOT NULL,
          "NextAttemptUtc" TEXT NOT NULL, "SentAtUtc" TEXT NULL,
          "Attempts" INTEGER NOT NULL DEFAULT 0, "LastError" TEXT NOT NULL DEFAULT ''
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceivedControlEmails_EntryId"
          ON "ReceivedControlEmails" ("EntryId");
        """);

    // Called only for newly registered red discrepancies, never historical imports.
    public static async Task Enqueue(AppDbContext db, IConfiguration config, ReceivedControlEntry entry)
    {
        if (!config.GetValue<bool>("ReceivedControlEmail:Enabled") ||
            entry.Result != ReceivedControlStatus.ReceiptHigher) return;
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.Id == entry.TerminalId);
        var recipient = config[$"ReceivedControlEmail:Recipients:{terminal.Code.ToUpperInvariant()}"];
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException($"Missing email recipient for {terminal.Code}.");
        _ = new MailAddress(recipient);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == entry.SubmittedByUserId)
            .Select(u => u.DisplayName).SingleAsync();
        var shortage = entry.ReceiptPalletCount!.Value - entry.ActualPalletCount;
        db.ReceivedControlEmails.Add(new ReceivedControlEmail
        {
            EntryId = entry.Id, Recipient = recipient,
            Subject = $"[PalletControl · {terminal.Code}] Palleavvik – {shortage} paller mangler",
            Body = $"Det er registrert et avvik i mottattkontrollen.\n\n" +
                $"Kontrollnummer: {entry.ControlNumber}\nTerminal: {terminal.Code}\n" +
                $"Fra terminal: {entry.FromTerminalSnapshot}\nDato: {entry.BusinessDate:dd.MM.yyyy}\n" +
                $"Container/tralle: {entry.UnitReference}\nAntall på kvittering: {entry.ReceiptPalletCount}\n" +
                $"Faktisk mottatt: {entry.ActualPalletCount}\nMangler: {shortage}\n" +
                $"Registrert av: {user}\nKommentar: {entry.Comment}\n\n" +
                "Åpne Mottattkontroll i PalletControl: http://paller.transportservice.no\n",
            NextAttemptUtc = DateTime.UtcNow
        });
    }
}

public sealed class ReceivedControlEmailWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    ILogger<ReceivedControlEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue<bool>("ReceivedControlEmail:Enabled")) await SendNext(stoppingToken);
            }
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
        var now = DateTime.UtcNow;
        var email = await db.ReceivedControlEmails.AsNoTracking()
            .Where(e => e.SentAtUtc == null && e.NextAttemptUtc <= now)
            .OrderBy(e => e.Id).FirstOrDefaultAsync(token);
        if (email == null) return;
        // Atomic lease protects against overlapping IIS workers. A crash releases it after 5 minutes.
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
            using var message = new MailMessage(
                config["ReceivedControlEmail:From"]!, email.Recipient, email.Subject, email.Body);
            message.SubjectEncoding = Encoding.UTF8;
            message.BodyEncoding = Encoding.UTF8;
            using var smtp = new SmtpClient(config["ReceivedControlEmail:Host"],
                config.GetValue<int>("ReceivedControlEmail:Port", 25))
            { EnableSsl = true, UseDefaultCredentials = false, Credentials = null };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await smtp.SendMailAsync(message, timeout.Token);
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
