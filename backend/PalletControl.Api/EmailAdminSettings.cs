using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Independent SQLite settings tables; does not change the application's existing EF model.
public sealed record EmailSmtpSettings(bool Enabled, string Host, int Port, bool StartTls,
    string From, string Username, bool HasPassword);
public sealed record EmailSmtpUpdate(bool Enabled, string Host, int Port, bool StartTls,
    string From, string Username, string? Password, bool ClearPassword = false);
public sealed record EmailTerminalSettings(bool Enabled, string To, string Cc, string Subject, string Body);
public sealed record EmailTestRequest(string To, string? Cc);

public static class EmailAdminStore
{
    public const string DefaultSubject = "[PalletControl \u00B7 {Terminal}] Palleavvik \u2013 {Shortage} paller mangler";
    public const string DefaultBody = "Det er registrert et avvik i mottattkontrollen.\n\n" +
        "Kontrollnummer: {ControlNumber}\nTerminal: {Terminal}\nFra terminal: {FromTerminal}\n" +
        "Dato: {Date}\nContainer/tralle: {UnitReference}\nAntall p\u00E5 kvittering: {ReceiptPalletCount}\n" +
        "Faktisk mottatt: {ActualPalletCount}\nMangler: {Shortage}\nRegistrert av: {RegisteredBy}\n" +
        "Kommentar: {Comment}\n\nRegistreringen er tilgjengelig i Mottattkontroll i PalletControl.";
    private static SqliteConnection Open(AppDbContext db)
    {
        var connection = new SqliteConnection(db.Database.GetDbConnection().ConnectionString);
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static void EnsureSchema(AppDbContext db)
    {
        using var connection = Open(db);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS EmailSmtpConfiguration (
                  Id INTEGER NOT NULL PRIMARY KEY CHECK(Id = 1),
                  Enabled INTEGER NOT NULL DEFAULT 0,
                  Host TEXT NOT NULL DEFAULT '', Port INTEGER NOT NULL DEFAULT 587,
                  StartTls INTEGER NOT NULL DEFAULT 1, FromAddress TEXT NOT NULL DEFAULT '',
                  Username TEXT NOT NULL DEFAULT '', EncryptedPassword TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS EmailTerminalConfiguration (
                  TerminalId INTEGER NOT NULL PRIMARY KEY,
                  Enabled INTEGER NOT NULL DEFAULT 0, ToAddresses TEXT NOT NULL DEFAULT '',
                  CcAddresses TEXT NOT NULL DEFAULT '', SubjectTemplate TEXT NOT NULL DEFAULT '',
                  BodyTemplate TEXT NOT NULL DEFAULT ''
                );
                """;
            cmd.ExecuteNonQuery();
        }
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info('ReceivedControlEmails')";
            using var reader = pragma.ExecuteReader();
            var hasCc = false;
            while (reader.Read()) if (reader.GetString(1).Equals("Cc", StringComparison.OrdinalIgnoreCase)) hasCc = true;
            reader.Close();
            if (!hasCc)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE ReceivedControlEmails ADD COLUMN Cc TEXT NOT NULL DEFAULT ''";
                alter.ExecuteNonQuery();
            }
        }
    }

    public static (EmailSmtpSettings Public, string EncryptedPassword) GetSmtp(AppDbContext db, IConfiguration config)
    {
        using var connection = Open(db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Enabled, Host, Port, StartTls, FromAddress, Username, EncryptedPassword FROM EmailSmtpConfiguration WHERE Id = 1";
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var secret = reader.GetString(6);
            return (new EmailSmtpSettings(reader.GetInt32(0) != 0, reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3) != 0, reader.GetString(4), reader.GetString(5), !string.IsNullOrEmpty(secret)), secret);
        }
        return (new EmailSmtpSettings(config.GetValue<bool>("ReceivedControlEmail:Enabled"),
            config["ReceivedControlEmail:Host"] ?? "", config.GetValue<int?>("ReceivedControlEmail:Port") ?? 587,
            true, config["ReceivedControlEmail:From"] ?? "", "", false), "");
    }

    public static EmailTerminalSettings GetTerminal(AppDbContext db, IConfiguration config, int terminalId, string terminalCode)
    {
        using var connection = Open(db);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Enabled, ToAddresses, CcAddresses, SubjectTemplate, BodyTemplate FROM EmailTerminalConfiguration WHERE TerminalId = $id";
        cmd.Parameters.AddWithValue("$id", terminalId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return new EmailTerminalSettings(reader.GetInt32(0) != 0, reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4));
        var legacy = config[$"ReceivedControlEmail:Recipients:{terminalCode.ToUpperInvariant()}"] ?? "";
        return new EmailTerminalSettings(!string.IsNullOrWhiteSpace(legacy), legacy, "", DefaultSubject, DefaultBody);
    }

    public static void SaveSmtp(AppDbContext db, EmailSmtpUpdate data, IDataProtector protector)
    {
        if (data.Port < 1 || data.Port > 65535) throw new ArgumentException("SMTP port must be 1–65535.");
        if (data.Host.Length > 253 || data.Username.Length > 320 || data.From.Length > 320)
            throw new ArgumentException("SMTP field is too long.");
        if (data.Enabled && (string.IsNullOrWhiteSpace(data.Host) || string.IsNullOrWhiteSpace(data.From)))
            throw new ArgumentException("SMTP host and sender are required when enabled.");
        if (!string.IsNullOrWhiteSpace(data.From)) ValidateAddresses(data.From, allowEmpty: false, single: true);
        using var connection = Open(db);
        using var transaction = connection.BeginTransaction();
        string existing = "";
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT EncryptedPassword FROM EmailSmtpConfiguration WHERE Id = 1";
            existing = read.ExecuteScalar()?.ToString() ?? "";
        }
        var secret = data.ClearPassword ? "" : data.Password is null or "" ? existing : protector.Protect(data.Password);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO EmailSmtpConfiguration(Id, Enabled, Host, Port, StartTls, FromAddress, Username, EncryptedPassword)
            VALUES (1, $enabled, $host, $port, $tls, $from, $username, $password)
            ON CONFLICT(Id) DO UPDATE SET Enabled=excluded.Enabled,Host=excluded.Host,Port=excluded.Port,
            StartTls=excluded.StartTls,FromAddress=excluded.FromAddress,Username=excluded.Username,
            EncryptedPassword=excluded.EncryptedPassword
            """;
        cmd.Parameters.AddWithValue("$enabled", data.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$host", data.Host.Trim());
        cmd.Parameters.AddWithValue("$port", data.Port);
        cmd.Parameters.AddWithValue("$tls", data.StartTls ? 1 : 0);
        cmd.Parameters.AddWithValue("$from", data.From.Trim());
        cmd.Parameters.AddWithValue("$username", data.Username.Trim());
        cmd.Parameters.AddWithValue("$password", secret);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public static void SaveTerminal(AppDbContext db, int terminalId, EmailTerminalSettings data)
    {
        ValidateAddresses(data.To, allowEmpty: !data.Enabled);
        ValidateAddresses(data.Cc, allowEmpty: true);
        if (data.Subject.Length > 500 || data.Body.Length > 20000) throw new ArgumentException("Email template exceeds the length limit.");
        if (data.Enabled && (string.IsNullOrWhiteSpace(data.Subject) || string.IsNullOrWhiteSpace(data.Body)))
            throw new ArgumentException("Subject and message are required when enabled.");
        using var connection = Open(db);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO EmailTerminalConfiguration(TerminalId, Enabled, ToAddresses, CcAddresses, SubjectTemplate, BodyTemplate)
            VALUES($id,$enabled,$to,$cc,$subject,$body)
            ON CONFLICT(TerminalId) DO UPDATE SET Enabled=excluded.Enabled,ToAddresses=excluded.ToAddresses,
            CcAddresses=excluded.CcAddresses,SubjectTemplate=excluded.SubjectTemplate,BodyTemplate=excluded.BodyTemplate
            """;
        cmd.Parameters.AddWithValue("$id", terminalId);
        cmd.Parameters.AddWithValue("$enabled", data.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$to", data.To.Trim());
        cmd.Parameters.AddWithValue("$cc", data.Cc.Trim());
        cmd.Parameters.AddWithValue("$subject", data.Subject);
        cmd.Parameters.AddWithValue("$body", data.Body);
        cmd.ExecuteNonQuery();
    }

    public static string SafeError(Exception error)
    {
        // Do not expose credentials or excessively long SMTP server responses in Admin logs.
        var text = error.GetBaseException().Message;
        text = Regex.Replace(text, @"(?i)(password|passwd|token|authorization|secret)\s*[:=]\s*\S+", "$1=[redacted]");
        return text.Length > 500 ? text[..500] : text;
    }

    public static void ValidateAddresses(string addresses, bool allowEmpty, bool single = false)
    {
        var parts = addresses.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 && !allowEmpty) throw new ArgumentException("At least one email address is required.");
        if (parts.Length > 30 || (single && parts.Length != 1)) throw new ArgumentException("Invalid number of email addresses.");
        foreach (var part in parts)
        {
            var addr = new MailAddress(part);
            if (!string.Equals(addr.Address, part, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid email address: {part}");
        }
    }

    public static void AddAddresses(MailAddressCollection destination, string addresses)
    {
        foreach (var address in addresses.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            destination.Add(new MailAddress(address));
    }

    public static string Render(string template, ReceivedControlEntry entry, string terminal, string user)
    {
        var shortage = entry.ReceiptPalletCount.GetValueOrDefault() - entry.ActualPalletCount;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Terminal"] = terminal, ["ControlNumber"] = entry.ControlNumber ?? "",
            ["FromTerminal"] = entry.FromTerminalSnapshot ?? "", ["Date"] = entry.BusinessDate.ToString("dd.MM.yyyy"),
            ["UnitReference"] = entry.UnitReference ?? "", ["ReceiptPalletCount"] = entry.ReceiptPalletCount?.ToString() ?? "",
            ["ActualPalletCount"] = entry.ActualPalletCount.ToString(), ["Shortage"] = shortage.ToString(),
            ["RegisteredBy"] = user, ["Comment"] = entry.Comment ?? ""
        };
        return Regex.Replace(template, @"\{([a-zA-Z]+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    public static async Task SendAsync(EmailSmtpSettings config, string encryptedPassword, IDataProtector protector,
        string to, string cc, string subject, string body, CancellationToken token)
    {
        if (!config.Enabled) throw new InvalidOperationException("SMTP sending is disabled.");
        using var mail = new MailMessage { From = new MailAddress(config.From), Subject = subject,
            Body = body, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        AddAddresses(mail.To, to);
        AddAddresses(mail.CC, cc);
        if (mail.To.Count == 0) throw new InvalidOperationException("Missing recipient.");
        using var smtp = new SmtpClient(config.Host, config.Port)
        {
            EnableSsl = config.StartTls,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrEmpty(config.Username) ? null :
                new NetworkCredential(config.Username, string.IsNullOrEmpty(encryptedPassword) ? "" : protector.Unprotect(encryptedPassword)),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30000
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
    }
}

public static class EmailAdminEndpoints
{
    private static bool Super(ClaimsPrincipal user) => user.IsInRole("SuperAdmin");
    private static bool Admin(ClaimsPrincipal user) => Super(user) || user.IsInRole("Admin") || user.IsInRole("TerminalAdmin");
    private static async Task<bool> Allowed(AppDbContext db, ClaimsPrincipal user, int terminalId)
    {
        if (!Admin(user)) return false;
        if (Super(user)) return await db.Terminals.AnyAsync(t => t.Id == terminalId && t.Active && t.IsOperatingTerminal);
        var username = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return false;
        return await db.Users.AnyAsync(u => u.Username == username && u.Active && u.TerminalId == terminalId &&
            (u.Role == "Admin" || u.Role == "TerminalAdmin"));
    }

    public static void MapEmailAdminEndpoints(this WebApplication app)
    {
        // Delivery log is limited to the requested operating terminal. A normal Admin
        // cannot view notifications belonging to other terminals.
        app.MapGet("/api/admin/email-log", async (int terminalId, ClaimsPrincipal user, AppDbContext db) =>
        {
            if (!await Allowed(db, user, terminalId)) return Results.Forbid();
            var messages = await db.ReceivedControlEmails.AsNoTracking()
                .Join(db.ReceivedControlEntries.AsNoTracking(),
                    email => email.EntryId, entry => entry.Id,
                    (email, entry) => new { email, entry })
                .Where(x => x.entry.TerminalId == terminalId)
                .OrderByDescending(x => x.email.Id).Take(150)
                .Select(x => new
                {
                    x.email.Id, x.email.EntryId, x.entry.ControlNumber,
                    x.email.Status, x.email.Attempts, x.email.Recipient, x.email.Cc,
                    x.email.Subject, x.email.CreatedAtUtc, x.email.LastAttemptUtc,
                    x.email.NextAttemptUtc, x.email.SentAtUtc, x.email.LastError
                }).ToListAsync();
            var isSuperAdmin = Super(user);
            var recent = await db.EmailDeliveryEvents.AsNoTracking()
                .Where(x => x.TerminalId == terminalId || (isSuperAdmin && x.TerminalId == null && x.Kind == "TEST"))
                .OrderByDescending(x => x.Id).Take(200)
                .Select(x => new
                {
                    x.Id, x.EmailId, x.Kind, x.Outcome, x.Recipient, x.Cc,
                    x.Detail, x.CreatedAtUtc
                }).ToListAsync();
            return Results.Ok(new { messages, recent });
        }).RequireAuthorization();

        app.MapPost("/api/admin/email-log/{id:int}/retry", async (
            int id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var email = await db.ReceivedControlEmails.FirstOrDefaultAsync(x => x.Id == id);
            if (email is null) return Results.NotFound();
            var entry = await db.ReceivedControlEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == email.EntryId);
            if (entry is null) return Results.NotFound();
            if (!await Allowed(db, user, entry.TerminalId)) return Results.Forbid();
            if (email.Status != "FAILED" && email.Status != "RETRYING")
                return Results.BadRequest(new { message = "Only failed or retry-scheduled emails can be retried." });
            // Do not retry an email if the source receipt was cancelled.
            if (entry.Status != ReceiptStatus.Active || entry.Result != ReceivedControlStatus.ReceiptHigher)
                return Results.BadRequest(new { message = "The source receipt is no longer active." });
            email.Status = "PENDING";
            // Regenerate message and recipients using the currently saved terminal settings.
            email.Recipient = "";
            email.Cc = "";
            email.Subject = "";
            email.Body = "";
            email.Attempts = 0;
            email.NextAttemptUtc = DateTime.UtcNow;
            email.LastError = "";
            db.EmailDeliveryEvents.Add(new EmailDeliveryEvent
            {
                EmailId = email.Id, TerminalId = entry.TerminalId,
                Kind = "NOTIFICATION", Outcome = "MANUAL_RETRY",
                Recipient = email.Recipient, Cc = email.Cc,
                Detail = "Requeued manually by an authorized administrator.",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "Email queued for another attempt." });
        }).RequireAuthorization();

        app.MapGet("/api/admin/email-settings", async (int terminalId, ClaimsPrincipal user, AppDbContext db, IConfiguration cfg) =>
        {
            if (!await Allowed(db, user, terminalId)) return Results.Forbid();
            var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == terminalId && t.Active && t.IsOperatingTerminal);
            if (terminal is null) return Results.NotFound();
            return Results.Ok(new { terminalId, terminalCode = terminal.Code,
                settings = EmailAdminStore.GetTerminal(db, cfg, terminalId, terminal.Code) });
        }).RequireAuthorization();

        app.MapPut("/api/admin/email-settings", async (int terminalId, EmailTerminalSettings input, ClaimsPrincipal user,
            AppDbContext db) =>
        {
            if (!await Allowed(db, user, terminalId)) return Results.Forbid();
            if (!await db.Terminals.AnyAsync(t => t.Id == terminalId && t.Active && t.IsOperatingTerminal)) return Results.NotFound();
            try { EmailAdminStore.SaveTerminal(db, terminalId, input); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.BadRequest(new { message = ex.Message }); }
            return Results.Ok(new { message = "Terminal email settings saved." });
        }).RequireAuthorization();

        app.MapGet("/api/admin/smtp-settings", (ClaimsPrincipal user, AppDbContext db, IConfiguration config) =>
        {
            if (!Super(user)) return Results.Forbid();
            return Results.Ok(EmailAdminStore.GetSmtp(db, config).Public);
        }).RequireAuthorization();

        app.MapPut("/api/admin/smtp-settings", (EmailSmtpUpdate input, ClaimsPrincipal user, AppDbContext db,
            IDataProtectionProvider keys) =>
        {
            if (!Super(user)) return Results.Forbid();
            try { EmailAdminStore.SaveSmtp(db, input, keys.CreateProtector("PalletControl.Email.SmtpSecret.v1")); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.BadRequest(new { message = ex.Message }); }
            return Results.Ok(EmailAdminStore.GetSmtp(db, new ConfigurationBuilder().Build()).Public);
        }).RequireAuthorization();

        app.MapPost("/api/admin/smtp-test", async (EmailTestRequest input, ClaimsPrincipal user, AppDbContext db,
            IConfiguration config, IDataProtectionProvider keys, CancellationToken token) =>
        {
            if (!Super(user)) return Results.Forbid();
            string outcome = "FAILED";
            string detail = "";
            try
            {
                EmailAdminStore.ValidateAddresses(input.To, allowEmpty: false);
                EmailAdminStore.ValidateAddresses(input.Cc ?? "", allowEmpty: true);
                var smtp = EmailAdminStore.GetSmtp(db, config);
                await EmailAdminStore.SendAsync(smtp.Public, smtp.EncryptedPassword,
                    keys.CreateProtector("PalletControl.Email.SmtpSecret.v1"), input.To, input.Cc ?? "",
                    "PalletControl SMTP test", "This is a test message from PalletControl.", token);
                outcome = "SENT";
                detail = "Accepted by SMTP server (mailbox delivery not confirmed).";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { detail = EmailAdminStore.SafeError(ex); }
            try
            {
                db.EmailDeliveryEvents.Add(new EmailDeliveryEvent
                {
                    Kind = "TEST", Outcome = outcome, Detail = detail,
                    Recipient = input.To, Cc = input.Cc ?? "", CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync(token);
            }
            catch (Exception) { /* Delivery result must not be overwritten by a log-write failure. */ }
            return outcome == "SENT"
                ? Results.Ok(new { message = "SMTP server accepted the test email (not a delivery confirmation)." })
                : Results.BadRequest(new { message = $"SMTP test failed: {detail}" });
        }).RequireAuthorization();
    }
}
