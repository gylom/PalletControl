using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ClosedXML.Excel;

var builder = WebApplication.CreateBuilder(args);

// Resolve the SQLite file to an absolute path based on the API project/content root.
// This makes the database location deterministic even when the app is started by Rider,
// PowerShell, IIS or a Windows service from a different working directory.
var configuredConnectionString = builder.Configuration.GetConnectionString("Default")
                                 ?? "Data Source=palletcontrol-v5.db";
var sqliteConnectionBuilder = new SqliteConnectionStringBuilder(configuredConnectionString);

if (string.IsNullOrWhiteSpace(sqliteConnectionBuilder.DataSource))
    throw new InvalidOperationException("SQLite Data Source is missing.");

var databasePath = sqliteConnectionBuilder.DataSource;
if (!Path.IsPathRooted(databasePath))
    databasePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, databasePath));

Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? builder.Environment.ContentRootPath);
sqliteConnectionBuilder.DataSource = databasePath;

var configuredBackupDirectory = builder.Configuration["Database:BackupDirectory"] ?? "Backups";
var backupDirectory = Path.IsPathRooted(configuredBackupDirectory)
    ? configuredBackupDirectory
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredBackupDirectory));

Directory.CreateDirectory(backupDirectory);

var databaseStorage = new DatabaseStorageOptions(
    databasePath,
    sqliteConnectionBuilder.ConnectionString,
    backupDirectory,
    Math.Clamp(builder.Configuration.GetValue<int?>("Database:BackupIntervalHours") ?? 24, 1, 168),
    Math.Clamp(builder.Configuration.GetValue<int?>("Database:BackupRetentionDays") ?? 30, 1, 3650));

builder.Services.AddSingleton(databaseStorage);
builder.Services.AddSingleton<DatabaseBackupManager>();
builder.Services.AddHostedService<DatabaseBackupHostedService>();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(
        databaseStorage.ConnectionString,
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

builder.Services.AddCors(o => o.AddPolicy("ui", p =>
    p.SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            var host = uri.Host;
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   || host == "127.0.0.1"
                   || host.StartsWith("192.168.", StringComparison.Ordinal)
                   || host.StartsWith("10.", StringComparison.Ordinal);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()));

var jwtKey = builder.Configuration["Jwt:Key"]
             ?? throw new InvalidOperationException("Jwt:Key is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "PalletControl";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "PalletControl";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // The token proves who the user is, but role/terminal are refreshed from
        // the database on every authenticated request. If an Admin changes a user's
        // terminal (for example ARE -> SRD), the next request immediately uses SRD
        // without requiring that user to log out and back in.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var userIdText = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                                 ?? principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

                if (!int.TryParse(userIdText, out var userId))
                {
                    context.Fail("Invalid user id.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var currentUser = await db.Users
                    .AsNoTracking()
                    .Include(x => x.Terminal)
                    .SingleOrDefaultAsync(x => x.Id == userId && x.Active);

                if (currentUser is null)
                {
                    context.Fail("User is inactive or no longer exists.");
                    return;
                }

                if (principal?.Identity is not ClaimsIdentity identity)
                {
                    context.Fail("Invalid identity.");
                    return;
                }

                foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
                    identity.RemoveClaim(claim);
                foreach (var claim in identity.FindAll("terminalId").ToList())
                    identity.RemoveClaim(claim);
                foreach (var claim in identity.FindAll("terminalCode").ToList())
                    identity.RemoveClaim(claim);
                foreach (var claim in identity.FindAll("moduleInternal").ToList())
                    identity.RemoveClaim(claim);
                foreach (var claim in identity.FindAll("moduleLinehaul").ToList())
                    identity.RemoveClaim(claim);
                foreach (var claim in identity.FindAll("moduleReceivedControl").ToList())
                    identity.RemoveClaim(claim);

                identity.AddClaim(new Claim(ClaimTypes.Role, currentUser.Role));
                identity.AddClaim(new Claim("terminalId", currentUser.TerminalId.ToString(CultureInfo.InvariantCulture)));
                identity.AddClaim(new Claim("terminalCode", currentUser.Terminal?.Code ?? ""));
                identity.AddClaim(new Claim("moduleInternal", currentUser.HasInternalPalletAccounting ? "1" : "0"));
                identity.AddClaim(new Claim("moduleLinehaul", currentUser.HasLinehaul ? "1" : "0"));
                identity.AddClaim(new Claim("moduleReceivedControl", currentUser.HasReceivedControl ? "1" : "0"));
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InternalModule", policy => policy.RequireClaim("moduleInternal", "1"));
    options.AddPolicy("InternalElevated", policy => policy.RequireAssertion(ctx =>
        ctx.User.FindFirstValue("moduleInternal") == "1" &&
        (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.TerminalAdmin) || ctx.User.IsInRole(Roles.LegacyAdmin) || ctx.User.IsInRole(Roles.Superuser))));
    options.AddPolicy("LinehaulModule", policy => policy.RequireClaim("moduleLinehaul", "1"));
    options.AddPolicy("LinehaulAdmin", policy => policy.RequireAssertion(ctx =>
        ctx.User.FindFirstValue("moduleLinehaul") == "1" &&
        (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.TerminalAdmin) || ctx.User.IsInRole(Roles.LegacyAdmin))));
    options.AddPolicy("ReceivedControlModule", policy => policy.RequireClaim("moduleReceivedControl", "1"));
    options.AddPolicy("ReceivedControlAdmin", policy => policy.RequireAssertion(ctx =>
        ctx.User.FindFirstValue("moduleReceivedControl") == "1" &&
        (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.TerminalAdmin) || ctx.User.IsInRole(Roles.LegacyAdmin))));
});

var app = builder.Build();
app.UseCors("ui");
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    EnsureCompatibilitySchema(db);
    Seed(db);
}

app.MapGet("/", () => Results.Ok(new
{
    name = "Pallet Control API",
    status = "running",
    version = "5.8.5"
}));

// Public health endpoint. This performs a real SQLite connection, real table read,
// and SQLite PRAGMA quick_check. It also reports whether a real backup exists.
app.MapGet("/api/health", async (
    AppDbContext db,
    DatabaseStorageOptions storage,
    DatabaseBackupManager backupManager) =>
{
    var started = DateTime.UtcNow;
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        if (!canConnect)
        {
            return Results.Json(new
            {
                status = "unhealthy",
                api = new { status = "online" },
                database = new { status = "offline", message = "Database connection failed." },
                checkedAtUtc = DateTime.UtcNow,
                responseMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1)
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Real application read.
        _ = await db.Settings.AsNoTracking().CountAsync();

        // Real SQLite integrity check.
        string quickCheck;
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

        var dbFile = new FileInfo(storage.DatabasePath);
        var backupStatus = backupManager.GetStatus();
        var healthy = string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase);

        var payload = new
        {
            status = healthy ? "healthy" : "unhealthy",
            api = new { status = "online" },
            database = new
            {
                status = healthy ? "online" : "unhealthy",
                provider = "SQLite",
                file = dbFile.Name,
                exists = dbFile.Exists,
                sizeBytes = dbFile.Exists ? dbFile.Length : 0,
                quickCheck,
                latestBackupUtc = backupStatus.LatestBackupUtc,
                backupCount = backupStatus.BackupCount
            },
            checkedAtUtc = DateTime.UtcNow,
            responseMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1)
        };

        return healthy
            ? Results.Ok(payload)
            : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status = "unhealthy",
            api = new { status = "online" },
            database = new { status = "offline", message = ex.Message },
            checkedAtUtc = DateTime.UtcNow,
            responseMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1)
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db) =>
{
    var username = req.Username.Trim().ToLowerInvariant();
    var user = await db.Users
        .Include(x => x.Terminal)
        .SingleOrDefaultAsync(x => x.Username == username && x.Active);

    if (user is null)
        return Results.Unauthorized();

    var hasher = new PasswordHasher<AppUser>();
    var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
    if (verification == PasswordVerificationResult.Failed)
        return Results.Unauthorized();

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role),
        new("terminalId", user.TerminalId.ToString()),
        new("terminalCode", user.Terminal?.Code ?? ""),
        new("moduleInternal", user.HasInternalPalletAccounting ? "1" : "0"),
        new("moduleLinehaul", user.HasLinehaul ? "1" : "0"),
        new("moduleReceivedControl", user.HasReceivedControl ? "1" : "0")
    };

    var creds = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        jwtIssuer,
        jwtAudience,
        claims,
        expires: DateTime.UtcNow.AddHours(12),
        signingCredentials: creds);

    return Results.Ok(new LoginResponse(
        new JwtSecurityTokenHandler().WriteToken(token),
        user.Username,
        user.DisplayName,
        user.Role,
        user.TerminalId,
        user.Terminal?.Code ?? "",
        user.ShowDriverStatisticsTab,
        user.ShowDailyCheckTab,
        user.HasInternalPalletAccounting,
        user.HasLinehaul,
        user.HasReceivedControl));
});

app.MapGet("/api/me", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var id = UserId(principal);
    var user = await db.Users.Include(x => x.Terminal).SingleAsync(x => x.Id == id);
    return Results.Ok(new
    {
        user.Id,
        user.Username,
        user.DisplayName,
        user.Role,
        user.TerminalId,
        terminalCode = user.Terminal?.Code ?? "",
        user.ShowDriverStatisticsTab,
        user.ShowDailyCheckTab,
        user.HasInternalPalletAccounting,
        user.HasLinehaul,
        user.HasReceivedControl
    });
}).RequireAuthorization();

app.MapGet("/api/me/settings", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var user = await db.Users.FindAsync(UserId(principal));
    if (user is null) return Results.NotFound();
    var settings = await GetTerminalSettings(db, user.TerminalId);

    return Results.Ok(new
    {
        user.ShowMilestoneNotifications,
        user.ShowLeaderboardNotifications,
        user.ShowBalanceNotifications,
        settings.AllowUsersAddDrivers
    });
}).RequireAuthorization();

app.MapPut("/api/me/settings", async (
    UserPreferenceRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var user = await db.Users.FindAsync(UserId(principal));
    if (user is null) return Results.NotFound();

    user.ShowMilestoneNotifications = req.ShowMilestoneNotifications;
    user.ShowLeaderboardNotifications = req.ShowLeaderboardNotifications;
    user.ShowBalanceNotifications = req.ShowBalanceNotifications;
    await db.SaveChangesAsync();

    var settings = await GetTerminalSettings(db, user.TerminalId);
    return Results.Ok(new
    {
        user.ShowMilestoneNotifications,
        user.ShowLeaderboardNotifications,
        user.ShowBalanceNotifications,
        settings.AllowUsersAddDrivers
    });
}).RequireAuthorization();

app.MapGet("/api/setup/register", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);

    var vehicles = await db.Vehicles
        .AsNoTracking()
        .Where(x => x.Active && x.TerminalId == terminalId && x.TransporterId != null)
        .Include(x => x.Transporter)
        .OrderBy(x => x.VehicleId)
        .Select(x => new
        {
            x.Id,
            x.VehicleId,
            x.TransporterId,
            transporter = x.Transporter != null ? x.Transporter.Name : "Not assigned"
        })
        .ToListAsync();

    var drivers = await db.Drivers
        .AsNoTracking()
        .Where(x => x.Active && x.TerminalId == terminalId)
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    var palletTypes = await db.PalletTypes
        .AsNoTracking()
        .Where(x => x.Active && x.UserSelectable)
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    var settings = await GetTerminalSettings(db, terminalId);

    return Results.Ok(new
    {
        vehicles,
        drivers,
        palletTypes,
        settings.AllowUsersAddDrivers
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/drivers/for-vehicle/{vehicleId:int}", async (
    int vehicleId,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var vehicle = await db.Vehicles.AsNoTracking()
        .FirstOrDefaultAsync(x => x.Id == vehicleId && x.Active && x.TerminalId == terminalId);

    if (vehicle is null)
        return Results.NotFound(new { message = "Vehicle not found." });

    var drivers = await db.Drivers.AsNoTracking()
        .Where(x => x.Active && x.TerminalId == terminalId)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    var usage = await db.Receipts.AsNoTracking()
        .Where(x => x.Status == ReceiptStatus.Active && x.VehicleId == vehicleId && x.DriverId != null)
        .GroupBy(x => x.DriverId!.Value)
        .Select(g => new { DriverId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.DriverId, x => x.Count);

    var sorted = drivers
        .Select(x => new
        {
            x.Id,
            x.Name,
            usageCount = usage.TryGetValue(x.Id, out var count) ? count : 0
        })
        .OrderByDescending(x => x.usageCount)
        .ThenBy(x => x.Name)
        .ToList();

    return Results.Ok(sorted);
}).RequireAuthorization("InternalModule");

app.MapPost("/api/drivers/quick-add", async (
    QuickDriverRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var settings = await GetTerminalSettings(db, terminalId);
    if (!settings.AllowUsersAddDrivers)
        return Results.Forbid();

    // Quick-add is always scoped to the terminal assigned to the logged-in user.
    // The client is intentionally not allowed to choose another terminal here.
    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Driver name is required." });

    var existing = await db.Drivers.FirstOrDefaultAsync(x =>
        x.TerminalId == terminalId && x.Name.ToLower() == name.ToLower());

    if (existing != null)
    {
        if (!existing.Active)
        {
            existing.Active = true;
            await db.SaveChangesAsync();
        }
        return Results.Ok(new { existing.Id, existing.Name });
    }

    var driver = new Driver { Name = name, TerminalId = terminalId, Active = true };
    db.Drivers.Add(driver);
    await db.SaveChangesAsync();
    await Audit(db, principal, "DRIVER_QUICK_ADD", $"Added driver {driver.Name}");
    return Results.Ok(new { driver.Id, driver.Name });
}).RequireAuthorization("InternalModule");

app.MapPost("/api/receipts/check", async (
    CreateReceiptRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var validation = await ValidateReceiptRequest(req, principal, db);
    if (validation.Error != null)
        return Results.BadRequest(new { message = validation.Error });

    var warnings = await EvaluateSubmissionWarnings(
        req,
        validation.Vehicle!,
        validation.Driver!,
        validation.PositiveItems,
        db);

    return Results.Ok(new { warnings });
}).RequireAuthorization("InternalModule");

app.MapPost("/api/receipts", async (
    CreateReceiptRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var existing = await db.Receipts
        .AsNoTracking()
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Include(x => x.Actions).ThenInclude(x => x.User)
        .FirstOrDefaultAsync(x => x.IdempotencyKey == req.IdempotencyKey);

    if (existing != null)
        return Results.Ok(new { receipt = ToReceiptDto(existing), warnings = Array.Empty<SubmissionWarningDto>(), notifications = Array.Empty<string>() });

    var validation = await ValidateReceiptRequest(req, principal, db);
    if (validation.Error != null)
        return Results.BadRequest(new { message = validation.Error });

    var vehicle = validation.Vehicle!;
    var driver = validation.Driver!;
    var positiveItems = validation.PositiveItems;

    var warnings = await EvaluateSubmissionWarnings(req, vehicle, driver, positiveItems, db);
    if (warnings.Count > 0 && !req.ConfirmWarnings)
    {
        return Results.Json(new
        {
            requiresConfirmation = true,
            message = "Please confirm the warnings before submitting.",
            warnings
        }, statusCode: StatusCodes.Status409Conflict);
    }

    var now = DateTime.UtcNow;
    var terminalId = TerminalId(principal);
    var terminal = await db.Terminals.FindAsync(terminalId);
    var submitterId = UserId(principal);
    var today = DateOnly.FromDateTime(DateTime.Today);
    var businessDate = req.BusinessDate ?? today;
    var manualDate = req.BusinessDate.HasValue && businessDate != today;

    var receipt = new PalletReceipt
    {
        ReceiptNumber = $"{terminal!.Code}-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        TerminalId = terminalId,
        VehicleId = vehicle.Id,
        DriverId = driver.Id,
        VehicleSnapshot = vehicle.VehicleId,
        DriverSnapshot = driver.Name,
        TransporterSnapshot = vehicle.Transporter?.Name ?? "Not assigned",
        Direction = req.Direction.ToUpperInvariant(),
        BusinessDate = businessDate,
        SubmittedAtUtc = now,
        SubmittedByUserId = submitterId,
        IdempotencyKey = req.IdempotencyKey,
        Status = ReceiptStatus.Active,
        Items = positiveItems.Select(x => new PalletReceiptItem
        {
            PalletTypeId = x.PalletTypeId,
            Quantity = x.Quantity
        }).ToList()
    };

    if (manualDate)
    {
        receipt.Actions.Add(new ReceiptAction
        {
            Action = "BUSINESS_DATE_OVERRIDE",
            Reason = $"Receipt date manually set to {businessDate:yyyy-MM-dd}. Actual submission time: {now:O}",
            CreatedAtUtc = now,
            UserId = submitterId
        });
    }

    db.Receipts.Add(receipt);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        var duplicate = await db.Receipts
            .AsNoTracking()
            .Include(x => x.Items).ThenInclude(x => x.PalletType)
            .Include(x => x.Actions).ThenInclude(x => x.User)
            .SingleOrDefaultAsync(x => x.IdempotencyKey == req.IdempotencyKey);

        if (duplicate != null)
            return Results.Ok(new { receipt = ToReceiptDto(duplicate), warnings = Array.Empty<SubmissionWarningDto>(), notifications = Array.Empty<string>() });
        throw;
    }

    foreach (var warning in warnings)
    {
        db.WarningEvents.Add(new WarningEvent
        {
            Type = warning.Type,
            Severity = warning.Severity,
            Message = warning.Message,
            CreatedAtUtc = now,
            ReceiptId = receipt.Id,
            TerminalId = terminalId,
            TriggeredByUserId = submitterId
        });
    }

    await db.SaveChangesAsync();
    await Audit(
        db,
        principal,
        manualDate ? "RECEIPT_CREATE_MANUAL_DATE" : "RECEIPT_CREATE",
        $"Created {receipt.ReceiptNumber}; BusinessDate={businessDate:yyyy-MM-dd}; SubmittedAtUtc={now:O}; " +
        $"Vehicle={vehicle.VehicleId}; Driver={driver.Name}; Transporter={vehicle.Transporter?.Name ?? "Not assigned"}");

    await db.Entry(receipt).Collection(x => x.Items).LoadAsync();
    foreach (var item in receipt.Items)
        await db.Entry(item).Reference(x => x.PalletType).LoadAsync();

    var submitNotifications = await BuildSubmitNotifications(receipt, driver.Id, principal, db);
    var dto = ToReceiptDto(receipt);

    return Results.Ok(new
    {
        receipt = dto,
        warnings,
        notifications = submitNotifications
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/receipts", async (
    DateOnly? date,
    int? limit,
    string? sort,
    string? status,
    string? search,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var role = Role(principal);
    var terminalId = TerminalId(principal);
    var isRegularUser = role == Roles.User;

    // Regular users always see the latest 25 receipts for their terminal,
    // regardless of date/limit/sort query parameters sent by the browser.
    // Admins and Superusers keep the selected-date + 25/50/All controls.
    var selectedDate = isRegularUser
        ? (DateOnly?)null
        : date ?? DateOnly.FromDateTime(DateTime.Today);

    var effectiveLimit = isRegularUser
        ? 25
        : limit switch
        {
            null => 25,
            <= 0 => 0,
            > 5000 => 5000,
            _ => limit.Value
        };

    IQueryable<PalletReceipt> query = db.Receipts
        .AsNoTracking()
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Include(x => x.Actions).ThenInclude(x => x.User);

    // Operational data is always isolated by the user's currently assigned terminal.
    query = query.Where(x => x.TerminalId == terminalId);

    if (isRegularUser)
    {
        query = query
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Take(25);
    }
    else
    {
        query = query.Where(x => x.BusinessDate == selectedDate!.Value);

        var statusFilter = (status ?? "all").Trim().ToLowerInvariant();
        query = statusFilter switch
        {
            "active" => query.Where(x => x.Status == ReceiptStatus.Active),
            "cancelled" => query.Where(x => x.Status == ReceiptStatus.Cancelled),
            "reversed" => query.Where(x => x.Actions.Any(a => a.Action == "CANCELLATION_REVERSED")),
            _ => query
        };

        // Receipt search is intentionally available only to Admin/Superuser.
        // Search happens before the 25/50 limit so matches are not hidden simply
        // because they are outside the first page of the selected date.
        var searchTerm = (search ?? "").Trim();
        if (searchTerm.Length > 0)
        {
            var quantitySearch = int.TryParse(
                searchTerm,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedQuantity)
                ? parsedQuantity
                : (int?)null;

            query = query.Where(x =>
                x.ReceiptNumber.Contains(searchTerm) ||
                x.VehicleSnapshot.Contains(searchTerm) ||
                x.DriverSnapshot.Contains(searchTerm) ||
                x.TransporterSnapshot.Contains(searchTerm) ||
                x.Direction.Contains(searchTerm) ||
                x.Status.Contains(searchTerm) ||
                (x.CancelReason != null && x.CancelReason.Contains(searchTerm)) ||
                x.Items.Any(i =>
                    (i.PalletType != null && i.PalletType.Name.Contains(searchTerm)) ||
                    (quantitySearch.HasValue && i.Quantity == quantitySearch.Value)) ||
                x.Actions.Any(a =>
                    a.Action.Contains(searchTerm) ||
                    a.Reason.Contains(searchTerm) ||
                    (a.User != null && (
                        a.User.DisplayName.Contains(searchTerm) ||
                        a.User.Username.Contains(searchTerm)))));
        }

        query = string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase)
            ? query.OrderBy(x => x.SubmittedAtUtc)
            : query.OrderByDescending(x => x.SubmittedAtUtc);

        if (effectiveLimit > 0)
            query = query.Take(effectiveLimit);
    }

    var receipts = await query.ToListAsync();
    return Results.Ok(new
    {
        date = selectedDate,
        limit = effectiveLimit == 0 ? "all" : effectiveLimit.ToString(CultureInfo.InvariantCulture),
        receipts = receipts.Select(ToReceiptDto).ToList()
    });
}).RequireAuthorization("InternalModule");

app.MapPost("/api/receipts/{id:int}/cancel", async (
    int id,
    CancelRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var reason = req.Reason.Trim();
    if (string.IsNullOrWhiteSpace(reason))
        return Results.BadRequest(new { message = "Cancellation reason is required." });

    var receipt = await db.Receipts
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Include(x => x.Actions).ThenInclude(x => x.User)
        .FirstOrDefaultAsync(x => x.Id == id);

    if (receipt is null) return Results.NotFound();
    if (receipt.TerminalId != TerminalId(principal)) return Results.NotFound();
    if (receipt.Status == ReceiptStatus.Cancelled)
        return Results.BadRequest(new { message = "Receipt is already cancelled." });

    var now = DateTime.UtcNow;
    var userId = UserId(principal);
    receipt.Status = ReceiptStatus.Cancelled;
    receipt.CancelledAtUtc = now;
    receipt.CancelledByUserId = userId;
    receipt.CancelReason = reason;

    receipt.Actions.Add(new ReceiptAction
    {
        Action = "CANCELLED",
        Reason = reason,
        CreatedAtUtc = now,
        UserId = userId
    });

    await db.SaveChangesAsync();

    var settings = await GetTerminalSettings(db, receipt.TerminalId);
    if (settings.CancellationWarningEnabled)
    {
        db.WarningEvents.Add(new WarningEvent
        {
            Type = "RECEIPT_CANCELLED",
            Severity = "info",
            Message = $"Receipt {receipt.ReceiptNumber} was cancelled. Reason: {reason}",
            CreatedAtUtc = now,
            ReceiptId = receipt.Id,
            TerminalId = receipt.TerminalId,
            TriggeredByUserId = userId
        });
        await db.SaveChangesAsync();
    }

    await Audit(db, principal, "RECEIPT_CANCEL", $"Cancelled {receipt.ReceiptNumber}: {reason}");
    await db.Entry(receipt).Collection(x => x.Actions).LoadAsync();
    foreach (var action in receipt.Actions)
        await db.Entry(action).Reference(x => x.User).LoadAsync();

    return Results.Ok(ToReceiptDto(receipt));
}).RequireAuthorization("InternalElevated");

app.MapPost("/api/receipts/{id:int}/reverse-cancellation", async (
    int id,
    ReverseCancellationRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var receipt = await db.Receipts
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Include(x => x.Actions).ThenInclude(x => x.User)
        .FirstOrDefaultAsync(x => x.Id == id);

    if (receipt is null) return Results.NotFound();
    if (receipt.TerminalId != TerminalId(principal)) return Results.NotFound();
    if (receipt.Status != ReceiptStatus.Cancelled)
        return Results.BadRequest(new { message = "Receipt is not cancelled." });

    var now = DateTime.UtcNow;
    var userId = UserId(principal);
    var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Cancellation reversed" : req.Reason.Trim();

    receipt.Status = ReceiptStatus.Active;
    receipt.Actions.Add(new ReceiptAction
    {
        Action = "CANCELLATION_REVERSED",
        Reason = reason,
        CreatedAtUtc = now,
        UserId = userId
    });

    // Current status fields are cleared. Full cancellation history remains in ReceiptActions.
    receipt.CancelledAtUtc = null;
    receipt.CancelledByUserId = null;
    receipt.CancelReason = null;

    await db.SaveChangesAsync();

    var settings = await GetTerminalSettings(db, receipt.TerminalId);
    if (settings.CancellationReversedWarningEnabled)
    {
        db.WarningEvents.Add(new WarningEvent
        {
            Type = "CANCELLATION_REVERSED",
            Severity = "info",
            Message = $"Cancellation was reversed for receipt {receipt.ReceiptNumber}.",
            CreatedAtUtc = now,
            ReceiptId = receipt.Id,
            TerminalId = receipt.TerminalId,
            TriggeredByUserId = userId
        });
        await db.SaveChangesAsync();
    }

    await Audit(db, principal, "RECEIPT_RESTORE", $"Restored {receipt.ReceiptNumber}: {reason}");
    await db.Entry(receipt).Collection(x => x.Actions).LoadAsync();
    foreach (var action in receipt.Actions)
        await db.Entry(action).Reference(x => x.User).LoadAsync();

    return Results.Ok(ToReceiptDto(receipt));
}).RequireAuthorization("InternalElevated");

app.MapGet("/api/statistics/options", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);

    var vehicleQuery = db.Vehicles
        .AsNoTracking()
        .Where(x => x.Active && x.TransporterId != null && x.TerminalId == terminalId)
        .Include(x => x.Transporter)
        .AsQueryable();

    // Statistics keep inactive/removed driver names available so historical receipts
    // can still be filtered after an Admin removes a name from future registration.
    var driverQuery = db.Drivers
        .AsNoTracking()
        .Where(x => x.TerminalId == terminalId)
        .AsQueryable();

    var transporterIdsForTerminal = await vehicleQuery
        .Where(x => x.TransporterId != null)
        .Select(x => x.TransporterId!.Value)
        .Distinct()
        .ToListAsync();

    var transporterQuery = db.Transporters
        .AsNoTracking()
        .Where(x => x.Active && transporterIdsForTerminal.Contains(x.Id));

    var transporters = await transporterQuery
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    var vehicles = await vehicleQuery
        .OrderBy(x => x.VehicleId)
        .Select(x => new
        {
            x.Id,
            x.VehicleId,
            x.TransporterId,
            transporter = x.Transporter != null ? x.Transporter.Name : "Not assigned"
        })
        .ToListAsync();

    var drivers = await driverQuery
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, name = x.Active ? x.Name : x.Name + " (removed)", rawName = x.Name, x.Active })
        .ToListAsync();

    var palletTypes = await db.PalletTypes.AsNoTracking()
        .Where(x => x.Active)
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    return Results.Ok(new { transporters, vehicles, drivers, palletTypes });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/statistics", async (
    DateOnly? from,
    DateOnly? to,
    int? palletTypeId,
    string? transporterIds,
    string? vehicleIds,
    string? driverIds,
    string? sortBy,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    from ??= new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    to ??= DateOnly.FromDateTime(DateTime.Today);

    if (to.Value < from.Value)
        return Results.BadRequest(new { message = "To date cannot be before From date." });

    var selectedTransporterIds = ParseIds(transporterIds);
    var selectedVehicleIds = ParseIds(vehicleIds);
    var selectedDriverIds = ParseIds(driverIds);
    var terminalId = TerminalId(principal);

    var query = db.Receipts
        .AsNoTracking()
        .Include(r => r.Vehicle).ThenInclude(v => v!.Transporter)
        .Include(r => r.Driver)
        .Include(r => r.Items).ThenInclude(i => i.PalletType)
        .Where(r =>
            r.Status == ReceiptStatus.Active &&
            r.BusinessDate >= from.Value &&
            r.BusinessDate <= to.Value);

    query = query.Where(r => r.TerminalId == terminalId);

    if (selectedTransporterIds.Count > 0)
        query = query.Where(r => r.Vehicle != null && r.Vehicle.TransporterId != null &&
                                 selectedTransporterIds.Contains(r.Vehicle.TransporterId.Value));

    if (selectedVehicleIds.Count > 0)
        query = query.Where(r => r.VehicleId != null && selectedVehicleIds.Contains(r.VehicleId.Value));

    if (selectedDriverIds.Count > 0)
        query = query.Where(r => r.DriverId != null && selectedDriverIds.Contains(r.DriverId.Value));

    // Materialize before SelectMany/GroupBy to avoid SQLite APPLY translation.
    var receipts = await query.ToListAsync();

    var flatRows = receipts
        .SelectMany(r => r.Items
            .Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
            .Select(i => new
            {
                Transporter = r.TransporterSnapshot,
                Vehicle = r.VehicleSnapshot,
                Driver = r.DriverSnapshot,
                Direction = r.Direction,
                PalletType = i.PalletType?.Name ?? "Unknown",
                Quantity = i.Quantity
            }))
        .ToList();

    var grouped = flatRows
        .GroupBy(x => new { x.Transporter, x.Vehicle, x.PalletType })
        .Select(g =>
        {
            var inQty = g.Where(x => x.Direction == "IN").Sum(x => x.Quantity);
            var outQty = g.Where(x => x.Direction == "OUT").Sum(x => x.Quantity);
            return new StatisticsRow
            {
                Transporter = g.Key.Transporter,
                Vehicle = g.Key.Vehicle,
                PalletType = g.Key.PalletType,
                InQty = inQty,
                OutQty = outQty,
                Balance = inQty - outQty,
                Movement = inQty + outQty
            };
        })
        .ToList();

    var sorted = (sortBy ?? "movementDesc") switch
    {
        "inDesc" => grouped.OrderByDescending(x => x.InQty).ThenBy(x => x.Vehicle),
        "outDesc" => grouped.OrderByDescending(x => x.OutQty).ThenBy(x => x.Vehicle),
        "balanceDesc" => grouped.OrderByDescending(x => x.Balance).ThenBy(x => x.Vehicle),
        "balanceAsc" => grouped.OrderBy(x => x.Balance).ThenBy(x => x.Vehicle),
        "vehicleAsc" => grouped.OrderBy(x => x.Vehicle).ThenBy(x => x.PalletType),
        _ => grouped.OrderByDescending(x => x.Movement).ThenBy(x => x.Vehicle)
    };

    var totalIn = flatRows.Where(x => x.Direction == "IN").Sum(x => x.Quantity);
    var totalOut = flatRows.Where(x => x.Direction == "OUT").Sum(x => x.Quantity);

    var totalsByPalletType = flatRows
        .GroupBy(x => x.PalletType)
        .Select(g =>
        {
            var inQty = g.Where(x => x.Direction == "IN").Sum(x => x.Quantity);
            var outQty = g.Where(x => x.Direction == "OUT").Sum(x => x.Quantity);
            return new { palletType = g.Key, inQty, outQty, balance = inQty - outQty };
        })
        .OrderBy(x => x.palletType)
        .ToList();

    return Results.Ok(new
    {
        from,
        to,
        filters = new
        {
            palletTypeId,
            transporterIds = selectedTransporterIds,
            vehicleIds = selectedVehicleIds,
            driverIds = selectedDriverIds,
            sortBy = sortBy ?? "movementDesc"
        },
        totalIn,
        totalOut,
        totalBalance = totalIn - totalOut,
        totalsByPalletType,
        rows = sorted.ToList()
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/statistics/drivers", async (
    DateOnly? from,
    DateOnly? to,
    int? palletTypeId,
    string? transporterIds,
    string? vehicleIds,
    string? driverIds,
    string? sortBy,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var currentUserId = UserId(principal);
    var currentUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == currentUserId);
    if (!currentUser.ShowDriverStatisticsTab) return Results.Forbid();

    from ??= new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    to ??= DateOnly.FromDateTime(DateTime.Today);

    if (to.Value < from.Value)
        return Results.BadRequest(new { message = "To date cannot be before From date." });

    var terminalId = TerminalId(principal);
    var selectedTransporterIds = ParseIds(transporterIds);
    var selectedVehicleIds = ParseIds(vehicleIds);
    var selectedDriverIds = ParseIds(driverIds);
    var settings = await GetTerminalSettings(db, terminalId);
    var deductionPerUnmatchedIn = Math.Max(0, settings.DriverUnmatchedInDeduction);

    var receiptQuery = db.Receipts
        .AsNoTracking()
        .Include(r => r.Vehicle).ThenInclude(v => v!.Transporter)
        .Include(r => r.Driver)
        .Include(r => r.Items).ThenInclude(i => i.PalletType)
        .Where(r =>
            r.Status == ReceiptStatus.Active &&
            r.TerminalId == terminalId &&
            r.BusinessDate >= from.Value &&
            r.BusinessDate <= to.Value &&
            r.DriverSnapshot != "");

    if (selectedTransporterIds.Count > 0)
        receiptQuery = receiptQuery.Where(r => r.Vehicle != null && r.Vehicle.TransporterId != null &&
                                             selectedTransporterIds.Contains(r.Vehicle.TransporterId.Value));
    if (selectedVehicleIds.Count > 0)
        receiptQuery = receiptQuery.Where(r => r.VehicleId != null && selectedVehicleIds.Contains(r.VehicleId.Value));
    if (selectedDriverIds.Count > 0)
        receiptQuery = receiptQuery.Where(r => r.DriverId != null && selectedDriverIds.Contains(r.DriverId.Value));

    var receipts = await receiptQuery.ToListAsync();

    // Group by stable DriverId when it still exists. For drivers that were physically deleted
    // by an older PalletControl version, fall back to the receipt's immutable DriverSnapshot.
    // New deletes are soft deletes, so their DriverId remains intact.
    var driverGroups = receipts
        .GroupBy(r => r.DriverId != null
            ? $"id:{r.DriverId.Value}"
            : $"snapshot:{r.DriverSnapshot.Trim().ToUpperInvariant()}")
        .OrderBy(g => g.First().DriverSnapshot)
        .ToList();

    var rows = new List<DriverStatisticsRow>();
    foreach (var group in driverGroups)
    {
        var driverReceipts = group.ToList();
        var first = driverReceipts.First();
        var driverId = first.DriverId ?? 0;
        var driverName = first.Driver?.Name ?? first.DriverSnapshot;
        var quantityItems = driverReceipts
            .SelectMany(r => r.Items
                .Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
                .Select(i => new { r.Direction, i.Quantity }))
            .ToList();

        var inQty = quantityItems.Where(x => x.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Quantity);
        var outQty = quantityItems.Where(x => x.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Quantity);

        // Matching is deliberately done per driver + business date, regardless of vehicle.
        // Every ACTIVE IN receipt needs one ACTIVE OUT receipt for that driver on the same day.
        // Example: 4 IN and 1 OUT across any vehicles on the same day = 3 unmatched IN receipts.
        // Cancelled receipts never participate in this calculation.
        var unmatchedIn = driverReceipts
            .GroupBy(r => r.BusinessDate)
            .Sum(g => Math.Max(
                0,
                g.Count(r => r.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase)) -
                g.Count(r => r.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase))));

        var inReceiptCount = driverReceipts.Count(r => r.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase));
        var outReceiptCount = driverReceipts.Count(r => r.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase));
        var rawBalance = inQty - outQty;
        var deduction = unmatchedIn * deductionPerUnmatchedIn;

        rows.Add(new DriverStatisticsRow
        {
            DriverId = driverId,
            Driver = driverName,
            Vehicles = string.Join(", ", driverReceipts.Select(r => r.VehicleSnapshot).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)),
            InReceipts = inReceiptCount,
            OutReceipts = outReceiptCount,
            UnmatchedInReceipts = unmatchedIn,
            InQty = inQty,
            OutQty = outQty,
            RawBalance = rawBalance,
            Deduction = deduction,
            AdjustedBalance = rawBalance - deduction,
            Movement = inQty + outQty
        });
    }

    var adjustmentDetails = receipts
        .GroupBy(r => new
        {
            DriverKey = r.DriverId != null ? $"id:{r.DriverId.Value}" : $"snapshot:{r.DriverSnapshot.Trim().ToUpperInvariant()}",
            DriverId = r.DriverId ?? 0,
            r.BusinessDate
        })
        .Select(g =>
        {
            var inReceipts = g.Count(r => r.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase));
            var outReceipts = g.Count(r => r.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase));
            var unmatchedIn = Math.Max(0, inReceipts - outReceipts);
            var first = g.First();
            return new DriverAdjustmentDetail
            {
                DriverId = g.Key.DriverId,
                Driver = first.Driver?.Name ?? first.DriverSnapshot,
                Vehicle = string.Join(", ", g.Select(r => r.VehicleSnapshot)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .OrderBy(x => x)),
                Date = g.Key.BusinessDate,
                InReceipts = inReceipts,
                OutReceipts = outReceipts,
                UnmatchedInReceipts = unmatchedIn,
                Deduction = unmatchedIn * deductionPerUnmatchedIn
            };
        })
        .Where(x => x.UnmatchedInReceipts > 0)
        .OrderByDescending(x => x.Date)
        .ThenBy(x => x.Driver)
        .ThenBy(x => x.Vehicle)
        .ToList();

    var sorted = (sortBy ?? "movementDesc") switch
    {
        "inDesc" => rows.OrderByDescending(x => x.InQty).ThenBy(x => x.Driver),
        "outDesc" => rows.OrderByDescending(x => x.OutQty).ThenBy(x => x.Driver),
        "rawBalanceDesc" => rows.OrderByDescending(x => x.RawBalance).ThenBy(x => x.Driver),
        "adjustedBalanceDesc" => rows.OrderByDescending(x => x.AdjustedBalance).ThenBy(x => x.Driver),
        "unmatchedDesc" => rows.OrderByDescending(x => x.UnmatchedInReceipts).ThenBy(x => x.Driver),
        "driverAsc" => rows.OrderBy(x => x.Driver),
        _ => rows.OrderByDescending(x => x.Movement).ThenBy(x => x.Driver)
    };

    var sortedRows = sorted.ToList();
    return Results.Ok(new
    {
        from,
        to,
        palletTypeId,
        deductionPerUnmatchedIn,
        totalIn = sortedRows.Sum(x => x.InQty),
        totalOut = sortedRows.Sum(x => x.OutQty),
        totalRawBalance = sortedRows.Sum(x => x.RawBalance),
        totalUnmatchedInReceipts = sortedRows.Sum(x => x.UnmatchedInReceipts),
        totalDeduction = sortedRows.Sum(x => x.Deduction),
        totalAdjustedBalance = sortedRows.Sum(x => x.AdjustedBalance),
        rows = sortedRows,
        adjustmentDetails
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/compliance", async (
    DateOnly? from,
    DateOnly? to,
    string? transporterIds,
    string? vehicleIds,
    string? driverIds,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var currentUserId = UserId(principal);
    var currentUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == currentUserId);
    if (!currentUser.ShowDailyCheckTab) return Results.Forbid();

    var today = DateOnly.FromDateTime(DateTime.Today);
    from ??= new DateOnly(today.Year, today.Month, 1);
    to ??= today;

    if (to.Value < from.Value)
        return Results.BadRequest(new { message = "To date cannot be before From date." });

    // Future dates are never treated as missed/pending work days.
    var effectiveTo = to.Value > today ? today : to.Value;
    if (effectiveTo < from.Value)
    {
        return Results.Ok(new
        {
            from,
            to,
            effectiveTo,
            expectedVehicleDays = 0,
            completeVehicleDays = 0,
            missedVehicleDays = 0,
            pendingTodayVehicleDays = 0,
            rows = Array.Empty<object>(),
            holidays = Array.Empty<object>()
        });
    }

    var terminalId = TerminalId(principal);
    var selectedTransporterIds = ParseIds(transporterIds);
    var selectedVehicleIds = ParseIds(vehicleIds);
    var selectedDriverIds = ParseIds(driverIds);

    var vehicleQuery = db.Vehicles
        .AsNoTracking()
        .Include(x => x.Transporter)
        .Where(x => x.Active && x.TerminalId == terminalId);

    if (selectedTransporterIds.Count > 0)
        vehicleQuery = vehicleQuery.Where(x => x.TransporterId != null && selectedTransporterIds.Contains(x.TransporterId.Value));
    if (selectedVehicleIds.Count > 0)
        vehicleQuery = vehicleQuery.Where(x => selectedVehicleIds.Contains(x.Id));

    var vehicles = await vehicleQuery
        .OrderBy(x => x.VehicleId)
        .ToListAsync();

    var holidays = await db.Holidays
        .AsNoTracking()
        .Where(x => x.Date >= from.Value && x.Date <= effectiveTo)
        .OrderBy(x => x.Date)
        .ToListAsync();

    var holidayDates = holidays.Select(x => x.Date).ToHashSet();

    var selectedVehicleIdSet = vehicles.Select(x => x.Id).ToHashSet();

    var receipts = await db.Receipts
        .AsNoTracking()
        .Where(x =>
            x.Status == ReceiptStatus.Active &&
            x.TerminalId == terminalId &&
            x.VehicleId != null &&
            selectedVehicleIdSet.Contains(x.VehicleId.Value) &&
            x.BusinessDate >= from.Value &&
            x.BusinessDate <= effectiveTo)
        .Select(x => new { x.VehicleId, x.BusinessDate, x.Direction, x.DriverId, x.DriverSnapshot })
        .ToListAsync();

    var receiptLookup = receipts
        .GroupBy(x => new { VehicleId = x.VehicleId!.Value, x.BusinessDate })
        .ToDictionary(
            g => (g.Key.VehicleId, g.Key.BusinessDate),
            g => new
            {
                Directions = g.Select(x => x.Direction.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                InDriverIds = g.Where(x => x.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase) && x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().OrderBy(x => x).ToList(),
                OutDriverIds = g.Where(x => x.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase) && x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().OrderBy(x => x).ToList(),
                InDrivers = g.Where(x => x.Direction.Equals("IN", StringComparison.OrdinalIgnoreCase)).Select(x => x.DriverSnapshot).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToList(),
                OutDrivers = g.Where(x => x.Direction.Equals("OUT", StringComparison.OrdinalIgnoreCase)).Select(x => x.DriverSnapshot).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToList()
            });

    var rows = new List<VehicleComplianceRow>();

    for (var date = from.Value; date <= effectiveTo; date = date.AddDays(1))
    {
        if (holidayDates.Contains(date))
            continue;

        var isoDay = IsoDayOfWeek(date);

        foreach (var vehicle in vehicles)
        {
            var operatingDays = ParseOperatingDays(vehicle.OperatingDays);
            if (!operatingDays.Contains(isoDay))
                continue;

            receiptLookup.TryGetValue((vehicle.Id, date), out var receiptInfo);
            var hasIn = receiptInfo?.Directions.Contains("IN") == true;
            var hasOut = receiptInfo?.Directions.Contains("OUT") == true;
            var complete = hasIn && hasOut;
            var isToday = date == today;

            rows.Add(new VehicleComplianceRow
            {
                Date = date,
                VehicleId = vehicle.Id,
                Vehicle = vehicle.VehicleId,
                Transporter = vehicle.Transporter?.Name ?? "Not assigned",
                InDriverIds = receiptInfo?.InDriverIds ?? [],
                OutDriverIds = receiptInfo?.OutDriverIds ?? [],
                InDrivers = receiptInfo?.InDrivers ?? [],
                OutDrivers = receiptInfo?.OutDrivers ?? [],
                HasIn = hasIn,
                HasOut = hasOut,
                Complete = complete,
                IsToday = isToday,
                Status = complete
                    ? "COMPLETE"
                    : hasIn
                        ? "MISSING_OUT"
                        : hasOut
                            ? "MISSING_IN"
                            : "MISSING_BOTH"
            });
        }
    }

    if (selectedDriverIds.Count > 0)
    {
        var driverSet = selectedDriverIds.ToHashSet();
        rows = rows
            .Where(x => x.InDriverIds.Any(driverSet.Contains) || x.OutDriverIds.Any(driverSet.Contains))
            .ToList();
    }

    var sortedRows = rows
        .OrderByDescending(x => x.Date)
        .ThenBy(x => x.Complete)
        .ThenBy(x => x.Vehicle)
        .ToList();

    return Results.Ok(new
    {
        from,
        to,
        effectiveTo,
        expectedVehicleDays = rows.Count,
        completeVehicleDays = rows.Count(x => x.Complete),
        missedVehicleDays = rows.Count(x => !x.IsToday && !x.Complete),
        pendingTodayVehicleDays = rows.Count(x => x.IsToday && !x.Complete),
        missingIn = rows.Count(x => !x.Complete && !x.HasIn),
        missingOut = rows.Count(x => !x.Complete && !x.HasOut),
        rows = sortedRows,
        holidays = holidays.Select(x => new { x.Id, x.Date, x.Name }).ToList()
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/statistics/best-drivers", async (
    string? period,
    int? palletTypeId,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var (from, to, normalizedPeriod) = ResolvePeriod(period);
    var terminalId = TerminalId(principal);

    var query = db.Receipts
        .AsNoTracking()
        .Include(x => x.Items)
        .Where(x => x.Status == ReceiptStatus.Active &&
                    x.BusinessDate >= from && x.BusinessDate <= to);

    query = query.Where(x => x.TerminalId == terminalId);

    var receipts = await query.ToListAsync();
    var leaderboard = BuildDriverLeaderboard(receipts, palletTypeId);

    return Results.Ok(new
    {
        period = normalizedPeriod,
        from,
        to,
        palletTypeId,
        drivers = leaderboard.Take(50).ToList()
    });
}).RequireAuthorization("InternalModule");

app.MapGet("/api/warnings", async (
    bool? unacknowledgedOnly,
    int? limit,
    string? search,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var take = Math.Clamp(limit ?? 100, 1, 500);

    var query = db.WarningEvents
        .AsNoTracking()
        .Include(x => x.Receipt)
        .Include(x => x.TriggeredByUser)
        .Include(x => x.AcknowledgedByUser)
        .AsQueryable();

    query = query.Where(x => x.TerminalId == terminalId);

    if (unacknowledgedOnly == true)
        query = query.Where(x => x.AcknowledgedAtUtc == null);

    var searchText = search?.Trim();
    if (!string.IsNullOrWhiteSpace(searchText))
    {
        query = query.Where(x =>
            x.Type.Contains(searchText) ||
            x.Message.Contains(searchText) ||
            (x.Receipt != null && (
                x.Receipt.ReceiptNumber.Contains(searchText) ||
                x.Receipt.VehicleSnapshot.Contains(searchText) ||
                x.Receipt.DriverSnapshot.Contains(searchText) ||
                x.Receipt.TransporterSnapshot.Contains(searchText))) ||
            (x.TriggeredByUser != null && (
                x.TriggeredByUser.DisplayName.Contains(searchText) ||
                x.TriggeredByUser.Username.Contains(searchText))) ||
            (x.AcknowledgedByUser != null && (
                x.AcknowledgedByUser.DisplayName.Contains(searchText) ||
                x.AcknowledgedByUser.Username.Contains(searchText))));
    }

    var rows = await query
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(take)
        .Select(x => new
        {
            x.Id,
            x.Type,
            x.Severity,
            x.Message,
            x.CreatedAtUtc,
            x.ReceiptId,
            receiptNumber = x.Receipt != null ? x.Receipt.ReceiptNumber : null,
            vehicle = x.Receipt != null ? x.Receipt.VehicleSnapshot : null,
            driver = x.Receipt != null ? x.Receipt.DriverSnapshot : null,
            transporter = x.Receipt != null ? x.Receipt.TransporterSnapshot : null,
            triggeredBy = x.TriggeredByUser != null ? x.TriggeredByUser.DisplayName : "System",
            x.AcknowledgedAtUtc,
            acknowledgedBy = x.AcknowledgedByUser != null ? x.AcknowledgedByUser.DisplayName : null
        })
        .ToListAsync();

    var openCountQuery = db.WarningEvents
        .AsNoTracking()
        .Where(x => x.AcknowledgedAtUtc == null && x.TerminalId == terminalId);

    return Results.Ok(new { openCount = await openCountQuery.CountAsync(), warnings = rows });
}).RequireAuthorization("InternalElevated");

app.MapPost("/api/warnings/{id:int}/acknowledge", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var warning = await db.WarningEvents.FindAsync(id);
    if (warning is null) return Results.NotFound();

    if (warning.TerminalId != TerminalId(principal))
        return Results.NotFound();

    if (warning.AcknowledgedAtUtc == null)
    {
        warning.AcknowledgedAtUtc = DateTime.UtcNow;
        warning.AcknowledgedByUserId = UserId(principal);
        await db.SaveChangesAsync();
        await Audit(db, principal, "WARNING_ACK", $"Acknowledged warning #{warning.Id}");
    }

    return Results.Ok();
}).RequireAuthorization("InternalElevated");

app.MapGet("/api/export", async (
    DateOnly from,
    DateOnly to,
    string? type,
    string? format,
    int? palletTypeId,
    string? transporterIds,
    string? vehicleIds,
    string? driverIds,
    string? direction,
    string? status,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (to < from)
        return Results.BadRequest(new { message = "To date cannot be before From date." });

    var terminalId = TerminalId(principal);
    var terminalCode = principal.FindFirstValue("terminalCode") ?? "TERM";
    var exportType = (type ?? "receipts").Trim().ToLowerInvariant();
    var exportFormat = (format ?? "csv").Trim().ToLowerInvariant();
    var selectedTransporterIds = ParseIds(transporterIds);
    var selectedVehicleIds = ParseIds(vehicleIds);
    var selectedDriverIds = ParseIds(driverIds);
    var directionFilter = (direction ?? "all").Trim().ToUpperInvariant();
    var statusFilter = (status ?? "active").Trim().ToUpperInvariant();

    if (exportFormat is not ("csv" or "xlsx"))
        return Results.BadRequest(new { message = "Format must be csv or xlsx." });
    if (exportType == "complete" && exportFormat != "xlsx")
        return Results.BadRequest(new { message = "Complete report is available as Excel (.xlsx)." });

    var receiptQuery = db.Receipts
        .AsNoTracking()
        .Include(x => x.Terminal)
        .Include(x => x.SubmittedByUser)
        .Include(x => x.Vehicle).ThenInclude(x => x!.Transporter)
        .Include(x => x.Driver)
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Where(x => x.TerminalId == terminalId && x.BusinessDate >= from && x.BusinessDate <= to);

    if (statusFilter == "ACTIVE") receiptQuery = receiptQuery.Where(x => x.Status == ReceiptStatus.Active);
    else if (statusFilter == "CANCELLED") receiptQuery = receiptQuery.Where(x => x.Status == ReceiptStatus.Cancelled);

    if (directionFilter is "IN" or "OUT")
        receiptQuery = receiptQuery.Where(x => x.Direction == directionFilter);
    if (selectedTransporterIds.Count > 0)
        receiptQuery = receiptQuery.Where(x => x.Vehicle != null && x.Vehicle.TransporterId != null && selectedTransporterIds.Contains(x.Vehicle.TransporterId.Value));
    if (selectedVehicleIds.Count > 0)
        receiptQuery = receiptQuery.Where(x => x.VehicleId != null && selectedVehicleIds.Contains(x.VehicleId.Value));
    if (selectedDriverIds.Count > 0)
        receiptQuery = receiptQuery.Where(x => x.DriverId != null && selectedDriverIds.Contains(x.DriverId.Value));

    var receipts = await receiptQuery
        .OrderBy(x => x.BusinessDate)
        .ThenBy(x => x.SubmittedAtUtc)
        .ToListAsync();

    // Driver statistics/adjustment exports always use ACTIVE receipts and both directions,
    // regardless of the receipt-detail Status/Direction filters. A cancelled OUT must never
    // satisfy an IN/OUT pair, and a cancelled IN must never create a deduction.
    var driverStatsQuery = db.Receipts
        .AsNoTracking()
        .Include(x => x.Vehicle).ThenInclude(x => x!.Transporter)
        .Include(x => x.Driver)
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Where(x => x.Status == ReceiptStatus.Active &&
                    x.TerminalId == terminalId &&
                    x.BusinessDate >= from && x.BusinessDate <= to &&
                    x.DriverSnapshot != "");

    if (selectedTransporterIds.Count > 0)
        driverStatsQuery = driverStatsQuery.Where(x => x.Vehicle != null && x.Vehicle.TransporterId != null && selectedTransporterIds.Contains(x.Vehicle.TransporterId.Value));
    if (selectedVehicleIds.Count > 0)
        driverStatsQuery = driverStatsQuery.Where(x => x.VehicleId != null && selectedVehicleIds.Contains(x.VehicleId.Value));
    if (selectedDriverIds.Count > 0)
        driverStatsQuery = driverStatsQuery.Where(x => x.DriverId != null && selectedDriverIds.Contains(x.DriverId.Value));

    var activeDriverReceipts = await driverStatsQuery
        .OrderBy(x => x.BusinessDate)
        .ThenBy(x => x.SubmittedAtUtc)
        .ToListAsync();

    var cancellationUserIds = receipts.Where(x => x.CancelledByUserId != null).Select(x => x.CancelledByUserId!.Value).Distinct().ToList();
    var cancellationUsers = cancellationUserIds.Count == 0
        ? new Dictionary<int, string>()
        : await db.Users.AsNoTracking().Where(x => cancellationUserIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username);

    var settings = await GetTerminalSettings(db, terminalId);
    var deductionPerUnmatchedIn = Math.Max(0, settings.DriverUnmatchedInDeduction);

    var detailTable = new ExportTable(
        "Receipt details",
        ["Receipt ID", "Terminal", "Transporter", "Vehicle", "Date", "Driver", "Direction", "Pallet Type", "Quantity", "Submitted At Local", "Submitted At UTC", "Submitted By", "Status", "Cancelled At Local", "Cancelled By", "Cancel Reason"],
        []);

    foreach (var r in receipts)
    {
        foreach (var i in r.Items.Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value))
        {
            detailTable.Rows.Add([
                r.ReceiptNumber,
                r.Terminal?.Code ?? terminalCode,
                r.TransporterSnapshot,
                r.VehicleSnapshot,
                r.BusinessDate.ToString("yyyy-MM-dd"),
                r.DriverSnapshot,
                r.Direction,
                i.PalletType?.Name ?? "",
                i.Quantity,
                r.SubmittedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.SubmittedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                r.SubmittedByUser?.Username ?? "",
                r.Status,
                r.CancelledAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                r.CancelledByUserId is int cancelledBy && cancellationUsers.TryGetValue(cancelledBy, out var cancelledName) ? cancelledName : "",
                r.CancelReason ?? ""
            ]);
        }
    }

    var receiptItemRows = receipts
        .SelectMany(r => r.Items
            .Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
            .Select(i => new
            {
                Receipt = r,
                PalletType = i.PalletType?.Name ?? "Unknown",
                i.Quantity
            }))
        .ToList();

    var vehicleTable = new ExportTable(
        "Vehicle summary",
        ["Transporter", "Vehicle", "Pallet Type", "IN", "OUT", "Balance", "Movement", "IN Receipts", "OUT Receipts"],
        []);

    foreach (var g in receiptItemRows.GroupBy(x => new { x.Receipt.TransporterSnapshot, x.Receipt.VehicleSnapshot, x.PalletType })
                 .OrderBy(x => x.Key.TransporterSnapshot).ThenBy(x => x.Key.VehicleSnapshot).ThenBy(x => x.Key.PalletType))
    {
        var inQty = g.Where(x => x.Receipt.Direction == "IN").Sum(x => x.Quantity);
        var outQty = g.Where(x => x.Receipt.Direction == "OUT").Sum(x => x.Quantity);
        vehicleTable.Rows.Add([
            g.Key.TransporterSnapshot,
            g.Key.VehicleSnapshot,
            g.Key.PalletType,
            inQty,
            outQty,
            inQty - outQty,
            inQty + outQty,
            g.Where(x => x.Receipt.Direction == "IN").Select(x => x.Receipt.Id).Distinct().Count(),
            g.Where(x => x.Receipt.Direction == "OUT").Select(x => x.Receipt.Id).Distinct().Count()
        ]);
    }

    var driverTable = new ExportTable(
        "Driver summary",
        ["Driver", "Vehicles", "IN Receipts", "OUT Receipts", "Unmatched IN Receipts", "IN Pallets", "OUT Pallets", "Raw Balance", "Deduction Per Unmatched IN", "Deduction", "Adjusted Balance", "Movement"],
        []);

    foreach (var g in activeDriverReceipts
                 .GroupBy(x => x.DriverId != null
                     ? $"id:{x.DriverId.Value}"
                     : $"snapshot:{x.DriverSnapshot.Trim().ToUpperInvariant()}")
                 .OrderBy(x => x.First().DriverSnapshot))
    {
        var driverReceipts = g.ToList();
        var driverName = driverReceipts.First().Driver?.Name ?? driverReceipts.First().DriverSnapshot;
        var items = driverReceipts
            .SelectMany(r => r.Items.Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
                .Select(i => new { r.Direction, i.Quantity }))
            .ToList();
        var inQty = items.Where(x => x.Direction == "IN").Sum(x => x.Quantity);
        var outQty = items.Where(x => x.Direction == "OUT").Sum(x => x.Quantity);
        var unmatchedIn = driverReceipts
            .GroupBy(r => r.BusinessDate)
            .Sum(day => Math.Max(0,
                day.Count(r => r.Direction == "IN") -
                day.Count(r => r.Direction == "OUT")));
        var deduction = unmatchedIn * deductionPerUnmatchedIn;
        var rawBalance = inQty - outQty;

        driverTable.Rows.Add([
            driverName,
            string.Join(", ", driverReceipts.Select(r => r.VehicleSnapshot).Distinct().OrderBy(x => x)),
            driverReceipts.Count(r => r.Direction == "IN"),
            driverReceipts.Count(r => r.Direction == "OUT"),
            unmatchedIn,
            inQty,
            outQty,
            rawBalance,
            deductionPerUnmatchedIn,
            deduction,
            rawBalance - deduction,
            inQty + outQty
        ]);
    }

    var driverAdjustmentTable = new ExportTable(
        "Driver adjustments",
        ["Date", "Driver", "Vehicles", "IN Receipts", "OUT Receipts", "Unmatched IN Receipts", "Deduction Per Unmatched IN", "Deduction"],
        activeDriverReceipts
            .GroupBy(x => new
            {
                DriverKey = x.DriverId != null ? $"id:{x.DriverId.Value}" : $"snapshot:{x.DriverSnapshot.Trim().ToUpperInvariant()}",
                x.BusinessDate
            })
            .Select(g =>
            {
                var inReceipts = g.Count(x => x.Direction == "IN");
                var outReceipts = g.Count(x => x.Direction == "OUT");
                var unmatched = Math.Max(0, inReceipts - outReceipts);
                var first = g.First();
                return new
                {
                    g.Key.BusinessDate,
                    Driver = first.Driver?.Name ?? first.DriverSnapshot,
                    Vehicles = string.Join(", ", g.Select(x => x.VehicleSnapshot)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .OrderBy(x => x)),
                    InReceipts = inReceipts,
                    OutReceipts = outReceipts,
                    Unmatched = unmatched,
                    Deduction = unmatched * deductionPerUnmatchedIn
                };
            })
            .Where(x => x.Unmatched > 0)
            .OrderByDescending(x => x.BusinessDate)
            .ThenBy(x => x.Driver)
            .Select(x => new List<object?>
            {
                x.BusinessDate.ToString("yyyy-MM-dd"), x.Driver, x.Vehicles,
                x.InReceipts, x.OutReceipts, x.Unmatched, deductionPerUnmatchedIn, x.Deduction
            })
            .ToList());

    var transporterTable = new ExportTable(
        "Transporter summary",
        ["Transporter", "Vehicles", "Receipts", "IN Receipts", "OUT Receipts", "IN Pallets", "OUT Pallets", "Balance", "Movement"],
        []);

    foreach (var g in receipts.GroupBy(x => x.TransporterSnapshot).OrderBy(x => x.Key))
    {
        var transporterReceipts = g.ToList();
        var items = transporterReceipts
            .SelectMany(r => r.Items.Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
                .Select(i => new { r.Direction, i.Quantity }))
            .ToList();
        var inQty = items.Where(x => x.Direction == "IN").Sum(x => x.Quantity);
        var outQty = items.Where(x => x.Direction == "OUT").Sum(x => x.Quantity);
        transporterTable.Rows.Add([
            g.Key,
            transporterReceipts.Select(x => x.VehicleSnapshot).Distinct().Count(),
            transporterReceipts.Count,
            transporterReceipts.Count(x => x.Direction == "IN"),
            transporterReceipts.Count(x => x.Direction == "OUT"),
            inQty,
            outQty,
            inQty - outQty,
            inQty + outQty
        ]);
    }

    // Daily compliance export is based on all ACTIVE receipts, independent of the receipt status/direction filters above.
    var today = DateOnly.FromDateTime(DateTime.Today);
    var effectiveTo = to > today ? today : to;
    var complianceRows = new List<VehicleComplianceRow>();

    if (effectiveTo >= from)
    {
        var vehicleQuery = db.Vehicles.AsNoTracking().Include(x => x.Transporter).Where(x => x.Active && x.TerminalId == terminalId);
        if (selectedTransporterIds.Count > 0)
            vehicleQuery = vehicleQuery.Where(x => x.TransporterId != null && selectedTransporterIds.Contains(x.TransporterId.Value));
        if (selectedVehicleIds.Count > 0)
            vehicleQuery = vehicleQuery.Where(x => selectedVehicleIds.Contains(x.Id));
        var vehicles = await vehicleQuery.OrderBy(x => x.VehicleId).ToListAsync();
        var vehicleSet = vehicles.Select(x => x.Id).ToHashSet();

        var holidays = await db.Holidays.AsNoTracking().Where(x => x.Date >= from && x.Date <= effectiveTo).ToListAsync();
        var holidayDates = holidays.Select(x => x.Date).ToHashSet();
        var complianceReceipts = await db.Receipts.AsNoTracking()
            .Where(x => x.Status == ReceiptStatus.Active && x.TerminalId == terminalId && x.VehicleId != null && vehicleSet.Contains(x.VehicleId.Value) && x.BusinessDate >= from && x.BusinessDate <= effectiveTo)
            .Select(x => new { x.VehicleId, x.BusinessDate, x.Direction, x.DriverId, x.DriverSnapshot })
            .ToListAsync();
        var lookup = complianceReceipts.GroupBy(x => new { VehicleId = x.VehicleId!.Value, x.BusinessDate }).ToDictionary(
            g => (g.Key.VehicleId, g.Key.BusinessDate),
            g => new
            {
                Directions = g.Select(x => x.Direction.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                InDriverIds = g.Where(x => x.Direction == "IN" && x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToList(),
                OutDriverIds = g.Where(x => x.Direction == "OUT" && x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToList(),
                InDrivers = g.Where(x => x.Direction == "IN").Select(x => x.DriverSnapshot).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToList(),
                OutDrivers = g.Where(x => x.Direction == "OUT").Select(x => x.DriverSnapshot).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToList()
            });

        for (var date = from; date <= effectiveTo; date = date.AddDays(1))
        {
            if (holidayDates.Contains(date)) continue;
            var isoDay = IsoDayOfWeek(date);
            foreach (var vehicle in vehicles)
            {
                if (!ParseOperatingDays(vehicle.OperatingDays).Contains(isoDay)) continue;
                lookup.TryGetValue((vehicle.Id, date), out var info);
                var hasIn = info?.Directions.Contains("IN") == true;
                var hasOut = info?.Directions.Contains("OUT") == true;
                var complete = hasIn && hasOut;
                complianceRows.Add(new VehicleComplianceRow
                {
                    Date = date,
                    VehicleId = vehicle.Id,
                    Vehicle = vehicle.VehicleId,
                    Transporter = vehicle.Transporter?.Name ?? "Not assigned",
                    InDriverIds = info?.InDriverIds ?? [],
                    OutDriverIds = info?.OutDriverIds ?? [],
                    InDrivers = info?.InDrivers ?? [],
                    OutDrivers = info?.OutDrivers ?? [],
                    HasIn = hasIn,
                    HasOut = hasOut,
                    Complete = complete,
                    IsToday = date == today,
                    Status = complete ? "COMPLETE" : hasIn ? "MISSING_OUT" : hasOut ? "MISSING_IN" : "MISSING_BOTH"
                });
            }
        }

        if (selectedDriverIds.Count > 0)
        {
            var driverSet = selectedDriverIds.ToHashSet();
            complianceRows = complianceRows.Where(x => x.InDriverIds.Any(driverSet.Contains) || x.OutDriverIds.Any(driverSet.Contains)).ToList();
        }
    }

    var dailyTable = new ExportTable(
        "Daily check",
        ["Date", "Vehicle", "Transporter", "IN", "IN Driver(s)", "OUT", "OUT Driver(s)", "Status"],
        complianceRows.OrderByDescending(x => x.Date).ThenBy(x => x.Vehicle).Select(x => new List<object?>
        {
            x.Date.ToString("yyyy-MM-dd"), x.Vehicle, x.Transporter,
            x.HasIn ? "YES" : "MISSING", string.Join(", ", x.InDrivers),
            x.HasOut ? "YES" : "MISSING", string.Join(", ", x.OutDrivers),
            x.Complete ? "Complete" : x.IsToday ? "Pending today" : x.Status == "MISSING_IN" ? "Missing IN" : x.Status == "MISSING_OUT" ? "Missing OUT" : "Missing IN + OUT"
        }).ToList());

    var missingTable = new ExportTable(
        "Missing receipts",
        ["Date", "Vehicle", "Transporter", "Missing", "IN Driver(s)", "OUT Driver(s)"],
        complianceRows.Where(x => !x.IsToday && !x.Complete).OrderByDescending(x => x.Date).ThenBy(x => x.Vehicle).Select(x => new List<object?>
        {
            x.Date.ToString("yyyy-MM-dd"), x.Vehicle, x.Transporter,
            !x.HasIn && !x.HasOut ? "IN + OUT" : !x.HasIn ? "IN" : "OUT",
            string.Join(", ", x.InDrivers), string.Join(", ", x.OutDrivers)
        }).ToList());

    ExportTable selectedTable = exportType switch
    {
        "vehicles" => vehicleTable,
        "drivers" => driverTable,
        "transporters" => transporterTable,
        "daily" => dailyTable,
        "missing" => missingTable,
        _ => detailTable
    };

    var tables = exportType == "complete"
        ? new List<ExportTable> { detailTable, vehicleTable, driverTable, driverAdjustmentTable, transporterTable, dailyTable, missingTable }
        : exportType == "drivers" && exportFormat == "xlsx"
            ? new List<ExportTable> { driverTable, driverAdjustmentTable }
            : new List<ExportTable> { selectedTable };

    byte[] bytes;
    string contentType;
    string extension;
    if (exportFormat == "xlsx")
    {
        bytes = ExportWorkbook(tables);
        contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        extension = "xlsx";
    }
    else
    {
        bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ExportCsv(selectedTable))).ToArray();
        contentType = "text/csv; charset=utf-8";
        extension = "csv";
    }

    await Audit(db, principal, "EXPORT", $"Exported {exportType}/{exportFormat} for terminal {terminalCode} from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
    var safeType = exportType == "complete" ? "CompleteReport" : selectedTable.Name.Replace(" ", "");
    return Results.File(bytes, contentType, $"PalletControl_{terminalCode}_{safeType}_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.{extension}");
}).RequireAuthorization("InternalElevated");


// ---------------- LINEHAUL PALLET ACCOUNTING ----------------

app.MapGet("/api/linehaul/setup", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var terminals = await db.Terminals.AsNoTracking()
        .Where(x => x.Active)
        .OrderBy(x => x.Code)
        .Select(x => new { x.Id, x.Code, x.Name })
        .ToListAsync();
    var comments = await db.LinehaulCommentOptions.AsNoTracking()
        .Where(x => x.TerminalId == terminalId && x.Active)
        .OrderBy(x => x.Text)
        .Select(x => new { x.Id, x.Text })
        .ToListAsync();
    return Results.Ok(new { terminalId, terminalCode = principal.FindFirstValue("terminalCode") ?? "", terminals, comments });
}).RequireAuthorization("LinehaulModule");

app.MapPost("/api/linehaul/receipts", async (
    CreateLinehaulReceiptRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var currentTerminalId = TerminalId(principal);
    var reference = (req.UnitReference ?? "").Trim();
    var palletReceiptNumber = (req.PalletReceiptNumber ?? "").Trim();
    // Container/trailer reference is optional. This supports historical/manual movements
    // where the pallet receipt is known but no unit number was recorded.
    if (reference.Length > 120)
        return Results.BadRequest(new { message = "Container/trailer text is too long." });
    if (string.IsNullOrWhiteSpace(palletReceiptNumber))
        return Results.BadRequest(new { message = "Pallet receipt number is required." });
    if (palletReceiptNumber.Length > 120)
        return Results.BadRequest(new { message = "Pallet receipt number is too long." });

    var normalizedPalletReceiptNumber = palletReceiptNumber.ToUpperInvariant();
    if (await db.LinehaulReceipts.AsNoTracking().AnyAsync(x =>
            x.PalletReceiptNumber != "" && x.PalletReceiptNumber.ToUpper() == normalizedPalletReceiptNumber))
        return Results.Conflict(new { message = $"Pallet receipt number {palletReceiptNumber} already exists in Linehaul." });
    if (req.PalletCount < 0 || req.PalletCount > 10000)
        return Results.BadRequest(new { message = "Pallet count must be between 0 and 10000." });
    if (req.FromTerminalId == req.ToTerminalId)
        return Results.BadRequest(new { message = "From terminal and To terminal must be different." });

    var terminals = await db.Terminals.AsNoTracking()
        .Where(x => x.Active && (x.Id == req.FromTerminalId || x.Id == req.ToTerminalId))
        .ToListAsync();
    var fromTerminal = terminals.FirstOrDefault(x => x.Id == req.FromTerminalId);
    var toTerminal = terminals.FirstOrDefault(x => x.Id == req.ToTerminalId);
    if (fromTerminal is null || toTerminal is null)
        return Results.BadRequest(new { message = "From or To terminal was not found/active." });

    // Operational Linehaul registration is always tied to the user's assigned terminal,
    // including SuperAdmin. Admin rights must not bypass terminal ownership of operational data.
    if (req.FromTerminalId != currentTerminalId && req.ToTerminalId != currentTerminalId)
        return Results.BadRequest(new { message = "One side of the Linehaul movement must be your assigned terminal." });

    string optionText = "";
    if (req.CommentOptionId.HasValue)
    {
        var option = await db.LinehaulCommentOptions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.CommentOptionId.Value && x.TerminalId == currentTerminalId && x.Active);
        if (option is null)
            return Results.BadRequest(new { message = "Selected standard comment is not available for your terminal." });
        optionText = option.Text;
    }

    var businessDate = req.BusinessDate ?? DateOnly.FromDateTime(DateTime.Today);
    var now = DateTime.UtcNow;
    var terminalCode = principal.FindFirstValue("terminalCode") ?? "TERM";
    var row = new LinehaulReceipt
    {
        ReceiptNumber = $"LH-{terminalCode}-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        OwnerTerminalId = currentTerminalId,
        FromTerminalId = fromTerminal.Id,
        ToTerminalId = toTerminal.Id,
        FromTerminalSnapshot = fromTerminal.Code,
        ToTerminalSnapshot = toTerminal.Code,
        UnitReference = reference,
        PalletReceiptNumber = palletReceiptNumber,
        PalletCount = req.PalletCount,
        CommentOptionSnapshot = optionText,
        FreeComment = (req.FreeComment ?? "").Trim(),
        BusinessDate = businessDate,
        SubmittedAtUtc = now,
        SubmittedByUserId = UserId(principal),
        Status = ReceiptStatus.Active
    };
    db.LinehaulReceipts.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "LINEHAUL_CREATE", $"{row.ReceiptNumber}: {row.FromTerminalSnapshot}->{row.ToTerminalSnapshot}, {row.PalletCount} pallets, {row.UnitReference}, pallet receipt {row.PalletReceiptNumber}");
    return Results.Ok(ToLinehaulDto(row));
}).RequireAuthorization("LinehaulModule");

app.MapGet("/api/linehaul/receipts", async (
    DateOnly? from,
    DateOnly? to,
    int? fromTerminalId,
    int? toTerminalId,
    string? direction,
    string? status,
    string? search,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var start = from ?? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    var end = to ?? DateOnly.FromDateTime(DateTime.Today);
    if (end < start) return Results.BadRequest(new { message = "To date cannot be before From date." });

    var q = db.LinehaulReceipts.AsNoTracking()
        .Where(x => (x.FromTerminalId == terminalId || x.ToTerminalId == terminalId || x.OwnerTerminalId == terminalId) && x.BusinessDate >= start && x.BusinessDate <= end);
    if (fromTerminalId.HasValue) q = q.Where(x => x.FromTerminalId == fromTerminalId.Value);
    if (toTerminalId.HasValue) q = q.Where(x => x.ToTerminalId == toTerminalId.Value);
    var recordStatus = (status ?? "all").Trim().ToLowerInvariant();
    if (recordStatus == "active") q = q.Where(x => x.Status == ReceiptStatus.Active);
    if (recordStatus == "cancelled") q = q.Where(x => x.Status == ReceiptStatus.Cancelled);
    var dir = (direction ?? "all").Trim().ToLowerInvariant();
    if (dir == "sent") q = q.Where(x => x.FromTerminalId == terminalId);
    if (dir == "received") q = q.Where(x => x.ToTerminalId == terminalId);
    var term = search?.Trim();
    if (!string.IsNullOrWhiteSpace(term))
        q = q.Where(x => x.UnitReference.Contains(term) || x.PalletReceiptNumber.Contains(term) || x.FreeComment.Contains(term) || x.CommentOptionSnapshot.Contains(term) || x.ReceiptNumber.Contains(term));

    var rows = await q.OrderByDescending(x => x.BusinessDate).ThenByDescending(x => x.SubmittedAtUtc).Take(5000).ToListAsync();
    var userIds = rows.Select(x => x.SubmittedByUserId)
        .Concat(rows.Where(x => x.CancelledByUserId.HasValue).Select(x => x.CancelledByUserId!.Value))
        .Distinct().ToList();
    var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName);
    var terminalCodes = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
    var adminCanManageOwn = IsTerminalAdmin(principal) || Role(principal) == Roles.LegacyAdmin;
    return Results.Ok(new
    {
        from = start,
        to = end,
        terminalId,
        rows = rows.Select(x => new
        {
            x.Id, x.ReceiptNumber, x.BusinessDate, x.SubmittedAtUtc, x.UnitReference, x.PalletReceiptNumber, x.PalletCount,
            x.OwnerTerminalId, x.FromTerminalId, x.ToTerminalId,
            fromTerminal = terminalCodes.GetValueOrDefault(x.FromTerminalId, x.FromTerminalSnapshot),
            toTerminal = terminalCodes.GetValueOrDefault(x.ToTerminalId, x.ToTerminalSnapshot),
            standardComment = x.CommentOptionSnapshot, x.FreeComment, x.Status, x.CancelledAtUtc, x.CancelReason,
            cancelledBy = x.CancelledByUserId.HasValue ? users.GetValueOrDefault(x.CancelledByUserId.Value, "Unknown") : null,
            submittedBy = users.GetValueOrDefault(x.SubmittedByUserId, "Unknown"),
            canManage = IsSuperAdmin(principal) || (adminCanManageOwn && x.OwnerTerminalId == terminalId)
        })
    });
}).RequireAuthorization("LinehaulModule");

app.MapPost("/api/linehaul/receipts/{id:int}/cancel", async (
    int id,
    CancelRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.LinehaulReceipts.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound();
    var terminalId = TerminalId(principal);
    if (!IsSuperAdmin(principal) && (!IsTerminalAdmin(principal) || row.OwnerTerminalId != terminalId))
        return Results.Forbid();
    if (row.Status == ReceiptStatus.Cancelled)
        return Results.BadRequest(new { message = "Linehaul receipt is already cancelled." });

    var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Cancelled by administrator" : req.Reason.Trim();
    row.Status = ReceiptStatus.Cancelled;
    row.CancelledAtUtc = DateTime.UtcNow;
    row.CancelledByUserId = UserId(principal);
    row.CancelReason = reason;
    await db.SaveChangesAsync();
    await Audit(db, principal, "LINEHAUL_CANCEL", $"Cancelled {row.ReceiptNumber}: {reason}");
    return Results.Ok(new { row.Id, row.Status, row.CancelledAtUtc, row.CancelReason });
}).RequireAuthorization("LinehaulAdmin");

app.MapDelete("/api/linehaul/receipts/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.LinehaulReceipts.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound();
    var terminalId = TerminalId(principal);
    if (!IsSuperAdmin(principal) && (!IsTerminalAdmin(principal) || row.OwnerTerminalId != terminalId))
        return Results.Forbid();

    var receiptNumber = row.ReceiptNumber;
    var palletReceiptNumber = row.PalletReceiptNumber;
    db.LinehaulReceipts.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "LINEHAUL_DELETE_PERMANENT", $"Permanently deleted {receiptNumber}; pallet receipt {palletReceiptNumber}");
    return Results.Ok();
}).RequireAuthorization("LinehaulAdmin");

app.MapGet("/api/linehaul/statistics", async (
    DateOnly? from,
    DateOnly? to,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var start = from ?? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    var end = to ?? DateOnly.FromDateTime(DateTime.Today);
    if (end < start) return Results.BadRequest(new { message = "To date cannot be before From date." });

    var receipts = await db.LinehaulReceipts.AsNoTracking()
        .Where(x => x.Status == ReceiptStatus.Active &&
                    (x.FromTerminalId == terminalId || x.ToTerminalId == terminalId) &&
                    x.BusinessDate >= start && x.BusinessDate <= end)
        .ToListAsync();
    var terminals = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
    var counterpartIds = receipts.Select(x => x.FromTerminalId == terminalId ? x.ToTerminalId : x.FromTerminalId).Distinct().ToList();
    var rows = counterpartIds.Select(otherId =>
    {
        var sentRows = receipts.Where(x => x.FromTerminalId == terminalId && x.ToTerminalId == otherId).ToList();
        var receivedRows = receipts.Where(x => x.ToTerminalId == terminalId && x.FromTerminalId == otherId).ToList();
        var sent = sentRows.Sum(x => x.PalletCount);
        var received = receivedRows.Sum(x => x.PalletCount);
        return new
        {
            terminalId = otherId,
            terminal = terminals.GetValueOrDefault(otherId, $"#{otherId}"),
            sentLoads = sentRows.Count,
            receivedLoads = receivedRows.Count,
            sentPallets = sent,
            receivedPallets = received,
            balance = sent - received
        };
    }).OrderByDescending(x => Math.Abs(x.balance)).ThenBy(x => x.terminal).ToList();

    return Results.Ok(new
    {
        from = start, to = end,
        terminalId,
        terminalCode = terminals.GetValueOrDefault(terminalId, ""),
        totalSentLoads = receipts.Count(x => x.FromTerminalId == terminalId),
        totalReceivedLoads = receipts.Count(x => x.ToTerminalId == terminalId),
        totalSentPallets = receipts.Where(x => x.FromTerminalId == terminalId).Sum(x => x.PalletCount),
        totalReceivedPallets = receipts.Where(x => x.ToTerminalId == terminalId).Sum(x => x.PalletCount),
        globalBalance = receipts.Where(x => x.FromTerminalId == terminalId).Sum(x => x.PalletCount) - receipts.Where(x => x.ToTerminalId == terminalId).Sum(x => x.PalletCount),
        rows
    });
}).RequireAuthorization("LinehaulModule");

app.MapGet("/api/linehaul/export", async (
    DateOnly from,
    DateOnly to,
    string? type,
    string? format,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (to < from) return Results.BadRequest(new { message = "To date cannot be before From date." });
    var terminalId = TerminalId(principal);
    var terminalCode = principal.FindFirstValue("terminalCode") ?? "TERM";
    var receipts = await db.LinehaulReceipts.AsNoTracking()
        .Where(x => (x.FromTerminalId == terminalId || x.ToTerminalId == terminalId || x.OwnerTerminalId == terminalId) && x.BusinessDate >= from && x.BusinessDate <= to)
        .OrderBy(x => x.BusinessDate).ThenBy(x => x.SubmittedAtUtc).ToListAsync();
    var activeReceipts = receipts.Where(x => x.Status == ReceiptStatus.Active && (x.FromTerminalId == terminalId || x.ToTerminalId == terminalId)).ToList();
    var terminalCodes = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);

    var details = new ExportTable("Linehaul receipts",
        ["Receipt ID", "Date", "Container/Trailer", "Pallet Receipt Number", "From", "To", "Pallets", "Standard comment", "Free comment", "Status", "Cancelled UTC", "Cancel reason", "Submitted UTC"], []);
    foreach (var x in receipts)
        details.Rows.Add([x.ReceiptNumber, x.BusinessDate, x.UnitReference, x.PalletReceiptNumber,
            terminalCodes.GetValueOrDefault(x.FromTerminalId, x.FromTerminalSnapshot), terminalCodes.GetValueOrDefault(x.ToTerminalId, x.ToTerminalSnapshot),
            x.PalletCount, x.CommentOptionSnapshot, x.FreeComment, x.Status, x.CancelledAtUtc, x.CancelReason, x.SubmittedAtUtc]);
    var summary = new ExportTable("Linehaul summary",
        ["Other terminal", "Sent loads", "Received loads", "Sent pallets", "Received pallets", "Balance"], []);
    var counterpartIds = activeReceipts.Select(x => x.FromTerminalId == terminalId ? x.ToTerminalId : x.FromTerminalId).Distinct().OrderBy(x => terminalCodes.GetValueOrDefault(x, "")).ToList();
    foreach (var otherId in counterpartIds)
    {
        var sentRows = activeReceipts.Where(x => x.FromTerminalId == terminalId && x.ToTerminalId == otherId).ToList();
        var receivedRows = activeReceipts.Where(x => x.ToTerminalId == terminalId && x.FromTerminalId == otherId).ToList();
        var sent = sentRows.Sum(x => x.PalletCount); var received = receivedRows.Sum(x => x.PalletCount);
        summary.Rows.Add([terminalCodes.GetValueOrDefault(otherId, $"#{otherId}"), sentRows.Count, receivedRows.Count, sent, received, sent - received]);
    }

    var exportType = (type ?? "receipts").Trim().ToLowerInvariant();
    var exportFormat = (format ?? "xlsx").Trim().ToLowerInvariant();
    var selected = exportType == "summary" ? summary : details;
    byte[] bytes; string contentType; string extension;
    if (exportFormat == "csv")
    {
        bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ExportCsv(selected))).ToArray();
        contentType = "text/csv; charset=utf-8"; extension = "csv";
    }
    else
    {
        bytes = ExportWorkbook(exportType == "complete" ? [details, summary] : [selected]);
        contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; extension = "xlsx";
    }
    await Audit(db, principal, "LINEHAUL_EXPORT", $"Exported {exportType}/{exportFormat} {from:yyyy-MM-dd}-{to:yyyy-MM-dd}");
    return Results.File(bytes, contentType, $"Linehaul_{terminalCode}_{exportType}_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.{extension}");
}).RequireAuthorization("LinehaulModule");

app.MapGet("/api/linehaul/import-template", (string? format) =>
{
    var exportFormat = (format ?? "xlsx").Trim().ToLowerInvariant();
    var headers = new List<string>
    {
        "Date", "ContainerTrailer", "PalletReceiptNumber", "Pallets", "FromTerminal", "ToTerminal", "StandardComment", "Comment"
    };

    if (exportFormat == "csv")
    {
        var csv = string.Join(",", headers.Select(Csv)) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", "Linehaul_Import_Template.csv");
    }

    var data = new ExportTable("Linehaul Import", headers, []);
    var instructions = new ExportTable("Instructions", ["Column", "Required", "Description", "Example"], [
        ["Date", "Yes", "Business date. Recommended format YYYY-MM-DD.", "2026-08-28"],
        ["ContainerTrailer", "No", "Optional container/trailer number or other reference text.", "TTR12345"],
        ["PalletReceiptNumber", "New data: Yes / legacy import: optional", "Pallekvitteringsnummer. Blank is accepted only to make old historical data importable.", "PK-123456"],
        ["Pallets", "Yes", "Whole number of pallets, 0-10000.", "33"],
        ["FromTerminal", "At least From or To", "Existing terminal Code, Name or Alias. If blank/missing, your current terminal is inferred.", "SRD / SRD123 / Sandefjord"],
        ["ToTerminal", "At least From or To", "Existing terminal Code, Name or Alias. If blank/missing, your current terminal is inferred.", "ARE"],
        ["StandardComment", "No", "Historical selectable comment text. Does not need to exist as a current option.", "Loaded by night shift"],
        ["Comment", "No", "Free-text comment.", "Old Excel import"]
    ]);
    return Results.File(ExportWorkbook([data, instructions]),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Linehaul_Import_Template.xlsx");
}).RequireAuthorization("LinehaulAdmin");

app.MapPost("/api/linehaul/import", async (
    HttpRequest request,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Upload an .xlsx or .csv file using multipart/form-data." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "Choose a non-empty .xlsx or .csv file." });
    var confirmImport = bool.TryParse(form["confirm"].FirstOrDefault(), out var parsedConfirm) && parsedConfirm;

    ImportGrid grid;
    try { grid = await ReadImportGrid(file); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"Could not read import file: {ex.Message}" }); }

    var missingHeaders = new List<string>();
    if (!HasImportHeader(grid, "Date", "Dato")) missingHeaders.Add("Date");
    if (!HasImportHeader(grid, "Pallets", "PalletCount", "Paller", "AntallPaller")) missingHeaders.Add("Pallets");
    var hasFromTerminalHeader = HasImportHeader(grid, "FromTerminal", "FraTerminal", "From", "Fra");
    var hasToTerminalHeader = HasImportHeader(grid, "ToTerminal", "TilTerminal", "To", "Til");
    if (!hasFromTerminalHeader && !hasToTerminalHeader) missingHeaders.Add("FromTerminal or ToTerminal");
    if (missingHeaders.Count > 0)
        return Results.BadRequest(new { message = $"Missing required column(s): {string.Join(", ", missingHeaders)}." });

    var terminalId = TerminalId(principal);
    var userId = UserId(principal);
    var terminalRows = await db.Terminals.AsNoTracking().ToListAsync();
    var terminalLookup = BuildTerminalLookup(terminalRows);
    var ownerTerminal = terminalRows.FirstOrDefault(x => x.Id == terminalId);
    if (ownerTerminal is null) return Results.BadRequest(new { message = "Your assigned terminal no longer exists." });

    var issues = new List<ImportIssue>();
    var warnings = new List<ImportIssue>();
    var pending = new List<PendingLinehaulImport>();
    var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var sourceRow in grid.Rows)
    {
        var dateText = ImportValue(sourceRow, "Date", "Dato");
        var reference = ImportValue(sourceRow, "ContainerTrailer", "ContainerTrailerNo", "UnitReference", "Container", "Trailer", "Tralle").Trim();
        var palletReceiptNumber = ImportValue(sourceRow, "PalletReceiptNumber", "Pallekvitteringsnummer", "PallekvitteringNr", "PalletReceiptNr", "Kvitteringsnummer").Trim();
        var palletsText = ImportValue(sourceRow, "Pallets", "PalletCount", "Paller", "AntallPaller");
        var fromCode = ImportValue(sourceRow, "FromTerminal", "FraTerminal", "From", "Fra").Trim();
        var toCode = ImportValue(sourceRow, "ToTerminal", "TilTerminal", "To", "Til").Trim();
        var standardComment = ImportValue(sourceRow, "StandardComment", "SelectableComment", "StandardKommentar").Trim();
        var freeComment = ImportValue(sourceRow, "Comment", "FreeComment", "Kommentar").Trim();

        var rowErrors = new List<string>();
        if (!TryParseImportDate(dateText, out var businessDate)) rowErrors.Add("invalid Date");
        if (reference.Length > 120) rowErrors.Add("ContainerTrailer exceeds 120 characters");
        if (!TryParseImportInt(palletsText, out var palletCount) || palletCount < 0 || palletCount > 10000) rowErrors.Add("Pallets must be a whole number from 0 to 10000");

        var fromTerminal = string.IsNullOrWhiteSpace(fromCode) ? null : ResolveImportTerminal(fromCode, terminalLookup, terminalRows);
        var toTerminal = string.IsNullOrWhiteSpace(toCode) ? null : ResolveImportTerminal(toCode, terminalLookup, terminalRows);
        if (!string.IsNullOrWhiteSpace(fromCode) && fromTerminal is null) rowErrors.Add($"unknown FromTerminal '{fromCode}'");
        if (!string.IsNullOrWhiteSpace(toCode) && toTerminal is null) rowErrors.Add($"unknown ToTerminal '{toCode}'");
        // Historical files often omit the local side because it was implicit in that terminal's spreadsheet.
        if (fromTerminal is null && string.IsNullOrWhiteSpace(fromCode) && toTerminal is not null) fromTerminal = ownerTerminal;
        if (toTerminal is null && string.IsNullOrWhiteSpace(toCode) && fromTerminal is not null) toTerminal = ownerTerminal;
        if (fromTerminal is null && toTerminal is null && string.IsNullOrWhiteSpace(fromCode) && string.IsNullOrWhiteSpace(toCode))
            rowErrors.Add("enter at least FromTerminal or ToTerminal");
        if (fromTerminal != null && toTerminal != null && fromTerminal.Id == toTerminal.Id) rowErrors.Add("FromTerminal and ToTerminal must be different");
        // Imports may contain historical rows from the wider terminal network. Both terminals only need
        // to resolve to entries in the PalletControl terminal master list; manual registration remains local.
        if (palletReceiptNumber.Length > 120) rowErrors.Add("PalletReceiptNumber exceeds 120 characters");
        if (standardComment.Length > 500) rowErrors.Add("StandardComment exceeds 500 characters");
        if (freeComment.Length > 2000) rowErrors.Add("Comment exceeds 2000 characters");

        if (rowErrors.Count > 0)
        {
            issues.Add(new ImportIssue(sourceRow.RowNumber, string.Join("; ", rowErrors)));
            continue;
        }

        if (string.IsNullOrWhiteSpace(palletReceiptNumber))
            warnings.Add(new ImportIssue(sourceRow.RowNumber, "PalletReceiptNumber is blank. Imported as legacy history; new manual registrations require a unique number."));

        var key = LinehaulImportKey(businessDate, fromTerminal!.Id, toTerminal!.Id, reference, palletReceiptNumber, palletCount);
        if (!fileKeys.Add(key))
        {
            issues.Add(new ImportIssue(sourceRow.RowNumber, "duplicate row inside the import file"));
            continue;
        }
        pending.Add(new PendingLinehaulImport(sourceRow.RowNumber, businessDate, reference, palletReceiptNumber, palletCount,
            fromTerminal.Id, toTerminal.Id, fromTerminal.Code, toTerminal.Code, standardComment, freeComment, key));
    }

    // Non-blank pallet receipt numbers are globally unique across Linehaul, including cancelled records.
    var palletNumberRows = pending.Where(x => !string.IsNullOrWhiteSpace(x.PalletReceiptNumber)).ToList();
    var duplicatePalletNumbersInFile = palletNumberRows
        .GroupBy(x => x.PalletReceiptNumber.Trim(), StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .SelectMany(g => g.Skip(1))
        .ToHashSet();
    if (duplicatePalletNumbersInFile.Count > 0)
    {
        foreach (var duplicate in duplicatePalletNumbersInFile)
            issues.Add(new ImportIssue(duplicate.RowNumber, $"PalletReceiptNumber '{duplicate.PalletReceiptNumber}' appears more than once in the import file"));
        pending = pending.Where(x => !duplicatePalletNumbersInFile.Contains(x)).ToList();
    }

    if (palletNumberRows.Count > 0)
    {
        var existingPalletNumbers = (await db.LinehaulReceipts.AsNoTracking()
                .Where(x => x.PalletReceiptNumber != "")
                .Select(x => x.PalletReceiptNumber)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = pending.Where(x => !string.IsNullOrWhiteSpace(x.PalletReceiptNumber) && existingPalletNumbers.Contains(x.PalletReceiptNumber.Trim())).ToList();
        foreach (var conflict in conflicts)
            issues.Add(new ImportIssue(conflict.RowNumber, $"PalletReceiptNumber '{conflict.PalletReceiptNumber}' already exists in the database"));
        if (conflicts.Count > 0)
        {
            var conflictRows = conflicts.Select(x => x.RowNumber).ToHashSet();
            pending = pending.Where(x => !conflictRows.Contains(x.RowNumber)).ToList();
        }
    }

    var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (pending.Count > 0)
    {
        var minDate = pending.Min(x => x.BusinessDate);
        var maxDate = pending.Max(x => x.BusinessDate);
        var existing = await db.LinehaulReceipts.AsNoTracking()
            .Where(x => x.BusinessDate >= minDate && x.BusinessDate <= maxDate)
            .Select(x => new { x.BusinessDate, x.FromTerminalId, x.ToTerminalId, x.UnitReference, x.PalletReceiptNumber, x.PalletCount })
            .ToListAsync();
        foreach (var x in existing)
            existingKeys.Add(LinehaulImportKey(x.BusinessDate, x.FromTerminalId, x.ToTerminalId, x.UnitReference, x.PalletReceiptNumber, x.PalletCount));
    }

    var ready = new List<PendingLinehaulImport>();
    var skippedDuplicates = 0;
    foreach (var p in pending)
    {
        if (existingKeys.Contains(p.DuplicateKey))
        {
            skippedDuplicates++;
            issues.Add(new ImportIssue(p.RowNumber, "matching Linehaul record already exists; skipped"));
            continue;
        }
        ready.Add(p);
        existingKeys.Add(p.DuplicateKey);
    }

    var previewRows = ready.OrderBy(x => x.RowNumber).Take(500).Select(x => new
    {
        row = x.RowNumber, date = x.BusinessDate, containerTrailer = x.UnitReference, palletReceiptNumber = x.PalletReceiptNumber,
        pallets = x.PalletCount, fromTerminal = x.FromTerminalCode, toTerminal = x.ToTerminalCode,
        standardComment = x.StandardComment, comment = x.FreeComment
    }).ToList();

    if (!confirmImport)
    {
        return Results.Ok(new
        {
            preview = true, file = file.FileName, rowsRead = grid.Rows.Count, readyToImport = ready.Count, imported = 0, skippedDuplicates,
            rejected = issues.Count, previewRows, previewRowsTruncated = ready.Count > 500,
            warnings = warnings.Take(200).ToList(), issues = issues.Take(200).ToList(), issueListTruncated = issues.Count > 200
        });
    }

    var now = DateTime.UtcNow;
    var importedRows = new List<LinehaulReceipt>();
    foreach (var p in ready)
    {
        var entity = new LinehaulReceipt
        {
            ReceiptNumber = $"LH-{ownerTerminal.Code}-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            OwnerTerminalId = terminalId,
            FromTerminalId = p.FromTerminalId,
            ToTerminalId = p.ToTerminalId,
            FromTerminalSnapshot = p.FromTerminalCode,
            ToTerminalSnapshot = p.ToTerminalCode,
            UnitReference = p.UnitReference,
            PalletReceiptNumber = p.PalletReceiptNumber,
            PalletCount = p.PalletCount,
            CommentOptionSnapshot = p.StandardComment,
            FreeComment = p.FreeComment,
            BusinessDate = p.BusinessDate,
            SubmittedAtUtc = now,
            SubmittedByUserId = userId,
            Status = ReceiptStatus.Active
        };
        importedRows.Add(entity);
    }

    if (importedRows.Count > 0)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.LinehaulReceipts.AddRange(importedRows);
        await db.SaveChangesAsync();
        await Audit(db, principal, "LINEHAUL_IMPORT", $"Imported {importedRows.Count} historical Linehaul rows for terminal {ownerTerminal.Code} from {file.FileName}");
        await tx.CommitAsync();
    }

    return Results.Ok(new
    {
        preview = false, file = file.FileName, rowsRead = grid.Rows.Count, imported = importedRows.Count, skippedDuplicates, rejected = issues.Count,
        importedRows = importedRows.OrderBy(x => x.BusinessDate).ThenBy(x => x.ReceiptNumber).Take(500).Select(x => new
        {
            x.ReceiptNumber, date = x.BusinessDate, containerTrailer = x.UnitReference, x.PalletReceiptNumber, pallets = x.PalletCount,
            fromTerminal = x.FromTerminalSnapshot, toTerminal = x.ToTerminalSnapshot, standardComment = x.CommentOptionSnapshot, comment = x.FreeComment
        }).ToList(),
        importedRowsTruncated = importedRows.Count > 500, warnings = warnings.Take(200).ToList(), issues = issues.Take(200).ToList(), issueListTruncated = issues.Count > 200
    });
}).RequireAuthorization("LinehaulAdmin");

// ---------------- RECEIVED PALLET CONTROL ----------------

app.MapGet("/api/received-control/setup", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var terminals = await db.Terminals.AsNoTracking()
        .Where(x => x.Active)
        .OrderBy(x => x.Code)
        .Select(x => new { x.Id, x.Code, x.Name })
        .ToListAsync();
    return Results.Ok(new { terminalId, terminalCode = principal.FindFirstValue("terminalCode") ?? "", terminals });
}).RequireAuthorization("ReceivedControlModule");

app.MapPost("/api/received-control/entries", async (
    CreateReceivedControlRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var reference = (req.UnitReference ?? "").Trim();
    var comment = (req.Comment ?? "").Trim();
    if (reference.Length > 120) return Results.BadRequest(new { message = "Container/trailer text is too long." });
    if (comment.Length > 2000) return Results.BadRequest(new { message = "Comment is too long." });
    if (req.ActualPalletCount < 0 || req.ActualPalletCount > 10000) return Results.BadRequest(new { message = "Actual pallet count must be between 0 and 10000." });
    if (req.PalletReceiptReceived && (!req.ReceiptPalletCount.HasValue || req.ReceiptPalletCount.Value < 0 || req.ReceiptPalletCount.Value > 10000))
        return Results.BadRequest(new { message = "Enter the pallet quantity written on the received pallet receipt." });

    var fromTerminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.FromTerminalId && x.Active);
    if (fromTerminal is null) return Results.BadRequest(new { message = "From terminal was not found or is inactive." });
    if (fromTerminal.Id == terminalId) return Results.BadRequest(new { message = "From terminal must be different from your receiving terminal." });

    var receiptQty = req.PalletReceiptReceived ? req.ReceiptPalletCount : null;
    var status = ReceivedControlStatus.Resolve(req.PalletReceiptReceived, receiptQty, req.ActualPalletCount);
    var now = DateTime.UtcNow;
    var businessDate = req.BusinessDate ?? DateOnly.FromDateTime(DateTime.Today);
    var terminalCode = principal.FindFirstValue("terminalCode") ?? "TERM";
    var row = new ReceivedControlEntry
    {
        ControlNumber = $"RC-{terminalCode}-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        TerminalId = terminalId,
        FromTerminalId = fromTerminal.Id,
        FromTerminalSnapshot = fromTerminal.Code,
        UnitReference = reference,
        Comment = comment,
        PalletReceiptReceived = req.PalletReceiptReceived,
        ReceiptPalletCount = receiptQty,
        ActualPalletCount = req.ActualPalletCount,
        Result = status,
        BusinessDate = businessDate,
        SubmittedAtUtc = now,
        SubmittedByUserId = UserId(principal),
        Status = ReceiptStatus.Active
    };
    db.ReceivedControlEntries.Add(row);
    await db.SaveChangesAsync();

    if (status == ReceivedControlStatus.ReceiptHigher)
    {
        var difference = receiptQty!.Value - req.ActualPalletCount;
        db.ReceivedControlWarnings.Add(new ReceivedControlWarning
        {
            TerminalId = terminalId,
            EntryId = row.Id,
            Message = $"From {fromTerminal.Code}{(string.IsNullOrWhiteSpace(reference) ? "" : $" · {reference}")}: pallet receipt says {receiptQty.Value}, but {req.ActualPalletCount} pallets were actually received. Shortage: {difference}.",
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync();
    }
    await Audit(db, principal, "RECEIVED_CONTROL_CREATE", $"{row.ControlNumber}: from {fromTerminal.Code}, {reference}, {status}");
    return Results.Ok(ToReceivedControlDto(row));
}).RequireAuthorization("ReceivedControlModule");

app.MapPost("/api/received-control/entries/{id:int}/cancel", async (
    int id,
    CancelRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.ReceivedControlEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound();
    var terminalId = TerminalId(principal);
    if (!IsSuperAdmin(principal) && (!IsTerminalAdmin(principal) || row.TerminalId != terminalId))
        return Results.Forbid();
    if (row.Status == ReceiptStatus.Cancelled)
        return Results.BadRequest(new { message = "MottattKontroll entry is already cancelled." });

    var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Cancelled by administrator" : req.Reason.Trim();
    row.Status = ReceiptStatus.Cancelled;
    row.CancelledAtUtc = DateTime.UtcNow;
    row.CancelledByUserId = UserId(principal);
    row.CancelReason = reason;
    await db.SaveChangesAsync();
    await Audit(db, principal, "RECEIVED_CONTROL_CANCEL", $"Cancelled {row.ControlNumber}: {reason}");
    return Results.Ok(new { row.Id, row.Status, row.CancelledAtUtc, row.CancelReason });
}).RequireAuthorization("ReceivedControlAdmin");

app.MapDelete("/api/received-control/entries/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.ReceivedControlEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound();
    var terminalId = TerminalId(principal);
    if (!IsSuperAdmin(principal) && (!IsTerminalAdmin(principal) || row.TerminalId != terminalId))
        return Results.Forbid();

    var warnings = await db.ReceivedControlWarnings.Where(x => x.EntryId == id).ToListAsync();
    if (warnings.Count > 0) db.ReceivedControlWarnings.RemoveRange(warnings);
    var controlNumber = row.ControlNumber;
    db.ReceivedControlEntries.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "RECEIVED_CONTROL_DELETE_PERMANENT", $"Permanently deleted {controlNumber}");
    return Results.Ok();
}).RequireAuthorization("ReceivedControlAdmin");

app.MapGet("/api/received-control/statistics", async (
    DateOnly? from,
    DateOnly? to,
    string? status,
    string? recordStatus,
    string? search,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var start = from ?? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    var end = to ?? DateOnly.FromDateTime(DateTime.Today);
    if (end < start) return Results.BadRequest(new { message = "To date cannot be before From date." });
    var baseQuery = db.ReceivedControlEntries.AsNoTracking()
        .Where(x => x.TerminalId == terminalId && x.BusinessDate >= start && x.BusinessDate <= end);
    var activeRowsForTotals = await baseQuery.Where(x => x.Status == ReceiptStatus.Active).ToListAsync();

    var q = baseQuery;
    var normalizedStatus = (status ?? "all").Trim().ToUpperInvariant();
    if (normalizedStatus != "ALL") q = q.Where(x => x.Result == normalizedStatus);
    var normalizedRecordStatus = (recordStatus ?? "all").Trim().ToLowerInvariant();
    if (normalizedRecordStatus == "active") q = q.Where(x => x.Status == ReceiptStatus.Active);
    if (normalizedRecordStatus == "cancelled") q = q.Where(x => x.Status == ReceiptStatus.Cancelled);
    var term = search?.Trim();
    if (!string.IsNullOrWhiteSpace(term)) q = q.Where(x => x.UnitReference.Contains(term) || x.Comment.Contains(term) || x.FromTerminalSnapshot.Contains(term) || x.ControlNumber.Contains(term));
    var rows = await q.OrderByDescending(x => x.BusinessDate).ThenByDescending(x => x.SubmittedAtUtc).Take(5000).ToListAsync();
    var userIds = rows.Select(x => x.SubmittedByUserId)
        .Concat(rows.Where(x => x.CancelledByUserId.HasValue).Select(x => x.CancelledByUserId!.Value))
        .Distinct().ToList();
    var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName);
    var terminalCodes = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
    var canManage = IsSuperAdmin(principal) || IsTerminalAdmin(principal) || Role(principal) == Roles.LegacyAdmin;
    return Results.Ok(new
    {
        from = start, to = end,
        total = activeRowsForTotals.Count,
        noReceipt = activeRowsForTotals.Count(x => x.Result == ReceivedControlStatus.NoReceipt),
        receiptHigher = activeRowsForTotals.Count(x => x.Result == ReceivedControlStatus.ReceiptHigher),
        receiptLower = activeRowsForTotals.Count(x => x.Result == ReceivedControlStatus.ReceiptLower),
        exact = activeRowsForTotals.Count(x => x.Result == ReceivedControlStatus.Exact),
        totalShortage = activeRowsForTotals.Where(x => x.Result == ReceivedControlStatus.ReceiptHigher).Sum(x => (x.ReceiptPalletCount ?? 0) - x.ActualPalletCount),
        totalExcess = activeRowsForTotals.Where(x => x.Result == ReceivedControlStatus.ReceiptLower).Sum(x => x.ActualPalletCount - (x.ReceiptPalletCount ?? 0)),
        rows = rows.Select(x => new
        {
            x.Id, x.ControlNumber, x.BusinessDate, x.SubmittedAtUtc, x.UnitReference, x.Comment, x.FromTerminalId,
            fromTerminal = terminalCodes.GetValueOrDefault(x.FromTerminalId, x.FromTerminalSnapshot),
            x.PalletReceiptReceived, x.ReceiptPalletCount, x.ActualPalletCount, x.Result, x.Status, x.CancelledAtUtc, x.CancelReason,
            cancelledBy = x.CancelledByUserId.HasValue ? users.GetValueOrDefault(x.CancelledByUserId.Value, "Unknown") : null,
            difference = x.PalletReceiptReceived ? x.ActualPalletCount - (x.ReceiptPalletCount ?? 0) : (int?)null,
            submittedBy = users.GetValueOrDefault(x.SubmittedByUserId, "Unknown"),
            canManage
        })
    });
}).RequireAuthorization("ReceivedControlModule");

app.MapGet("/api/received-control/warnings", async (
    bool? unacknowledgedOnly,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var activeEntryIds = db.ReceivedControlEntries.AsNoTracking()
        .Where(x => x.TerminalId == terminalId && x.Status == ReceiptStatus.Active)
        .Select(x => x.Id);
    var q = db.ReceivedControlWarnings.AsNoTracking()
        .Where(x => x.TerminalId == terminalId && activeEntryIds.Contains(x.EntryId));
    if (unacknowledgedOnly == true) q = q.Where(x => x.AcknowledgedAtUtc == null);
    var warnings = await q.OrderByDescending(x => x.CreatedAtUtc).Take(2000).ToListAsync();
    var entryIds = warnings.Select(x => x.EntryId).Distinct().ToList();
    var entries = await db.ReceivedControlEntries.AsNoTracking().Where(x => entryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
    var terminalCodes = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
    var ackIds = warnings.Where(x => x.AcknowledgedByUserId.HasValue).Select(x => x.AcknowledgedByUserId!.Value).Distinct().ToList();
    var users = await db.Users.AsNoTracking().Where(x => ackIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName);
    return Results.Ok(new
    {
        warnings = warnings.Select(x => new
        {
            x.Id, x.Message, x.CreatedAtUtc, x.AcknowledgedAtUtc,
            acknowledgedBy = x.AcknowledgedByUserId.HasValue ? users.GetValueOrDefault(x.AcknowledgedByUserId.Value, "Unknown") : null,
            entry = entries.TryGetValue(x.EntryId, out var e) ? new { e.ControlNumber, e.UnitReference, e.BusinessDate, e.FromTerminalId, fromTerminal = terminalCodes.GetValueOrDefault(e.FromTerminalId, e.FromTerminalSnapshot), e.Comment, e.ReceiptPalletCount, e.ActualPalletCount } : null
        })
    });
}).RequireAuthorization("ReceivedControlModule");

app.MapPost("/api/received-control/warnings/{id:int}/acknowledge", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var warning = await db.ReceivedControlWarnings.FirstOrDefaultAsync(x => x.Id == id && x.TerminalId == terminalId);
    if (warning is null) return Results.NotFound();
    if (warning.AcknowledgedAtUtc == null)
    {
        warning.AcknowledgedAtUtc = DateTime.UtcNow;
        warning.AcknowledgedByUserId = UserId(principal);
        await db.SaveChangesAsync();
        await Audit(db, principal, "RECEIVED_CONTROL_WARNING_ACK", $"Acknowledged received-control warning #{warning.Id}");
    }
    return Results.Ok();
}).RequireAuthorization("ReceivedControlModule");

app.MapGet("/api/received-control/export", async (
    DateOnly from,
    DateOnly to,
    string? format,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (to < from) return Results.BadRequest(new { message = "To date cannot be before From date." });
    var terminalId = TerminalId(principal);
    var terminalCode = principal.FindFirstValue("terminalCode") ?? "TERM";
    var rows = await db.ReceivedControlEntries.AsNoTracking()
        .Where(x => x.TerminalId == terminalId && x.BusinessDate >= from && x.BusinessDate <= to)
        .OrderBy(x => x.BusinessDate).ThenBy(x => x.SubmittedAtUtc).ToListAsync();
    var activeRows = rows.Where(x => x.Status == ReceiptStatus.Active).ToList();
    var terminalCodes = await db.Terminals.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
    var details = new ExportTable("Received control",
        ["Control ID", "Date", "From terminal", "Container/Trailer", "Comment", "Pallet receipt received", "Receipt pallets", "Actual pallets", "Difference actual-receipt", "Result", "Status", "Cancelled UTC", "Cancel reason", "Submitted UTC"], []);
    foreach (var x in rows)
        details.Rows.Add([x.ControlNumber, x.BusinessDate, terminalCodes.GetValueOrDefault(x.FromTerminalId, x.FromTerminalSnapshot), x.UnitReference, x.Comment, x.PalletReceiptReceived ? "Yes" : "No", x.ReceiptPalletCount, x.ActualPalletCount, x.PalletReceiptReceived ? x.ActualPalletCount - (x.ReceiptPalletCount ?? 0) : null, x.Result, x.Status, x.CancelledAtUtc, x.CancelReason, x.SubmittedAtUtc]);
    var summary = new ExportTable("Received summary", ["Metric", "Value"], [
        ["Active controls", activeRows.Count],
        ["Cancelled controls", rows.Count(x => x.Status == ReceiptStatus.Cancelled)],
        ["No pallet receipt", activeRows.Count(x => x.Result == ReceivedControlStatus.NoReceipt)],
        ["Receipt higher than actual (red)", activeRows.Count(x => x.Result == ReceivedControlStatus.ReceiptHigher)],
        ["Receipt lower than actual (orange)", activeRows.Count(x => x.Result == ReceivedControlStatus.ReceiptLower)],
        ["Exact (green)", activeRows.Count(x => x.Result == ReceivedControlStatus.Exact)],
        ["Total shortage pallets", activeRows.Where(x => x.Result == ReceivedControlStatus.ReceiptHigher).Sum(x => (x.ReceiptPalletCount ?? 0) - x.ActualPalletCount)],
        ["Total excess pallets", activeRows.Where(x => x.Result == ReceivedControlStatus.ReceiptLower).Sum(x => x.ActualPalletCount - (x.ReceiptPalletCount ?? 0))]
    ]);
    var exportFormat = (format ?? "xlsx").Trim().ToLowerInvariant();
    byte[] bytes; string contentType; string extension;
    if (exportFormat == "csv")
    {
        bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ExportCsv(details))).ToArray();
        contentType = "text/csv; charset=utf-8"; extension = "csv";
    }
    else
    {
        bytes = ExportWorkbook([details, summary]);
        contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; extension = "xlsx";
    }
    await Audit(db, principal, "RECEIVED_CONTROL_EXPORT", $"Exported received control {from:yyyy-MM-dd}-{to:yyyy-MM-dd}");
    return Results.File(bytes, contentType, $"ReceivedControl_{terminalCode}_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.{extension}");
}).RequireAuthorization("ReceivedControlModule");

app.MapGet("/api/received-control/import-template", (string? format) =>
{
    var exportFormat = (format ?? "xlsx").Trim().ToLowerInvariant();
    var headers = new List<string> { "Date", "FromTerminal", "ContainerTrailer", "PalletReceiptReceived", "ReceiptPallets", "ActualPallets", "Comment" };
    if (exportFormat == "csv")
    {
        var csv = string.Join(",", headers.Select(Csv)) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", "MottattKontroll_Import_Template.csv");
    }

    var data = new ExportTable("MottattKontroll Import", headers, []);
    var instructions = new ExportTable("Instructions", ["Column", "Required", "Description", "Example"], [
        ["Date", "Yes", "Business date. Recommended format YYYY-MM-DD.", "2026-08-28"],
        ["FromTerminal", "Yes", "Existing terminal Code, Name or Alias. SRD, SRD123 and Sandefjord can all resolve to the same terminal when configured.", "ARE"],
        ["ContainerTrailer", "Yes", "Container/trailer number or reference.", "TTR12345"],
        ["PalletReceiptReceived", "Yes", "Yes/No. Ja/Nei, true/false and 1/0 are also accepted.", "Yes"],
        ["ReceiptPallets", "When receipt received", "Pallet quantity written on the pallet receipt. Leave blank when PalletReceiptReceived=No.", "33"],
        ["ActualPallets", "Yes", "Actual pallets physically received, 0-10000.", "31"],
        ["Comment", "No", "Optional free-text comment.", "Seal damaged on arrival"]
    ]);
    return Results.File(ExportWorkbook([data, instructions]),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "MottattKontroll_Import_Template.xlsx");
}).RequireAuthorization("ReceivedControlAdmin");

app.MapPost("/api/received-control/import", async (
    HttpRequest request,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Upload an .xlsx or .csv file using multipart/form-data." });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "Choose a non-empty .xlsx or .csv file." });
    var confirmImport = bool.TryParse(form["confirm"].FirstOrDefault(), out var parsedConfirm) && parsedConfirm;

    ImportGrid grid;
    try { grid = await ReadImportGrid(file); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"Could not read import file: {ex.Message}" }); }

    var missingHeaders = new List<string>();
    if (!HasImportHeader(grid, "Date", "Dato")) missingHeaders.Add("Date");
    if (!HasImportHeader(grid, "FromTerminal", "FraTerminal", "From", "Fra")) missingHeaders.Add("FromTerminal");
    if (!HasImportHeader(grid, "ContainerTrailer", "ContainerTrailerNo", "UnitReference", "Container", "Trailer", "Tralle")) missingHeaders.Add("ContainerTrailer");
    if (!HasImportHeader(grid, "PalletReceiptReceived", "PallekvitteringReceived", "PallekvitteringMottatt", "ReceiptReceived")) missingHeaders.Add("PalletReceiptReceived");
    if (!HasImportHeader(grid, "ReceiptPallets", "PalletReceiptPallets", "KvitteringPaller", "PallekvitteringAntall")) missingHeaders.Add("ReceiptPallets");
    if (!HasImportHeader(grid, "ActualPallets", "ActualPalletCount", "FaktiskePaller", "ReeltAntall")) missingHeaders.Add("ActualPallets");
    if (missingHeaders.Count > 0)
        return Results.BadRequest(new { message = $"Missing required column(s): {string.Join(", ", missingHeaders)}." });

    var terminalId = TerminalId(principal);
    var userId = UserId(principal);
    var terminalRows = await db.Terminals.AsNoTracking().ToListAsync();
    var terminalLookup = BuildTerminalLookup(terminalRows);
    var terminal = terminalRows.FirstOrDefault(x => x.Id == terminalId);
    if (terminal is null) return Results.BadRequest(new { message = "Your assigned terminal no longer exists." });

    var issues = new List<ImportIssue>();
    var pending = new List<PendingReceivedControlImport>();
    var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var sourceRow in grid.Rows)
    {
        var dateText = ImportValue(sourceRow, "Date", "Dato");
        var fromCode = ImportValue(sourceRow, "FromTerminal", "FraTerminal", "From", "Fra").Trim();
        var reference = ImportValue(sourceRow, "ContainerTrailer", "ContainerTrailerNo", "UnitReference", "Container", "Trailer", "Tralle").Trim();
        var receivedText = ImportValue(sourceRow, "PalletReceiptReceived", "PallekvitteringReceived", "PallekvitteringMottatt", "ReceiptReceived");
        var receiptPalletsText = ImportValue(sourceRow, "ReceiptPallets", "PalletReceiptPallets", "KvitteringPaller", "PallekvitteringAntall");
        var actualText = ImportValue(sourceRow, "ActualPallets", "ActualPalletCount", "FaktiskePaller", "ReeltAntall");
        var comment = ImportValue(sourceRow, "Comment", "FreeComment", "Kommentar").Trim();

        var rowErrors = new List<string>();
        if (!TryParseImportDate(dateText, out var businessDate)) rowErrors.Add("invalid Date");
        var fromTerminal = ResolveImportTerminal(fromCode, terminalLookup, terminalRows);
        if (string.IsNullOrWhiteSpace(fromCode)) rowErrors.Add("FromTerminal is required");
        else if (fromTerminal is null) rowErrors.Add($"unknown FromTerminal '{fromCode}'");
        else if (fromTerminal.Id == terminalId) rowErrors.Add("FromTerminal cannot be the receiving terminal itself");
        if (string.IsNullOrWhiteSpace(reference)) rowErrors.Add("ContainerTrailer is required");
        if (reference.Length > 120) rowErrors.Add("ContainerTrailer exceeds 120 characters");
        if (comment.Length > 2000) rowErrors.Add("Comment exceeds 2000 characters");
        if (!TryParseImportBool(receivedText, out var receiptReceived)) rowErrors.Add("PalletReceiptReceived must be Yes/No");
        if (!TryParseImportInt(actualText, out var actualPallets) || actualPallets < 0 || actualPallets > 10000) rowErrors.Add("ActualPallets must be a whole number from 0 to 10000");
        int? receiptPallets = null;
        if (receiptReceived)
        {
            if (!TryParseImportInt(receiptPalletsText, out var parsedReceiptPallets) || parsedReceiptPallets < 0 || parsedReceiptPallets > 10000)
                rowErrors.Add("ReceiptPallets is required and must be 0-10000 when a pallet receipt was received");
            else receiptPallets = parsedReceiptPallets;
        }

        if (rowErrors.Count > 0)
        {
            issues.Add(new ImportIssue(sourceRow.RowNumber, string.Join("; ", rowErrors)));
            continue;
        }

        var key = ReceivedControlImportKey(businessDate, fromTerminal!.Id, reference, receiptReceived, receiptPallets, actualPallets, comment);
        if (!fileKeys.Add(key))
        {
            issues.Add(new ImportIssue(sourceRow.RowNumber, "duplicate row inside the import file"));
            continue;
        }
        pending.Add(new PendingReceivedControlImport(sourceRow.RowNumber, businessDate, fromTerminal.Id, fromTerminal.Code, reference, receiptReceived, receiptPallets, actualPallets, comment, key));
    }

    var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (pending.Count > 0)
    {
        var minDate = pending.Min(x => x.BusinessDate);
        var maxDate = pending.Max(x => x.BusinessDate);
        var existing = await db.ReceivedControlEntries.AsNoTracking()
            .Where(x => x.TerminalId == terminalId && x.BusinessDate >= minDate && x.BusinessDate <= maxDate)
            .Select(x => new { x.BusinessDate, x.FromTerminalId, x.UnitReference, x.PalletReceiptReceived, x.ReceiptPalletCount, x.ActualPalletCount, x.Comment })
            .ToListAsync();
        foreach (var x in existing)
            existingKeys.Add(ReceivedControlImportKey(x.BusinessDate, x.FromTerminalId, x.UnitReference, x.PalletReceiptReceived, x.ReceiptPalletCount, x.ActualPalletCount, x.Comment));
    }

    var ready = new List<PendingReceivedControlImport>();
    var skippedDuplicates = 0;
    foreach (var p in pending)
    {
        if (existingKeys.Contains(p.DuplicateKey))
        {
            skippedDuplicates++;
            issues.Add(new ImportIssue(p.RowNumber, "matching MottattKontroll record already exists; skipped"));
            continue;
        }
        ready.Add(p);
        existingKeys.Add(p.DuplicateKey);
    }

    var previewRows = ready.OrderBy(x => x.RowNumber).Take(500).Select(x => new
    {
        row = x.RowNumber, date = x.BusinessDate, fromTerminal = x.FromTerminalCode, containerTrailer = x.UnitReference, comment = x.Comment,
        palletReceiptReceived = x.PalletReceiptReceived, receiptPallets = x.ReceiptPalletCount, actualPallets = x.ActualPalletCount,
        result = ReceivedControlStatus.Resolve(x.PalletReceiptReceived, x.ReceiptPalletCount, x.ActualPalletCount)
    }).ToList();

    if (!confirmImport)
    {
        return Results.Ok(new
        {
            preview = true, file = file.FileName, rowsRead = grid.Rows.Count, readyToImport = ready.Count, imported = 0, skippedDuplicates,
            rejected = issues.Count, redWarningsCreated = 0, previewRows, previewRowsTruncated = ready.Count > 500,
            issues = issues.Take(200).ToList(), issueListTruncated = issues.Count > 200
        });
    }

    var now = DateTime.UtcNow;
    var importedRows = new List<ReceivedControlEntry>();
    foreach (var p in ready)
    {
        var result = ReceivedControlStatus.Resolve(p.PalletReceiptReceived, p.ReceiptPalletCount, p.ActualPalletCount);
        var entity = new ReceivedControlEntry
        {
            ControlNumber = $"RC-{terminal.Code}-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            TerminalId = terminalId,
            FromTerminalId = p.FromTerminalId,
            FromTerminalSnapshot = p.FromTerminalCode,
            UnitReference = p.UnitReference,
            Comment = p.Comment,
            PalletReceiptReceived = p.PalletReceiptReceived,
            ReceiptPalletCount = p.ReceiptPalletCount,
            ActualPalletCount = p.ActualPalletCount,
            Result = result,
            BusinessDate = p.BusinessDate,
            SubmittedAtUtc = now,
            SubmittedByUserId = userId,
            Status = ReceiptStatus.Active
        };
        importedRows.Add(entity);
    }

    if (importedRows.Count > 0)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.ReceivedControlEntries.AddRange(importedRows);
        await db.SaveChangesAsync();
        foreach (var row in importedRows.Where(x => x.Result == ReceivedControlStatus.ReceiptHigher))
        {
            var difference = (row.ReceiptPalletCount ?? 0) - row.ActualPalletCount;
            db.ReceivedControlWarnings.Add(new ReceivedControlWarning
            {
                TerminalId = terminalId,
                EntryId = row.Id,
                Message = $"From {row.FromTerminalSnapshot}{(string.IsNullOrWhiteSpace(row.UnitReference) ? "" : $" · {row.UnitReference}")}: pallet receipt says {row.ReceiptPalletCount}, but {row.ActualPalletCount} pallets were actually received. Shortage: {difference}. Imported historical control.",
                CreatedAtUtc = now
            });
        }
        await db.SaveChangesAsync();
        await Audit(db, principal, "RECEIVED_CONTROL_IMPORT", $"Imported {importedRows.Count} historical MottattKontroll rows for terminal {terminal.Code} from {file.FileName}");
        await tx.CommitAsync();
    }

    var terminalCodesForResult = terminalRows.ToDictionary(x => x.Id, x => x.Code);
    return Results.Ok(new
    {
        preview = false, file = file.FileName, rowsRead = grid.Rows.Count, imported = importedRows.Count, skippedDuplicates, rejected = issues.Count,
        redWarningsCreated = importedRows.Count(x => x.Result == ReceivedControlStatus.ReceiptHigher),
        importedRows = importedRows.OrderBy(x => x.BusinessDate).ThenBy(x => x.ControlNumber).Take(500).Select(x => new
        {
            x.ControlNumber, date = x.BusinessDate, fromTerminal = terminalCodesForResult.GetValueOrDefault(x.FromTerminalId, x.FromTerminalSnapshot),
            containerTrailer = x.UnitReference, comment = x.Comment, palletReceiptReceived = x.PalletReceiptReceived, receiptPallets = x.ReceiptPalletCount,
            actualPallets = x.ActualPalletCount, x.Result
        }).ToList(), importedRowsTruncated = importedRows.Count > 500,
        issues = issues.Take(200).ToList(), issueListTruncated = issues.Count > 200
    });
}).RequireAuthorization("ReceivedControlAdmin");

// Database status is Admin-only because it contains server filesystem paths.
app.MapGet("/api/admin/database/status", (
    DatabaseStorageOptions storage,
    DatabaseBackupManager backupManager) =>
{
    var dbFile = new FileInfo(storage.DatabasePath);
    var backupStatus = backupManager.GetStatus();

    return Results.Ok(new
    {
        databasePath = storage.DatabasePath,
        databaseExists = dbFile.Exists,
        databaseSizeBytes = dbFile.Exists ? dbFile.Length : 0,
        backupDirectory = storage.BackupDirectory,
        backupIntervalHours = storage.BackupIntervalHours,
        backupRetentionDays = storage.BackupRetentionDays,
        backupCount = backupStatus.BackupCount,
        latestBackupPath = backupStatus.LatestBackupPath,
        latestBackupUtc = backupStatus.LatestBackupUtc
    });
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/database/backup", async (
    DatabaseBackupManager backupManager,
    CancellationToken cancellationToken) =>
{
    var backup = await backupManager.CreateBackupAsync(cancellationToken);
    return Results.Ok(new
    {
        message = "Database backup created.",
        backup.Path,
        backup.CreatedAtUtc,
        backup.SizeBytes
    });
}).RequireAuthorization(SuperAdminOnly());

// ---------------- ADMIN ----------------


app.MapGet("/api/admin/terminals", async (AppDbContext db) =>
{
    return Results.Ok(new { terminals = await db.Terminals.AsNoTracking().OrderBy(x => x.Code).ToListAsync() });
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/terminals", async (
    AdminTerminalRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var code = (req.Code ?? "").Trim().ToUpperInvariant();
    var name = (req.Name ?? "").Trim();
    if (string.IsNullOrWhiteSpace(code) || code.Length > 24) return Results.BadRequest(new { message = "Terminal code is required (max 24 characters)." });
    if (string.IsNullOrWhiteSpace(name)) name = code;
    var existing = await db.Terminals.FirstOrDefaultAsync(x => x.Code.ToUpper() == code);
    if (existing != null)
    {
        if (existing.Active) return Results.Conflict(new { message = $"Terminal {code} already exists." });
        existing.Active = true; existing.Name = name; existing.Aliases = string.Join(", ", ParseTerminalAliases(req.Aliases)); await db.SaveChangesAsync();
        await EnsureTerminalSettings(db, existing.Id);
        return Results.Ok(existing);
    }
    var row = new Terminal { Code = code, Name = name, Aliases = string.Join(", ", ParseTerminalAliases(req.Aliases)), Active = true };
    db.Terminals.Add(row); await db.SaveChangesAsync();
    await EnsureTerminalSettings(db, row.Id);
    await Audit(db, principal, "TERMINAL_CREATE", $"Created terminal {code} - {name}");
    return Results.Ok(row);
}).RequireAuthorization(SuperAdminOnly());

app.MapPut("/api/admin/terminals/{id:int}", async (
    int id,
    AdminTerminalUpdateRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Terminals.FindAsync(id);
    if (row is null) return Results.NotFound();

    var oldCode = row.Code;
    var oldName = row.Name;
    var code = string.IsNullOrWhiteSpace(req.Code) ? row.Code : req.Code.Trim().ToUpperInvariant();
    var name = string.IsNullOrWhiteSpace(req.Name) ? row.Name : req.Name.Trim();
    if (string.IsNullOrWhiteSpace(code) || code.Length > 24)
        return Results.BadRequest(new { message = "Terminal code is required (max 24 characters)." });
    if (await db.Terminals.AsNoTracking().AnyAsync(x => x.Id != id && x.Code.ToUpper() == code))
        return Results.Conflict(new { message = $"Terminal code {code} is already in use." });

    var aliases = req.Aliases is null ? ParseTerminalAliases(row.Aliases) : ParseTerminalAliases(req.Aliases);
    if (!oldCode.Equals(code, StringComparison.OrdinalIgnoreCase)) aliases.Add(oldCode);
    if (!oldName.Equals(name, StringComparison.OrdinalIgnoreCase)) aliases.Add(oldName);
    aliases = aliases
        .Where(x => !x.Equals(code, StringComparison.OrdinalIgnoreCase) && !x.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    row.Code = code;
    row.Name = name;
    row.Aliases = string.Join(", ", aliases);
    row.Active = req.Active;
    await db.SaveChangesAsync();

    // Keep snapshots readable and consistent with the current terminal display code.
    var snapshotRows = await db.LinehaulReceipts.Where(x => x.FromTerminalId == id || x.ToTerminalId == id).ToListAsync();
    foreach (var receipt in snapshotRows)
    {
        if (receipt.FromTerminalId == id) receipt.FromTerminalSnapshot = code;
        if (receipt.ToTerminalId == id) receipt.ToTerminalSnapshot = code;
    }
    if (snapshotRows.Count > 0) await db.SaveChangesAsync();

    await Audit(db, principal, "TERMINAL_UPDATE", $"Updated terminal {oldCode} -> {row.Code}: name={row.Name}, aliases={row.Aliases}, active={row.Active}");
    return Results.Ok(row);
}).RequireAuthorization(SuperAdminOnly());

app.MapGet("/api/admin/terminal-settings", async (
    int? terminalId,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var targetTerminalId = ResolveAdminTerminal(principal, terminalId);
    if (targetTerminalId <= 0) return Results.Forbid();
    var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetTerminalId);
    if (terminal is null) return Results.NotFound(new { message = "Terminal not found." });
    var settings = await GetTerminalSettings(db, targetTerminalId);
    return Results.Ok(new
    {
        terminalId = targetTerminalId,
        terminalCode = terminal.Code,
        terminals = IsSuperAdmin(principal)
            ? await db.Terminals.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync()
            : await db.Terminals.AsNoTracking().Where(x => x.Id == targetTerminalId).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync(),
        settings
    });
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/terminal-settings", async (
    int? terminalId,
    AdminSettingsRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var targetTerminalId = ResolveAdminTerminal(principal, terminalId);
    if (targetTerminalId <= 0) return Results.Forbid();
    var s = await GetTerminalSettings(db, targetTerminalId, tracking: true);
    ApplySettings(s, req);
    await db.SaveChangesAsync();
    await Audit(db, principal, "TERMINAL_SETTINGS_UPDATE", $"Updated terminal settings for terminal #{targetTerminalId}");
    return Results.Ok(s);
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/linehaul-comments", async (
    int? terminalId,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var targetTerminalId = ResolveAdminTerminal(principal, terminalId);
    if (targetTerminalId <= 0) return Results.Forbid();
    var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetTerminalId);
    if (terminal is null) return Results.NotFound();
    return Results.Ok(new
    {
        terminalId = targetTerminalId,
        terminalCode = terminal.Code,
        terminals = IsSuperAdmin(principal)
            ? await db.Terminals.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync()
            : await db.Terminals.AsNoTracking().Where(x => x.Id == targetTerminalId).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync(),
        comments = await db.LinehaulCommentOptions.AsNoTracking().Where(x => x.TerminalId == targetTerminalId).OrderBy(x => x.Text).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/linehaul-comments", async (
    AdminLinehaulCommentRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var targetTerminalId = ResolveAdminTerminal(principal, req.TerminalId);
    if (targetTerminalId <= 0) return Results.Forbid();
    var text = (req.Text ?? "").Trim();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { message = "Comment text is required." });
    var existing = await db.LinehaulCommentOptions.FirstOrDefaultAsync(x => x.TerminalId == targetTerminalId && x.Text.ToLower() == text.ToLower());
    if (existing != null)
    {
        existing.Active = true; await db.SaveChangesAsync(); return Results.Ok(existing);
    }
    var row = new LinehaulCommentOption { TerminalId = targetTerminalId, Text = text, Active = true };
    db.LinehaulCommentOptions.Add(row); await db.SaveChangesAsync();
    await Audit(db, principal, "LINEHAUL_COMMENT_CREATE", $"Added linehaul comment for terminal #{targetTerminalId}: {text}");
    return Results.Ok(row);
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/linehaul-comments/{id:int}/active", async (
    int id,
    AdminActiveRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.LinehaulCommentOptions.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();
    row.Active = req.Active; await db.SaveChangesAsync();
    await Audit(db, principal, "LINEHAUL_COMMENT_UPDATE", $"Set linehaul comment #{id} active={req.Active}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/all", async (AppDbContext db) =>
{
    var settings = await db.Settings.AsNoTracking().SingleAsync();
    return Results.Ok(new
    {
        terminals = await db.Terminals.AsNoTracking().OrderBy(x => x.Code).ToListAsync(),
        transporters = await db.Transporters.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
        vehicles = await db.Vehicles.AsNoTracking()
            .Include(x => x.Terminal)
            .Include(x => x.Transporter)
            .OrderBy(x => x.VehicleId)
            .Select(x => new
            {
                x.Id,
                x.VehicleId,
                x.Active,
                x.TerminalId,
                terminal = x.Terminal!.Code,
                x.TransporterId,
                transporter = x.Transporter != null ? x.Transporter.Name : "Not assigned"
            }).ToListAsync(),
        drivers = await db.Drivers.AsNoTracking()
            .Include(x => x.Terminal)
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Active,
                x.TerminalId,
                terminal = x.Terminal!.Code
            }).ToListAsync(),
        palletTypes = await db.PalletTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
        users = await db.Users.AsNoTracking()
            .Include(x => x.Terminal)
            .OrderBy(x => x.Username)
            .Select(x => new
            {
                x.Id,
                x.Username,
                x.DisplayName,
                x.Role,
                x.Active,
                x.TerminalId,
                terminal = x.Terminal!.Code,
                x.ShowMilestoneNotifications,
                x.ShowLeaderboardNotifications,
                x.ShowBalanceNotifications,
                x.ShowDriverStatisticsTab,
                x.ShowDailyCheckTab,
                x.HasInternalPalletAccounting,
                x.HasLinehaul,
                x.HasReceivedControl
            }).ToListAsync(),
        settings
    });
}).RequireAuthorization(SuperAdminOnly());


app.MapGet("/api/admin/transporters", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        transporters = await db.Transporters.AsNoTracking().OrderBy(x => x.Name).ToListAsync()
    });
}).RequireAuthorization(SuperAdminOnly());

app.MapGet("/api/admin/vehicles", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var q = db.Vehicles.AsNoTracking().Include(x => x.Terminal).Include(x => x.Transporter).AsQueryable();
    if (!IsSuperAdmin(principal)) q = q.Where(x => x.TerminalId == terminalId);
    var vehicles = await q.OrderBy(x => x.VehicleId).ToListAsync();
    var terminals = IsSuperAdmin(principal)
        ? await db.Terminals.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).ToListAsync()
        : await db.Terminals.AsNoTracking().Where(x => x.Id == terminalId).ToListAsync();
    return Results.Ok(new
    {
        terminals,
        transporters = await db.Transporters.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
        vehicles = vehicles.Select(x => new
        {
            x.Id, x.VehicleId, x.Active, x.TerminalId, terminal = x.Terminal!.Code,
            x.TransporterId, transporter = x.Transporter != null ? x.Transporter.Name : "Not assigned",
            operatingDays = ParseOperatingDays(x.OperatingDays).OrderBy(day => day).ToArray()
        }).ToList()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/drivers", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var q = db.Drivers.AsNoTracking().Include(x => x.Terminal).AsQueryable();
    if (!IsSuperAdmin(principal)) q = q.Where(x => x.TerminalId == terminalId);
    var terminals = IsSuperAdmin(principal)
        ? await db.Terminals.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).ToListAsync()
        : await db.Terminals.AsNoTracking().Where(x => x.Id == terminalId).ToListAsync();
    return Results.Ok(new
    {
        terminals,
        drivers = await q.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Active, x.TerminalId, terminal = x.Terminal!.Code }).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/pallet-types", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        palletTypes = await db.PalletTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync()
    });
}).RequireAuthorization(SuperAdminOnly());

app.MapGet("/api/admin/users", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var q = db.Users.AsNoTracking().Include(x => x.Terminal).AsQueryable();
    if (!IsSuperAdmin(principal)) q = q.Where(x => x.TerminalId == terminalId && x.Role != Roles.SuperAdmin && x.Role != Roles.LegacyAdmin);
    var terminals = IsSuperAdmin(principal)
        ? await db.Terminals.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).ToListAsync()
        : await db.Terminals.AsNoTracking().Where(x => x.Id == terminalId).ToListAsync();
    return Results.Ok(new
    {
        terminals,
        users = await q.OrderBy(x => x.Username).Select(x => new
        {
            x.Id, x.Username, x.DisplayName, x.Role, x.Active, x.TerminalId, terminal = x.Terminal!.Code,
            x.ShowMilestoneNotifications, x.ShowLeaderboardNotifications, x.ShowBalanceNotifications,
            x.ShowDriverStatisticsTab, x.ShowDailyCheckTab,
            x.HasInternalPalletAccounting, x.HasLinehaul, x.HasReceivedControl
        }).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/settings", async (AppDbContext db) =>
{
    return Results.Ok(await db.Settings.AsNoTracking().SingleAsync());
}).RequireAuthorization(SuperAdminOnly());

app.MapGet("/api/admin/holidays", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        holidays = await db.Holidays
            .AsNoTracking()
            .OrderByDescending(x => x.Date)
            .Select(x => new { x.Id, x.Date, x.Name })
            .ToListAsync()
    });
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/holidays", async (
    AdminHolidayRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var name = (req.Name ?? "").Trim();
    if (string.IsNullOrWhiteSpace(name))
        name = "Holiday / non-working day";

    if (await db.Holidays.AnyAsync(x => x.Date == req.Date))
        return Results.Conflict(new { message = $"{req.Date:yyyy-MM-dd} is already registered as a holiday." });

    var row = new Holiday { Date = req.Date, Name = name };
    db.Holidays.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "HOLIDAY_CREATE", $"Added non-working day {row.Date:yyyy-MM-dd}: {row.Name}");
    return Results.Ok(new { row.Id, row.Date, row.Name });
}).RequireAuthorization(SuperAdminOnly());

app.MapDelete("/api/admin/holidays/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Holidays.FindAsync(id);
    if (row is null) return Results.NotFound();

    db.Holidays.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "HOLIDAY_DELETE", $"Removed non-working day {row.Date:yyyy-MM-dd}: {row.Name}");
    return Results.Ok();
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/transporters", async (
    AdminTransporterRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Transporter name is required." });

    if (await db.Transporters.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
        return Results.BadRequest(new { message = "Transporter already exists." });

    var row = new Transporter { Name = name, Active = true };
    db.Transporters.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "TRANSPORTER_CREATE", $"Created transporter {name}");
    return Results.Ok(row);
}).RequireAuthorization(SuperAdminOnly());

app.MapDelete("/api/admin/transporters/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Transporters.FindAsync(id);
    if (row is null) return Results.NotFound();

    var vehicles = await db.Vehicles.Where(x => x.TransporterId == id).ToListAsync();
    foreach (var vehicle in vehicles)
        vehicle.TransporterId = null;

    db.Transporters.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "TRANSPORTER_DELETE", $"Deleted transporter {row.Name}; {vehicles.Count} vehicle(s) became unassigned");
    return Results.Ok();
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/vehicles", async (
    AdminVehicleRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (!CanManageTerminal(principal, req.TerminalId)) return Results.Forbid();
    var idText = req.VehicleId.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(idText))
        return Results.BadRequest(new { message = "Vehicle ID is required." });

    if (await db.Vehicles.AnyAsync(x => x.VehicleId == idText))
        return Results.Conflict(new { code = "VEHICLE_EXISTS", message = $"Vehicle {idText} already exists." });

    if (!await db.Terminals.AnyAsync(x => x.Id == req.TerminalId))
        return Results.BadRequest(new { message = "Terminal not found." });

    if (!await db.Transporters.AnyAsync(x => x.Id == req.TransporterId && x.Active))
        return Results.BadRequest(new { message = "Transporter not found." });

    var row = new Vehicle
    {
        VehicleId = idText,
        TerminalId = req.TerminalId,
        TransporterId = req.TransporterId,
        Active = true,
        OperatingDays = "1,2,3,4,5"
    };
    db.Vehicles.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "VEHICLE_CREATE", $"Created vehicle {idText}");
    return Results.Ok(row);
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/vehicles/{id:int}/transporter", async (
    int id,
    VehicleTransporterRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Vehicles.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();

    if (!await db.Transporters.AnyAsync(x => x.Id == req.TransporterId && x.Active))
        return Results.BadRequest(new { message = "Transporter not found." });

    row.TransporterId = req.TransporterId;
    await db.SaveChangesAsync();
    await Audit(db, principal, "VEHICLE_TRANSPORTER", $"Changed transporter for {row.VehicleId}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/vehicles/{id:int}/schedule", async (
    int id,
    VehicleScheduleRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Vehicles.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();

    var days = (req.Days ?? [])
        .Distinct()
        .OrderBy(x => x)
        .ToList();

    if (days.Any(x => x < 1 || x > 7))
        return Results.BadRequest(new { message = "Operating days must be between Monday (1) and Sunday (7)." });

    row.OperatingDays = string.Join(',', days);
    await db.SaveChangesAsync();
    await Audit(db, principal, "VEHICLE_SCHEDULE", $"Changed operating days for {row.VehicleId} to {(days.Count == 0 ? "none" : string.Join(',', days))}");
    return Results.Ok(new { operatingDays = days });
}).RequireAuthorization(AdminOnly());

app.MapDelete("/api/admin/vehicles/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Vehicles.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();

    var name = row.VehicleId;
    db.Vehicles.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "VEHICLE_DELETE", $"Deleted vehicle {name}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/drivers", async (
    AdminDriverRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (!CanManageTerminal(principal, req.TerminalId)) return Results.Forbid();
    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Driver name is required." });

    var existingDriver = await db.Drivers
        .FirstOrDefaultAsync(x => x.TerminalId == req.TerminalId && x.Name.ToLower() == name.ToLower());
    if (existingDriver is not null)
    {
        if (existingDriver.Active)
            return Results.BadRequest(new { message = "Driver already exists for this terminal." });

        existingDriver.Active = true;
        await db.SaveChangesAsync();
        await Audit(db, principal, "DRIVER_RESTORE", $"Restored driver {existingDriver.Name} to future selection");
        return Results.Ok(existingDriver);
    }

    var row = new Driver { Name = name, TerminalId = req.TerminalId, Active = true };
    db.Drivers.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "DRIVER_CREATE", $"Created driver {name}");
    return Results.Ok(row);
}).RequireAuthorization(AdminOnly());

app.MapDelete("/api/admin/drivers/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Drivers.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();

    // Driver names are soft-deleted. This removes the name from future registration
    // while preserving DriverId links and all historical statistics.
    row.Active = false;
    await db.SaveChangesAsync();
    await Audit(db, principal, "DRIVER_DEACTIVATE", $"Removed driver {row.Name} from future selection; history preserved");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/drivers/{id:int}/active", async (
    int id,
    AdminActiveRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Drivers.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();
    row.Active = req.Active;
    await db.SaveChangesAsync();
    await Audit(db, principal, req.Active ? "DRIVER_RESTORE" : "DRIVER_DEACTIVATE",
        $"Set driver {row.Name} active={req.Active}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/pallet-types", async (
    AdminPalletTypeRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Pallet type name is required." });
    if (await db.PalletTypes.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
        return Results.BadRequest(new { message = "Pallet type already exists." });

    var row = new PalletType { Name = name, Active = true, UserSelectable = req.UserSelectable };
    db.PalletTypes.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "PALLET_TYPE_CREATE", $"Created pallet type {name}");
    return Results.Ok(row);
}).RequireAuthorization(SuperAdminOnly());

app.MapPut("/api/admin/pallet-types/{id:int}", async (
    int id,
    AdminPalletTypeUpdate req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.PalletTypes.FindAsync(id);
    if (row is null) return Results.NotFound();
    row.Active = req.Active;
    row.UserSelectable = req.UserSelectable;
    await db.SaveChangesAsync();
    await Audit(db, principal, "PALLET_TYPE_UPDATE", $"Updated pallet type {row.Name}");
    return Results.Ok();
}).RequireAuthorization(SuperAdminOnly());

app.MapPost("/api/admin/users", async (
    AdminUserRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var username = req.Username.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Username and password are required." });
    if (!ValidRole(req.Role)) return Results.BadRequest(new { message = "Invalid role." });
    if (!CanManageTerminal(principal, req.TerminalId)) return Results.Forbid();
    if (!IsSuperAdmin(principal) && req.Role == Roles.SuperAdmin) return Results.Forbid();
    if (await db.Users.AnyAsync(x => x.Username == username)) return Results.BadRequest(new { message = "Username already exists." });

    var row = new AppUser
    {
        Username = username,
        DisplayName = req.DisplayName.Trim(),
        Role = req.Role,
        TerminalId = req.TerminalId,
        Active = true,
        HasInternalPalletAccounting = req.HasInternalPalletAccounting,
        HasLinehaul = req.HasLinehaul,
        HasReceivedControl = req.HasReceivedControl,
        ShowDriverStatisticsTab = req.ShowDriverStatisticsTab,
        ShowDailyCheckTab = req.ShowDailyCheckTab
    };
    row.PasswordHash = new PasswordHasher<AppUser>().HashPassword(row, req.Password);
    db.Users.Add(row); await db.SaveChangesAsync();
    await Audit(db, principal, "USER_CREATE", $"Created user {username} ({req.Role}) modules internal={row.HasInternalPalletAccounting}, linehaul={row.HasLinehaul}, received={row.HasReceivedControl}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/users/{id:int}", async (
    int id,
    AdminUserUpdateRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Users.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!ValidRole(req.Role)) return Results.BadRequest(new { message = "Invalid role." });
    if (!CanManageTerminal(principal, row.TerminalId) || !CanManageTerminal(principal, req.TerminalId)) return Results.Forbid();
    if (!IsSuperAdmin(principal) && (row.Role == Roles.SuperAdmin || row.Role == Roles.LegacyAdmin || req.Role == Roles.SuperAdmin)) return Results.Forbid();

    row.DisplayName = req.DisplayName.Trim();
    row.Role = req.Role;
    row.TerminalId = req.TerminalId;
    row.Active = req.Active;
    row.HasInternalPalletAccounting = req.HasInternalPalletAccounting;
    row.HasLinehaul = req.HasLinehaul;
    row.HasReceivedControl = req.HasReceivedControl;
    row.ShowDriverStatisticsTab = req.ShowDriverStatisticsTab;
    row.ShowDailyCheckTab = req.ShowDailyCheckTab;
    await db.SaveChangesAsync();
    await Audit(db, principal, "USER_UPDATE", $"Updated user {row.Username}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/users/{id:int}/tab-access", async (
    int id,
    AdminTabAccessRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Users.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();
    if (!IsSuperAdmin(principal) && (row.Role == Roles.SuperAdmin || row.Role == Roles.LegacyAdmin)) return Results.Forbid();

    row.ShowDriverStatisticsTab = req.ShowDriverStatisticsTab;
    row.ShowDailyCheckTab = req.ShowDailyCheckTab;
    await db.SaveChangesAsync();
    await Audit(db, principal, "USER_TAB_ACCESS",
        $"Updated tab access for {row.Username}: driverStats={row.ShowDriverStatisticsTab}, dailyCheck={row.ShowDailyCheckTab}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/users/{id:int}/password", async (
    int id,
    AdminPasswordRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
        return Results.BadRequest(new { message = "Password must be at least 6 characters." });

    var row = await db.Users.FindAsync(id);
    if (row is null) return Results.NotFound();
    if (!CanManageTerminal(principal, row.TerminalId)) return Results.Forbid();
    if (!IsSuperAdmin(principal) && (row.Role == Roles.SuperAdmin || row.Role == Roles.LegacyAdmin)) return Results.Forbid();
    row.PasswordHash = new PasswordHasher<AppUser>().HashPassword(row, req.Password);
    await db.SaveChangesAsync();
    await Audit(db, principal, "USER_PASSWORD", $"Reset password for {row.Username}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapPut("/api/admin/settings", async (
    AdminSettingsRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var s = await db.Settings.SingleAsync();
    s.AllowUsersAddDrivers = req.AllowUsersAddDrivers;

    s.LargeInEnabled = req.LargeInEnabled;
    s.LargeInThreshold = Math.Max(1, req.LargeInThreshold);
    s.LargeOutEnabled = req.LargeOutEnabled;
    s.LargeOutThreshold = Math.Max(1, req.LargeOutThreshold);

    s.RecentVehicleEnabled = req.RecentVehicleEnabled;
    s.RecentVehicleMinutes = Math.Clamp(req.RecentVehicleMinutes, 1, 1440);
    s.RecentDriverEnabled = req.RecentDriverEnabled;
    s.RecentDriverMinutes = Math.Clamp(req.RecentDriverMinutes, 1, 1440);

    s.DuplicateEnabled = req.DuplicateEnabled;
    s.DuplicateMinutes = Math.Clamp(req.DuplicateMinutes, 1, 1440);

    s.RapidSubmissionsEnabled = req.RapidSubmissionsEnabled;
    s.RapidSubmissionCount = Math.Clamp(req.RapidSubmissionCount, 2, 50);
    s.RapidSubmissionMinutes = Math.Clamp(req.RapidSubmissionMinutes, 1, 1440);

    s.DailyTotalEnabled = req.DailyTotalEnabled;
    s.DailyTotalThreshold = Math.Max(1, req.DailyTotalThreshold);

    s.CancellationWarningEnabled = req.CancellationWarningEnabled;
    s.CancellationReversedWarningEnabled = req.CancellationReversedWarningEnabled;

    s.MilestoneNotificationsEnabled = req.MilestoneNotificationsEnabled;
    s.MonthlyMilestoneStep = Math.Max(1, req.MonthlyMilestoneStep);
    s.LeaderboardNotificationsEnabled = req.LeaderboardNotificationsEnabled;
    s.BalanceNotificationsEnabled = req.BalanceNotificationsEnabled;
    s.DriverUnmatchedInDeduction = Math.Clamp(req.DriverUnmatchedInDeduction ?? s.DriverUnmatchedInDeduction, 0, 5000);

    await db.SaveChangesAsync();
    await Audit(db, principal, "SETTINGS_UPDATE", "Updated warning and notification settings");
    return Results.Ok(s);
}).RequireAuthorization(SuperAdminOnly());

app.Run("http://0.0.0.0:5000");

// ---------------- HELPERS ----------------

static AuthorizeAttribute AdminOnly() => new() { Roles = $"{Roles.SuperAdmin},{Roles.TerminalAdmin},{Roles.LegacyAdmin}" };
static AuthorizeAttribute SuperAdminOnly() => new() { Roles = $"{Roles.SuperAdmin},{Roles.LegacyAdmin}" };

static int UserId(ClaimsPrincipal principal) =>
    int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? throw new InvalidOperationException("User ID claim missing."));

static int TerminalId(ClaimsPrincipal principal) =>
    int.Parse(principal.FindFirstValue("terminalId")
              ?? throw new InvalidOperationException("Terminal claim missing."));

static string Role(ClaimsPrincipal principal) =>
    principal.FindFirstValue(ClaimTypes.Role) ?? Roles.User;

static bool ValidRole(string role) => role is Roles.SuperAdmin or Roles.TerminalAdmin or Roles.Superuser or Roles.User or Roles.LegacyAdmin;

static bool IsSuperAdmin(ClaimsPrincipal principal) => Role(principal) is Roles.SuperAdmin or Roles.LegacyAdmin;
static bool IsTerminalAdmin(ClaimsPrincipal principal) => Role(principal) == Roles.TerminalAdmin;
static bool CanManageTerminal(ClaimsPrincipal principal, int terminalId) => IsSuperAdmin(principal) || (IsTerminalAdmin(principal) && TerminalId(principal) == terminalId);
static int ResolveAdminTerminal(ClaimsPrincipal principal, int? requestedTerminalId) => IsSuperAdmin(principal) ? (requestedTerminalId ?? TerminalId(principal)) : TerminalId(principal);

static List<int> ParseIds(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var id) ? id : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();

static HashSet<int> ParseOperatingDays(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return [];

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var day) ? day : 0)
        .Where(x => x is >= 1 and <= 7)
        .ToHashSet();
}

static int IsoDayOfWeek(DateOnly date)
{
    var day = (int)date.DayOfWeek;
    return day == 0 ? 7 : day;
}

static void EnsureCompatibilitySchema(AppDbContext db)
{
    // EnsureCreated() creates the complete schema for a new database, but it does not
    // add newly introduced columns/tables to an existing SQLite database. These small
    // compatibility migrations preserve all existing PalletControl data.
    var connection = (SqliteConnection)db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose) connection.Open();

    try
    {
        var vehicleColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info('Vehicles');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                vehicleColumns.Add(reader.GetString(1));
        }

        if (!vehicleColumns.Contains("OperatingDays"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Vehicles\" ADD COLUMN \"OperatingDays\" TEXT NOT NULL DEFAULT '1,2,3,4,5';";
            cmd.ExecuteNonQuery();
        }

        var userColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info('Users');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                userColumns.Add(reader.GetString(1));
        }

        if (!userColumns.Contains("ShowDriverStatisticsTab"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Users\" ADD COLUMN \"ShowDriverStatisticsTab\" INTEGER NOT NULL DEFAULT 1;";
            cmd.ExecuteNonQuery();
        }

        if (!userColumns.Contains("ShowDailyCheckTab"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Users\" ADD COLUMN \"ShowDailyCheckTab\" INTEGER NOT NULL DEFAULT 1;";
            cmd.ExecuteNonQuery();
        }


        if (!userColumns.Contains("HasInternalPalletAccounting"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Users\" ADD COLUMN \"HasInternalPalletAccounting\" INTEGER NOT NULL DEFAULT 1;";
            cmd.ExecuteNonQuery();
        }
        if (!userColumns.Contains("HasLinehaul"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Users\" ADD COLUMN \"HasLinehaul\" INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }
        if (!userColumns.Contains("HasReceivedControl"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Users\" ADD COLUMN \"HasReceivedControl\" INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE \"Users\" SET \"Role\"='SuperAdmin' WHERE \"Role\"='Admin';";
            cmd.ExecuteNonQuery();
        }

        var terminalColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info('Terminals');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) terminalColumns.Add(reader.GetString(1));
        }
        if (!terminalColumns.Contains("Active"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Terminals\" ADD COLUMN \"Active\" INTEGER NOT NULL DEFAULT 1;";
            cmd.ExecuteNonQuery();
        }
        if (!terminalColumns.Contains("Aliases"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Terminals\" ADD COLUMN \"Aliases\" TEXT NOT NULL DEFAULT '';";
            cmd.ExecuteNonQuery();
        }

        var settingsColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info('Settings');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                settingsColumns.Add(reader.GetString(1));
        }

        if (!settingsColumns.Contains("DriverUnmatchedInDeduction"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Settings\" ADD COLUMN \"DriverUnmatchedInDeduction\" INTEGER NOT NULL DEFAULT 15;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
CREATE TABLE IF NOT EXISTS "Holidays" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Holidays" PRIMARY KEY AUTOINCREMENT,
    "Date" TEXT NOT NULL,
    "Name" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Holidays_Date" ON "Holidays" ("Date");
""";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
CREATE TABLE IF NOT EXISTS "TerminalSettings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TerminalSettings" PRIMARY KEY AUTOINCREMENT,
    "TerminalId" INTEGER NOT NULL,
    "AllowUsersAddDrivers" INTEGER NOT NULL DEFAULT 1,
    "LargeInEnabled" INTEGER NOT NULL DEFAULT 1, "LargeInThreshold" INTEGER NOT NULL DEFAULT 20,
    "LargeOutEnabled" INTEGER NOT NULL DEFAULT 1, "LargeOutThreshold" INTEGER NOT NULL DEFAULT 20,
    "RecentVehicleEnabled" INTEGER NOT NULL DEFAULT 1, "RecentVehicleMinutes" INTEGER NOT NULL DEFAULT 5,
    "RecentDriverEnabled" INTEGER NOT NULL DEFAULT 1, "RecentDriverMinutes" INTEGER NOT NULL DEFAULT 5,
    "DuplicateEnabled" INTEGER NOT NULL DEFAULT 1, "DuplicateMinutes" INTEGER NOT NULL DEFAULT 5,
    "RapidSubmissionsEnabled" INTEGER NOT NULL DEFAULT 1, "RapidSubmissionCount" INTEGER NOT NULL DEFAULT 3, "RapidSubmissionMinutes" INTEGER NOT NULL DEFAULT 10,
    "DailyTotalEnabled" INTEGER NOT NULL DEFAULT 1, "DailyTotalThreshold" INTEGER NOT NULL DEFAULT 60,
    "CancellationWarningEnabled" INTEGER NOT NULL DEFAULT 1, "CancellationReversedWarningEnabled" INTEGER NOT NULL DEFAULT 1,
    "MilestoneNotificationsEnabled" INTEGER NOT NULL DEFAULT 1, "MonthlyMilestoneStep" INTEGER NOT NULL DEFAULT 100,
    "LeaderboardNotificationsEnabled" INTEGER NOT NULL DEFAULT 1, "BalanceNotificationsEnabled" INTEGER NOT NULL DEFAULT 1,
    "DriverUnmatchedInDeduction" INTEGER NOT NULL DEFAULT 15,
    CONSTRAINT "FK_TerminalSettings_Terminals_TerminalId" FOREIGN KEY ("TerminalId") REFERENCES "Terminals" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalSettings_TerminalId" ON "TerminalSettings" ("TerminalId");

CREATE TABLE IF NOT EXISTS "LinehaulCommentOptions" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_LinehaulCommentOptions" PRIMARY KEY AUTOINCREMENT,
    "TerminalId" INTEGER NOT NULL, "Text" TEXT NOT NULL, "Active" INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS "IX_LinehaulCommentOptions_TerminalId" ON "LinehaulCommentOptions" ("TerminalId");

CREATE TABLE IF NOT EXISTS "LinehaulReceipts" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_LinehaulReceipts" PRIMARY KEY AUTOINCREMENT,
    "ReceiptNumber" TEXT NOT NULL, "OwnerTerminalId" INTEGER NOT NULL,
    "FromTerminalId" INTEGER NOT NULL, "ToTerminalId" INTEGER NOT NULL,
    "FromTerminalSnapshot" TEXT NOT NULL, "ToTerminalSnapshot" TEXT NOT NULL,
    "UnitReference" TEXT NOT NULL, "PalletReceiptNumber" TEXT NOT NULL DEFAULT '', "PalletCount" INTEGER NOT NULL,
    "CommentOptionSnapshot" TEXT NOT NULL, "FreeComment" TEXT NOT NULL,
    "BusinessDate" TEXT NOT NULL, "SubmittedAtUtc" TEXT NOT NULL, "SubmittedByUserId" INTEGER NOT NULL,
    "Status" TEXT NOT NULL DEFAULT 'ACTIVE', "CancelledAtUtc" TEXT NULL, "CancelledByUserId" INTEGER NULL, "CancelReason" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_LinehaulReceipts_ReceiptNumber" ON "LinehaulReceipts" ("ReceiptNumber");
CREATE INDEX IF NOT EXISTS "IX_LinehaulReceipts_FromToDate" ON "LinehaulReceipts" ("FromTerminalId", "ToTerminalId", "BusinessDate");

CREATE TABLE IF NOT EXISTS "ReceivedControlEntries" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ReceivedControlEntries" PRIMARY KEY AUTOINCREMENT,
    "ControlNumber" TEXT NOT NULL, "TerminalId" INTEGER NOT NULL, "FromTerminalId" INTEGER NOT NULL DEFAULT 0,
    "FromTerminalSnapshot" TEXT NOT NULL DEFAULT '', "UnitReference" TEXT NOT NULL, "Comment" TEXT NOT NULL DEFAULT '',
    "PalletReceiptReceived" INTEGER NOT NULL, "ReceiptPalletCount" INTEGER NULL, "ActualPalletCount" INTEGER NOT NULL,
    "Result" TEXT NOT NULL, "BusinessDate" TEXT NOT NULL, "SubmittedAtUtc" TEXT NOT NULL, "SubmittedByUserId" INTEGER NOT NULL,
    "Status" TEXT NOT NULL DEFAULT 'ACTIVE', "CancelledAtUtc" TEXT NULL, "CancelledByUserId" INTEGER NULL, "CancelReason" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceivedControlEntries_ControlNumber" ON "ReceivedControlEntries" ("ControlNumber");
CREATE INDEX IF NOT EXISTS "IX_ReceivedControlEntries_TerminalDate" ON "ReceivedControlEntries" ("TerminalId", "BusinessDate");

CREATE TABLE IF NOT EXISTS "ReceivedControlWarnings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ReceivedControlWarnings" PRIMARY KEY AUTOINCREMENT,
    "TerminalId" INTEGER NOT NULL, "EntryId" INTEGER NOT NULL, "Message" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL, "AcknowledgedAtUtc" TEXT NULL, "AcknowledgedByUserId" INTEGER NULL
);
CREATE INDEX IF NOT EXISTS "IX_ReceivedControlWarnings_TerminalAck" ON "ReceivedControlWarnings" ("TerminalId", "AcknowledgedAtUtc", "CreatedAtUtc");
""";
            cmd.ExecuteNonQuery();
        }

        var linehaulColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"LinehaulReceipts\");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) linehaulColumns.Add(reader.GetString(1));
        }
        if (!linehaulColumns.Contains("PalletReceiptNumber"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"LinehaulReceipts\" ADD COLUMN \"PalletReceiptNumber\" TEXT NOT NULL DEFAULT '';";
            cmd.ExecuteNonQuery();
        }
        if (!linehaulColumns.Contains("Status"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"LinehaulReceipts\" ADD COLUMN \"Status\" TEXT NOT NULL DEFAULT 'ACTIVE';";
            cmd.ExecuteNonQuery();
        }
        if (!linehaulColumns.Contains("CancelledAtUtc"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"LinehaulReceipts\" ADD COLUMN \"CancelledAtUtc\" TEXT NULL;";
            cmd.ExecuteNonQuery();
        }
        if (!linehaulColumns.Contains("CancelledByUserId"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"LinehaulReceipts\" ADD COLUMN \"CancelledByUserId\" INTEGER NULL;";
            cmd.ExecuteNonQuery();
        }
        if (!linehaulColumns.Contains("CancelReason"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"LinehaulReceipts\" ADD COLUMN \"CancelReason\" TEXT NULL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_LinehaulReceipts_PalletReceiptNumber\" ON \"LinehaulReceipts\" (\"PalletReceiptNumber\");";
            cmd.ExecuteNonQuery();
        }
        // Enforce unique non-blank pallet receipt numbers at database level as well as API validation.
        // If a legacy database already contains duplicate non-blank values, keep running and let Admin clean them up.
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_LinehaulReceipts_PalletReceiptNumber_NotBlank\" ON \"LinehaulReceipts\" (\"PalletReceiptNumber\") WHERE \"PalletReceiptNumber\" <> '';";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { }

        var receivedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"ReceivedControlEntries\");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) receivedColumns.Add(reader.GetString(1));
        }
        if (!receivedColumns.Contains("FromTerminalId"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"FromTerminalId\" INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("FromTerminalSnapshot"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"FromTerminalSnapshot\" TEXT NOT NULL DEFAULT '';";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("Comment"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"Comment\" TEXT NOT NULL DEFAULT '';";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("Status"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"Status\" TEXT NOT NULL DEFAULT 'ACTIVE';";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("CancelledAtUtc"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"CancelledAtUtc\" TEXT NULL;";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("CancelledByUserId"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"CancelledByUserId\" INTEGER NULL;";
            cmd.ExecuteNonQuery();
        }
        if (!receivedColumns.Contains("CancelReason"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ReceivedControlEntries\" ADD COLUMN \"CancelReason\" TEXT NULL;";
            cmd.ExecuteNonQuery();
        }

    }
    finally
    {
        if (shouldClose) connection.Close();
    }
}


static async Task<TerminalSettings> GetTerminalSettings(AppDbContext db, int terminalId, bool tracking = false)
{
    var query = tracking ? db.TerminalSettings.AsQueryable() : db.TerminalSettings.AsNoTracking();
    var existing = await query.FirstOrDefaultAsync(x => x.TerminalId == terminalId);
    if (existing != null) return existing;
    await EnsureTerminalSettings(db, terminalId);
    return tracking
        ? await db.TerminalSettings.SingleAsync(x => x.TerminalId == terminalId)
        : await db.TerminalSettings.AsNoTracking().SingleAsync(x => x.TerminalId == terminalId);
}

static async Task EnsureTerminalSettings(AppDbContext db, int terminalId)
{
    if (await db.TerminalSettings.AnyAsync(x => x.TerminalId == terminalId)) return;
    var g = await db.Settings.AsNoTracking().SingleAsync();
    db.TerminalSettings.Add(TerminalSettings.FromGlobal(terminalId, g));
    await db.SaveChangesAsync();
}

static void ApplySettings(TerminalSettings s, AdminSettingsRequest req)
{
    s.AllowUsersAddDrivers = req.AllowUsersAddDrivers;
    s.LargeInEnabled = req.LargeInEnabled;
    s.LargeInThreshold = Math.Max(1, req.LargeInThreshold);
    s.LargeOutEnabled = req.LargeOutEnabled;
    s.LargeOutThreshold = Math.Max(1, req.LargeOutThreshold);
    s.RecentVehicleEnabled = req.RecentVehicleEnabled;
    s.RecentVehicleMinutes = Math.Clamp(req.RecentVehicleMinutes, 1, 1440);
    s.RecentDriverEnabled = req.RecentDriverEnabled;
    s.RecentDriverMinutes = Math.Clamp(req.RecentDriverMinutes, 1, 1440);
    s.DuplicateEnabled = req.DuplicateEnabled;
    s.DuplicateMinutes = Math.Clamp(req.DuplicateMinutes, 1, 1440);
    s.RapidSubmissionsEnabled = req.RapidSubmissionsEnabled;
    s.RapidSubmissionCount = Math.Clamp(req.RapidSubmissionCount, 2, 50);
    s.RapidSubmissionMinutes = Math.Clamp(req.RapidSubmissionMinutes, 1, 1440);
    s.DailyTotalEnabled = req.DailyTotalEnabled;
    s.DailyTotalThreshold = Math.Max(1, req.DailyTotalThreshold);
    s.CancellationWarningEnabled = req.CancellationWarningEnabled;
    s.CancellationReversedWarningEnabled = req.CancellationReversedWarningEnabled;
    s.MilestoneNotificationsEnabled = req.MilestoneNotificationsEnabled;
    s.MonthlyMilestoneStep = Math.Max(1, req.MonthlyMilestoneStep);
    s.LeaderboardNotificationsEnabled = req.LeaderboardNotificationsEnabled;
    s.BalanceNotificationsEnabled = req.BalanceNotificationsEnabled;
    s.DriverUnmatchedInDeduction = Math.Clamp(req.DriverUnmatchedInDeduction ?? s.DriverUnmatchedInDeduction, 0, 5000);
}

static object ToLinehaulDto(LinehaulReceipt x) => new
{
    x.Id, x.ReceiptNumber, x.BusinessDate, x.SubmittedAtUtc, x.UnitReference, x.PalletReceiptNumber, x.PalletCount,
    x.FromTerminalId, x.ToTerminalId, fromTerminal = x.FromTerminalSnapshot, toTerminal = x.ToTerminalSnapshot,
    standardComment = x.CommentOptionSnapshot, x.FreeComment, x.Status, x.CancelledAtUtc, x.CancelReason
};

static object ToReceivedControlDto(ReceivedControlEntry x) => new
{
    x.Id, x.ControlNumber, x.BusinessDate, x.SubmittedAtUtc, x.FromTerminalId, x.FromTerminalSnapshot, x.UnitReference, x.Comment,
    x.PalletReceiptReceived, x.ReceiptPalletCount, x.ActualPalletCount, x.Result, x.Status, x.CancelledAtUtc, x.CancelReason,
    difference = x.PalletReceiptReceived ? x.ActualPalletCount - (x.ReceiptPalletCount ?? 0) : (int?)null
};

static List<string> ParseTerminalAliases(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

static string NormalizeTerminalLabel(string value) =>
    new(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

static Dictionary<string, Terminal> BuildTerminalLookup(IEnumerable<Terminal> terminals)
{
    var result = new Dictionary<string, Terminal>(StringComparer.OrdinalIgnoreCase);
    foreach (var terminal in terminals)
    {
        var labels = new List<string> { terminal.Code, terminal.Name };
        labels.AddRange(ParseTerminalAliases(terminal.Aliases));
        foreach (var label in labels.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = NormalizeTerminalLabel(label);
            if (normalized.Length > 0 && !result.ContainsKey(normalized)) result[normalized] = terminal;
        }
    }
    return result;
}

static Terminal? ResolveImportTerminal(string value, IReadOnlyDictionary<string, Terminal> lookup, IReadOnlyList<Terminal> terminals)
{
    var normalized = NormalizeTerminalLabel(value);
    if (normalized.Length == 0) return null;
    if (lookup.TryGetValue(normalized, out var exact)) return exact;

    // Common legacy convention: SRD123 / ARE01 / KRS7 where the numeric suffix was local.
    var matches = terminals
        .Where(t =>
        {
            var code = NormalizeTerminalLabel(t.Code);
            if (code.Length == 0 || !normalized.StartsWith(code, StringComparison.OrdinalIgnoreCase)) return false;
            var suffix = normalized[code.Length..];
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        })
        .ToList();
    return matches.Count == 1 ? matches[0] : null;
}

static string NormalizeImportHeader(string value) =>
    new(value.Trim().Trim('\uFEFF').Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

static bool HasImportHeader(ImportGrid grid, params string[] aliases)
{
    var headers = grid.Headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
    return aliases.Select(NormalizeImportHeader).Any(headers.Contains);
}

static string ImportValue(ImportDataRow row, params string[] aliases)
{
    foreach (var alias in aliases)
        if (row.Values.TryGetValue(NormalizeImportHeader(alias), out var value)) return value ?? "";
    return "";
}

static async Task<ImportGrid> ReadImportGrid(IFormFile file)
{
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not ".xlsx" and not ".csv")
        throw new InvalidOperationException("Only .xlsx and .csv files are supported. Save old .xls files as .xlsx or CSV first.");

    if (extension == ".xlsx")
    {
        await using var stream = file.OpenReadStream();
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault() ?? throw new InvalidOperationException("Excel workbook has no worksheet.");
        var firstRow = sheet.FirstRowUsed() ?? throw new InvalidOperationException("Excel sheet is empty.");
        var lastCell = firstRow.LastCellUsed() ?? throw new InvalidOperationException("Excel header row is empty.");
        var firstRowNumber = firstRow.RowNumber();
        var lastColumn = lastCell.Address.ColumnNumber;
        var headers = new List<string>();
        for (var col = 1; col <= lastColumn; col++)
            headers.Add(NormalizeImportHeader(sheet.Cell(firstRowNumber, col).GetFormattedString()));

        var rows = new List<ImportDataRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? firstRowNumber;
        for (var rowNumber = firstRowNumber + 1; rowNumber <= lastRow; rowNumber++)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hasValue = false;
            for (var col = 1; col <= lastColumn; col++)
            {
                var header = headers[col - 1];
                if (string.IsNullOrWhiteSpace(header)) continue;
                var value = sheet.Cell(rowNumber, col).GetFormattedString().Trim();
                if (!string.IsNullOrWhiteSpace(value)) hasValue = true;
                values[header] = value;
            }
            if (hasValue) rows.Add(new ImportDataRow(rowNumber, values));
        }
        return new ImportGrid(headers.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(), rows);
    }

    string text;
    await using (var stream = file.OpenReadStream())
    using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        text = await reader.ReadToEndAsync();

    var delimiter = DetectCsvDelimiter(text);
    var parsed = ParseCsvText(text, delimiter).Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
    if (parsed.Count == 0) throw new InvalidOperationException("CSV file is empty.");
    var csvHeaders = parsed[0].Select(NormalizeImportHeader).ToList();
    var csvRows = new List<ImportDataRow>();
    for (var i = 1; i < parsed.Count; i++)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var col = 0; col < csvHeaders.Count; col++)
        {
            var header = csvHeaders[col];
            if (string.IsNullOrWhiteSpace(header)) continue;
            values[header] = col < parsed[i].Count ? parsed[i][col].Trim() : "";
        }
        csvRows.Add(new ImportDataRow(i + 1, values));
    }
    return new ImportGrid(csvHeaders.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(), csvRows);
}

static char DetectCsvDelimiter(string text)
{
    var firstLine = text.Replace("\r\n", "\n").Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    var comma = 0; var semicolon = 0; var quoted = false;
    for (var i = 0; i < firstLine.Length; i++)
    {
        if (firstLine[i] == '"')
        {
            if (quoted && i + 1 < firstLine.Length && firstLine[i + 1] == '"') { i++; continue; }
            quoted = !quoted;
        }
        else if (!quoted && firstLine[i] == ',') comma++;
        else if (!quoted && firstLine[i] == ';') semicolon++;
    }
    return semicolon > comma ? ';' : ',';
}

static List<List<string>> ParseCsvText(string text, char delimiter)
{
    var rows = new List<List<string>>();
    var row = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '"')
        {
            if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
            else quoted = !quoted;
            continue;
        }
        if (!quoted && c == delimiter)
        {
            row.Add(field.ToString()); field.Clear(); continue;
        }
        if (!quoted && (c == '\r' || c == '\n'))
        {
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            row.Add(field.ToString()); field.Clear();
            if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
            row = new List<string>();
            continue;
        }
        field.Append(c);
    }
    if (field.Length > 0 || row.Count > 0)
    {
        row.Add(field.ToString());
        if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
    }
    return rows;
}

static bool TryParseImportDate(string value, out DateOnly date)
{
    date = default;
    var text = (value ?? "").Trim();
    if (string.IsNullOrWhiteSpace(text)) return false;
    foreach (var format in new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy" })
        if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
    if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("nb-NO"), DateTimeStyles.AllowWhiteSpaces, out var nbDate))
    { date = DateOnly.FromDateTime(nbDate); return true; }
    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariantDate))
    { date = DateOnly.FromDateTime(invariantDate); return true; }
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 1 && oa < 100000)
    { date = DateOnly.FromDateTime(DateTime.FromOADate(oa)); return true; }
    return false;
}

static bool TryParseImportInt(string value, out int result)
{
    result = 0;
    var text = (value ?? "").Trim();
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.GetCultureInfo("nb-NO"), out result)) return true;
    if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec) && dec == decimal.Truncate(dec) && dec >= int.MinValue && dec <= int.MaxValue)
    { result = (int)dec; return true; }
    if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("nb-NO"), out dec) && dec == decimal.Truncate(dec) && dec >= int.MinValue && dec <= int.MaxValue)
    { result = (int)dec; return true; }
    return false;
}

static bool TryParseImportBool(string value, out bool result)
{
    var text = NormalizeImportHeader(value ?? "");
    if (text is "yes" or "ja" or "true" or "1" or "y" or "j" or "received" or "mottatt") { result = true; return true; }
    if (text is "no" or "nei" or "false" or "0" or "n" or "notreceived" or "ikkemottatt") { result = false; return true; }
    result = false; return false;
}

static string LinehaulImportKey(DateOnly date, int fromId, int toId, string reference, string palletReceiptNumber, int pallets) =>
    $"{date:yyyy-MM-dd}|{fromId}|{toId}|{reference.Trim().ToUpperInvariant()}|{palletReceiptNumber.Trim().ToUpperInvariant()}|{pallets}";

static string ReceivedControlImportKey(DateOnly date, int fromTerminalId, string reference, bool received, int? receiptPallets, int actualPallets, string comment) =>
    $"{date:yyyy-MM-dd}|{fromTerminalId}|{reference.Trim().ToUpperInvariant()}|{received}|{receiptPallets?.ToString(CultureInfo.InvariantCulture) ?? ""}|{actualPallets}|{comment.Trim().ToUpperInvariant()}";

static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

static string ExportCsv(ExportTable table)
{
    var sb = new StringBuilder();
    sb.AppendLine(string.Join(",", table.Headers.Select(Csv)));
    foreach (var row in table.Rows)
    {
        sb.AppendLine(string.Join(",", row.Select(value => Csv(ExportCellText(value)))));
    }
    return sb.ToString();
}

static string ExportCellText(object? value) => value switch
{
    null => "",
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
    _ => value.ToString() ?? ""
};

static byte[] ExportWorkbook(IEnumerable<ExportTable> tables)
{
    using var workbook = new XLWorkbook();
    foreach (var table in tables)
    {
        var worksheet = workbook.Worksheets.Add(table.Name.Length > 31 ? table.Name[..31] : table.Name);
        for (var c = 0; c < table.Headers.Count; c++)
        {
            worksheet.Cell(1, c + 1).Value = table.Headers[c];
            worksheet.Cell(1, c + 1).Style.Font.Bold = true;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                var value = table.Rows[r][c];
                var cell = worksheet.Cell(r + 2, c + 1);
                switch (value)
                {
                    case int i: cell.Value = i; break;
                    case long l: cell.Value = l; break;
                    case decimal d: cell.Value = d; break;
                    case double d: cell.Value = d; break;
                    case DateTime dt: cell.Value = dt; break;
                    default: cell.Value = ExportCellText(value); break;
                }
            }
        }

        worksheet.SheetView.FreezeRows(1);
        worksheet.RangeUsed()?.SetAutoFilter();
        worksheet.Columns().AdjustToContents(1, Math.Max(1, Math.Min(table.Rows.Count + 1, 250)));
        foreach (var column in worksheet.ColumnsUsed())
        {
            if (column.Width > 45) column.Width = 45;
        }
    }

    using var stream = new MemoryStream();
    workbook.SaveAs(stream);
    return stream.ToArray();
}

static async Task Audit(
    AppDbContext db,
    ClaimsPrincipal principal,
    string action,
    string details)
{
    db.AuditLogs.Add(new AuditLog
    {
        UserId = UserId(principal),
        Action = action,
        Details = details,
        CreatedAtUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync();
}

static async Task<ReceiptValidation> ValidateReceiptRequest(
    CreateReceiptRequest req,
    ClaimsPrincipal principal,
    AppDbContext db)
{
    if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        return ReceiptValidation.Fail("Idempotency key is required.");

    var direction = req.Direction.Trim().ToUpperInvariant();
    if (direction is not ("IN" or "OUT"))
        return ReceiptValidation.Fail("Direction must be IN or OUT.");

    if (Role(principal) == Roles.User && req.BusinessDate.HasValue)
        return ReceiptValidation.Fail("Only Admin and Superuser can choose a receipt date.");

    var terminalId = TerminalId(principal);
    var vehicle = await db.Vehicles
        .Include(x => x.Transporter)
        .FirstOrDefaultAsync(x => x.Id == req.VehicleId && x.Active);
    var driver = await db.Drivers
        .FirstOrDefaultAsync(x => x.Id == req.DriverId && x.Active);

    if (vehicle is null)
        return ReceiptValidation.Fail("Vehicle not found.");
    if (vehicle.Transporter is null)
        return ReceiptValidation.Fail("Vehicle must be assigned to a transporter before it can be used.");
    if (driver is null)
        return ReceiptValidation.Fail("Driver not found.");
    if (vehicle.TerminalId != terminalId || driver.TerminalId != terminalId)
        return ReceiptValidation.Fail("Vehicle and driver must belong to your terminal.");

    // v5.5.2: an explicitly submitted quantity of 0 is valid. Only an empty Items list is rejected.
    if (req.Items.Count == 0)
        return ReceiptValidation.Fail("Enter a pallet quantity. 0 is allowed.");

    if (req.Items.Any(x => x.Quantity < 0))
        return ReceiptValidation.Fail("Pallet quantity cannot be negative.");

    var positiveItems = req.Items
        .GroupBy(x => x.PalletTypeId)
        .Select(g => new ReceiptItemRequest(g.Key, g.Sum(x => x.Quantity)))
        .ToList();

    if (positiveItems.Any(x => x.Quantity > 5000))
        return ReceiptValidation.Fail("A pallet quantity is too large.");

    var allowedIds = await db.PalletTypes
        .Where(x => x.Active && x.UserSelectable)
        .Select(x => x.Id)
        .ToListAsync();

    if (positiveItems.Any(x => !allowedIds.Contains(x.PalletTypeId)))
        return ReceiptValidation.Fail("One or more pallet types are not allowed.");

    return new ReceiptValidation(null, vehicle, driver, positiveItems);
}

static async Task<List<SubmissionWarningDto>> EvaluateSubmissionWarnings(
    CreateReceiptRequest req,
    Vehicle vehicle,
    Driver driver,
    List<ReceiptItemRequest> items,
    AppDbContext db)
{
    var settings = await GetTerminalSettings(db, vehicle.TerminalId);
    var now = DateTime.UtcNow;
    var total = items.Sum(x => x.Quantity);
    var direction = req.Direction.Trim().ToUpperInvariant();
    var warnings = new List<SubmissionWarningDto>();

    if (direction == "IN" && settings.LargeInEnabled && total > settings.LargeInThreshold)
        warnings.Add(new("LARGE_IN", "warning", $"Large IN submission: {total} pallets. Warning limit is {settings.LargeInThreshold}."));

    if (direction == "OUT" && settings.LargeOutEnabled && total > settings.LargeOutThreshold)
        warnings.Add(new("LARGE_OUT", "warning", $"Large OUT submission: {total} pallets. Warning limit is {settings.LargeOutThreshold}."));

    if (settings.RecentVehicleEnabled)
    {
        var since = now.AddMinutes(-settings.RecentVehicleMinutes);
        var recent = await db.Receipts.AsNoTracking()
            .Where(x => x.VehicleId == vehicle.Id && x.SubmittedAtUtc >= since)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Select(x => new { x.ReceiptNumber, x.SubmittedAtUtc })
            .FirstOrDefaultAsync();

        if (recent != null)
        {
            var ago = Math.Max(0, (int)Math.Floor((now - recent.SubmittedAtUtc).TotalMinutes));
            warnings.Add(new("RECENT_VEHICLE", "warning",
                $"Vehicle {vehicle.VehicleId} submitted {ago} minute(s) ago ({recent.ReceiptNumber}). Check that this is not accidental."));
        }
    }

    if (settings.RecentDriverEnabled)
    {
        var since = now.AddMinutes(-settings.RecentDriverMinutes);
        var recent = await db.Receipts.AsNoTracking()
            .Where(x => x.DriverId == driver.Id && x.SubmittedAtUtc >= since)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Select(x => new { x.ReceiptNumber, x.SubmittedAtUtc })
            .FirstOrDefaultAsync();

        if (recent != null)
        {
            var ago = Math.Max(0, (int)Math.Floor((now - recent.SubmittedAtUtc).TotalMinutes));
            warnings.Add(new("RECENT_DRIVER", "warning",
                $"Driver {driver.Name} submitted {ago} minute(s) ago ({recent.ReceiptNumber})."));
        }
    }

    if (settings.DuplicateEnabled)
    {
        var since = now.AddMinutes(-settings.DuplicateMinutes);
        var candidates = await db.Receipts.AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.VehicleId == vehicle.Id && x.DriverId == driver.Id &&
                        x.Direction == direction && x.SubmittedAtUtc >= since)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Take(20)
            .ToListAsync();

        var expected = items.OrderBy(x => x.PalletTypeId)
            .Select(x => $"{x.PalletTypeId}:{x.Quantity}");
        var expectedKey = string.Join("|", expected);

        var duplicate = candidates.FirstOrDefault(c =>
        {
            var key = string.Join("|", c.Items.OrderBy(x => x.PalletTypeId)
                .Select(x => $"{x.PalletTypeId}:{x.Quantity}"));
            return key == expectedKey;
        });

        if (duplicate != null)
            warnings.Add(new("POSSIBLE_DUPLICATE", "danger",
                $"Possible duplicate of {duplicate.ReceiptNumber}: same vehicle, driver, direction and pallet quantities."));
    }

    if (settings.RapidSubmissionsEnabled)
    {
        var since = now.AddMinutes(-settings.RapidSubmissionMinutes);
        var recentCount = await db.Receipts.AsNoTracking()
            .CountAsync(x => x.VehicleId == vehicle.Id && x.SubmittedAtUtc >= since);

        if (recentCount + 1 >= settings.RapidSubmissionCount)
            warnings.Add(new("RAPID_SUBMISSIONS", "warning",
                $"This will be submission #{recentCount + 1} for {vehicle.VehicleId} within {settings.RapidSubmissionMinutes} minutes."));
    }

    if (settings.DailyTotalEnabled)
    {
        var businessDate = req.BusinessDate ?? DateOnly.FromDateTime(DateTime.Today);
        var dateReceipts = await db.Receipts.AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.Status == ReceiptStatus.Active && x.VehicleId == vehicle.Id &&
                        x.BusinessDate == businessDate && x.Direction == direction)
            .ToListAsync();
        var dateTotal = dateReceipts.Sum(x => x.Items.Sum(i => i.Quantity));

        if (dateTotal + total > settings.DailyTotalThreshold)
            warnings.Add(new("HIGH_DAILY_TOTAL", "warning",
                $"{vehicle.VehicleId} will reach {dateTotal + total} pallets {direction} on {businessDate:dd.MM.yyyy}. Daily warning limit is {settings.DailyTotalThreshold}."));
    }

    return warnings
        .GroupBy(x => x.Type)
        .Select(x => x.First())
        .ToList();
}

static async Task<List<string>> BuildSubmitNotifications(
    PalletReceipt receipt,
    int driverId,
    ClaimsPrincipal principal,
    AppDbContext db)
{
    var settings = await GetTerminalSettings(db, receipt.TerminalId);
    var currentUserId = UserId(principal);
    var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == currentUserId);
    var notifications = new List<string>();

    var periodDate = receipt.BusinessDate;
    var monthStart = new DateOnly(periodDate.Year, periodDate.Month, 1);
    var monthEnd = monthStart.AddMonths(1).AddDays(-1);
    var monthLabel = periodDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    var monthReceipts = await db.Receipts.AsNoTracking()
        .Include(x => x.Items)
        .Where(x => x.Status == ReceiptStatus.Active &&
                    x.BusinessDate >= monthStart && x.BusinessDate <= monthEnd &&
                    x.TerminalId == receipt.TerminalId)
        .ToListAsync();

    var myReceipts = monthReceipts.Where(x => x.DriverId == driverId).ToList();
    var myIn = myReceipts.Where(x => x.Direction == "IN").Sum(x => x.Items.Sum(i => i.Quantity));
    var myOut = myReceipts.Where(x => x.Direction == "OUT").Sum(x => x.Items.Sum(i => i.Quantity));
    var myBalance = myIn - myOut;

    if (settings.MilestoneNotificationsEnabled && user.ShowMilestoneNotifications && receipt.Direction == "IN")
    {
        var currentIn = receipt.Items.Sum(x => x.Quantity);
        var before = myIn - currentIn;
        var step = Math.Max(1, settings.MonthlyMilestoneStep);
        var oldLevel = before / step;
        var newLevel = myIn / step;
        if (newLevel > oldLevel)
            notifications.Add($"🎉 Monthly milestone: {receipt.DriverSnapshot} has now brought in {myIn} pallets in {monthLabel}.");
    }

    if (settings.BalanceNotificationsEnabled && user.ShowBalanceNotifications)
        notifications.Add($"📊 Balance for {receipt.DriverSnapshot} in {monthLabel}: {Signed(myBalance)} pallets (IN {myIn} / OUT {myOut}).");

    if (settings.LeaderboardNotificationsEnabled && user.ShowLeaderboardNotifications)
    {
        var leaderboard = BuildDriverLeaderboard(monthReceipts, null);
        var index = leaderboard.FindIndex(x => x.DriverId == driverId);
        if (index >= 0)
        {
            var rank = index + 1;
            var me = leaderboard[index];
            if (rank == 1)
            {
                if (leaderboard.Count > 1)
                {
                    var second = leaderboard[1];
                    notifications.Add($"🏆 {receipt.DriverSnapshot} is #1 in {monthLabel} at {Signed(me.Balance)}. #2 is {second.Driver} in {second.Vehicle} at {Signed(second.Balance)}.");
                }
                else
                {
                    notifications.Add($"🏆 {receipt.DriverSnapshot} is currently #1 in {monthLabel} at {Signed(me.Balance)}.");
                }
            }
            else if (rank <= 3)
            {
                var leader = leaderboard[0];
                notifications.Add($"🥇 {receipt.DriverSnapshot} is currently #{rank} in {monthLabel} at {Signed(me.Balance)}. Leader: {leader.Driver} in {leader.Vehicle} at {Signed(leader.Balance)}.");
            }
        }
    }

    return notifications;
}

static List<DriverLeaderboardRow> BuildDriverLeaderboard(
    List<PalletReceipt> receipts,
    int? palletTypeId)
{
    var rows = receipts
        .Where(r => r.DriverId != null)
        .GroupBy(r => new { DriverId = r.DriverId!.Value, r.DriverSnapshot })
        .Select(g =>
        {
            var itemRows = g.SelectMany(r => r.Items
                .Where(i => !palletTypeId.HasValue || i.PalletTypeId == palletTypeId.Value)
                .Select(i => new { Receipt = r, i.Quantity }))
                .ToList();

            var inQty = itemRows.Where(x => x.Receipt.Direction == "IN").Sum(x => x.Quantity);
            var outQty = itemRows.Where(x => x.Receipt.Direction == "OUT").Sum(x => x.Quantity);

            var topVehicle = itemRows
                .GroupBy(x => x.Receipt.VehicleSnapshot)
                .Select(v => new { Vehicle = v.Key, Movement = v.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Movement)
                .ThenBy(x => x.Vehicle)
                .FirstOrDefault()?.Vehicle ?? "—";

            return new DriverLeaderboardRow
            {
                DriverId = g.Key.DriverId,
                Driver = g.Key.DriverSnapshot,
                Vehicle = topVehicle,
                InQty = inQty,
                OutQty = outQty,
                Balance = inQty - outQty,
                Movement = inQty + outQty
            };
        })
        .Where(x => x.Movement > 0)
        .OrderByDescending(x => x.Balance)
        .ThenByDescending(x => x.InQty)
        .ThenByDescending(x => x.Movement)
        .ThenBy(x => x.Driver)
        .ToList();

    for (var i = 0; i < rows.Count; i++)
        rows[i].Rank = i + 1;

    return rows;
}

static (DateOnly From, DateOnly To, string Period) ResolvePeriod(string? period)
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var normalized = (period ?? "thisMonth").Trim();

    return normalized switch
    {
        "thisWeek" => (StartOfWeek(today), today, "thisWeek"),
        "lastMonth" => (
            new DateOnly(today.AddMonths(-1).Year, today.AddMonths(-1).Month, 1),
            new DateOnly(today.Year, today.Month, 1).AddDays(-1),
            "lastMonth"),
        _ => (new DateOnly(today.Year, today.Month, 1), today, "thisMonth")
    };
}

static DateOnly StartOfWeek(DateOnly date)
{
    var day = (int)date.DayOfWeek;
    var mondayOffset = day == 0 ? 6 : day - 1;
    return date.AddDays(-mondayOffset);
}

static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

static object ToReceiptDto(PalletReceipt r) => new
{
    r.Id,
    r.ReceiptNumber,
    r.BusinessDate,
    r.SubmittedAtUtc,
    r.Direction,
    r.Status,
    vehicle = r.VehicleSnapshot,
    driver = r.DriverSnapshot,
    transporter = r.TransporterSnapshot,
    r.CancelledAtUtc,
    r.CancelReason,
    wasReversed = r.Actions.Any(a => a.Action == "CANCELLATION_REVERSED"),
    items = r.Items.Select(i => new
    {
        i.PalletTypeId,
        palletType = i.PalletType?.Name ?? "Unknown",
        i.Quantity
    }).ToList(),
    actions = r.Actions
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(a => new
        {
            a.Id,
            a.Action,
            a.Reason,
            a.CreatedAtUtc,
            user = a.User?.DisplayName ?? a.User?.Username ?? "Unknown"
        }).ToList()
};

static void Seed(AppDbContext db)
{
    // Demo/master data is seeded only when the database is genuinely new.
    // This is important because Admin is allowed to delete vehicles, drivers and
    // transporters. A deleted master record must stay deleted after an app restart.
    var isFreshDatabase = !db.Terminals.Any()
                          && !db.Users.Any()
                          && !db.Vehicles.Any()
                          && !db.Drivers.Any()
                          && !db.Transporters.Any()
                          && !db.PalletTypes.Any();

    // Settings are application configuration rather than demo data. Ensure that the
    // singleton exists even for an existing database, but never recreate deleted
    // master data just to satisfy the seed routine.
    if (!db.Settings.Any())
    {
        db.Settings.Add(new AppSettings());
        db.SaveChanges();
    }

    foreach (var terminalId in db.Terminals.Select(x => x.Id).ToList())
        EnsureTerminalSettings(db, terminalId).GetAwaiter().GetResult();

    if (!isFreshDatabase)
    {
        // v5.4.3 adds ARE as a real terminal. Add it once to existing databases
        // without recreating any master data that an Admin intentionally deleted.
        if (!db.Terminals.Any(x => x.Code == "ARE"))
        {
            db.Terminals.Add(new Terminal { Code = "ARE", Name = "Arendal", Active = true });
            db.SaveChanges();
        }
        foreach (var terminalId in db.Terminals.Select(x => x.Id).ToList())
            EnsureTerminalSettings(db, terminalId).GetAwaiter().GetResult();
        return;
    }

    var srd = new Terminal { Code = "SRD", Name = "Sandefjord", Active = true };
    var krs = new Terminal { Code = "KRS", Name = "Kristiansand", Active = true };
    var are = new Terminal { Code = "ARE", Name = "Arendal", Active = true };
    db.Terminals.AddRange(srd, krs, are);

    db.PalletTypes.AddRange(
        new PalletType { Name = "EUR pallet", Active = true, UserSelectable = true },
        new PalletType { Name = "Half pallet", Active = true, UserSelectable = true },
        new PalletType { Name = "One-time pallet", Active = true, UserSelectable = true });

    var telemark = new Transporter { Name = "Telemark TransportService", Active = true };
    var frode = new Transporter { Name = "Frode Bjønnes", Active = true };
    db.Transporters.AddRange(telemark, frode);

    db.SaveChanges();
    EnsureTerminalSettings(db, srd.Id).GetAwaiter().GetResult();
    EnsureTerminalSettings(db, krs.Id).GetAwaiter().GetResult();
    EnsureTerminalSettings(db, are.Id).GetAwaiter().GetResult();

    db.Vehicles.AddRange(
        new Vehicle { VehicleId = "VTM3241", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTM3755", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTA3754", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTM3771", TerminalId = srd.Id, TransporterId = frode.Id, Active = true });

    db.Drivers.AddRange(
        new Driver { Name = "Test Driver", TerminalId = srd.Id, Active = true },
        new Driver { Name = "John Smith", TerminalId = srd.Id, Active = true });

    AddUser("admin", "Administrator", Roles.SuperAdmin, "admin123");
    AddUser("super", "Super User", Roles.Superuser, "super123");
    AddUser("user", "Terminal User", Roles.User, "user123");

    db.SaveChanges();

    void AddUser(string username, string displayName, string role, string password)
    {
        var u = new AppUser
        {
            Username = username,
            DisplayName = displayName,
            Role = role,
            TerminalId = srd.Id,
            Active = true
        };
        u.PasswordHash = new PasswordHasher<AppUser>().HashPassword(u, password);
        db.Users.Add(u);
    }
}

// ---------------- DATABASE STORAGE / BACKUP ----------------

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

public sealed record ExportTable(string Name, List<string> Headers, List<List<object?>> Rows);

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string TerminalAdmin = "TerminalAdmin";
    public const string Superuser = "Superuser";
    public const string User = "User";
    public const string LegacyAdmin = "Admin";
}

public static class ReceiptStatus
{
    public const string Active = "ACTIVE";
    public const string Cancelled = "CANCELLED";
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Transporter> Transporters => Set<Transporter>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<PalletType> PalletTypes => Set<PalletType>();
    public DbSet<PalletReceipt> Receipts => Set<PalletReceipt>();
    public DbSet<PalletReceiptItem> ReceiptItems => Set<PalletReceiptItem>();
    public DbSet<ReceiptAction> ReceiptActions => Set<ReceiptAction>();
    public DbSet<WarningEvent> WarningEvents => Set<WarningEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<TerminalSettings> TerminalSettings => Set<TerminalSettings>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<LinehaulReceipt> LinehaulReceipts => Set<LinehaulReceipt>();
    public DbSet<LinehaulCommentOption> LinehaulCommentOptions => Set<LinehaulCommentOption>();
    public DbSet<ReceivedControlEntry> ReceivedControlEntries => Set<ReceivedControlEntry>();
    public DbSet<ReceivedControlWarning> ReceivedControlWarnings => Set<ReceivedControlWarning>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.VehicleId).IsUnique();
        b.Entity<Transporter>().HasIndex(x => x.Name).IsUnique();
        b.Entity<PalletType>().HasIndex(x => x.Name).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.ReceiptNumber).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<WarningEvent>().HasIndex(x => new { x.TerminalId, x.AcknowledgedAtUtc, x.CreatedAtUtc });
        b.Entity<Holiday>().HasIndex(x => x.Date).IsUnique();
        b.Entity<TerminalSettings>().HasIndex(x => x.TerminalId).IsUnique();
        b.Entity<LinehaulReceipt>().HasIndex(x => x.ReceiptNumber).IsUnique();
        b.Entity<LinehaulReceipt>().HasIndex(x => x.PalletReceiptNumber);
        b.Entity<LinehaulReceipt>().HasIndex(x => new { x.FromTerminalId, x.ToTerminalId, x.BusinessDate });
        b.Entity<LinehaulCommentOption>().HasIndex(x => x.TerminalId);
        b.Entity<ReceivedControlEntry>().HasIndex(x => x.ControlNumber).IsUnique();
        b.Entity<ReceivedControlEntry>().HasIndex(x => new { x.TerminalId, x.FromTerminalId, x.BusinessDate });
        b.Entity<ReceivedControlWarning>().HasIndex(x => new { x.TerminalId, x.AcknowledgedAtUtc, x.CreatedAtUtc });
        b.Entity<Vehicle>().Property(x => x.OperatingDays).HasDefaultValue("1,2,3,4,5");

        b.Entity<Vehicle>()
            .HasOne(x => x.Transporter)
            .WithMany(x => x.Vehicles)
            .HasForeignKey(x => x.TransporterId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.Vehicle)
            .WithMany()
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.Driver)
            .WithMany()
            .HasForeignKey(x => x.DriverId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.SubmittedByUser)
            .WithMany()
            .HasForeignKey(x => x.SubmittedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<PalletReceipt>()
            .HasMany(x => x.Actions)
            .WithOne(x => x.Receipt)
            .HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<ReceiptAction>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<WarningEvent>()
            .HasOne(x => x.Receipt)
            .WithMany()
            .HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<WarningEvent>()
            .HasOne(x => x.TriggeredByUser)
            .WithMany()
            .HasForeignKey(x => x.TriggeredByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<WarningEvent>()
            .HasOne(x => x.AcknowledgedByUser)
            .WithMany()
            .HasForeignKey(x => x.AcknowledgedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.User;
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }

    public bool ShowMilestoneNotifications { get; set; } = true;
    public bool ShowLeaderboardNotifications { get; set; } = true;
    public bool ShowBalanceNotifications { get; set; } = true;

    // Per-user navigation/data access controlled from Admin -> Tab access.
    public bool ShowDriverStatisticsTab { get; set; } = true;
    public bool ShowDailyCheckTab { get; set; } = true;

    // Operational modules can be combined freely on one user.
    public bool HasInternalPalletAccounting { get; set; } = true;
    public bool HasLinehaul { get; set; } = false;
    public bool HasReceivedControl { get; set; } = false;

    [JsonIgnore] public Terminal? Terminal { get; set; }
}

public class Terminal
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Aliases { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class Transporter
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    [JsonIgnore] public List<Vehicle> Vehicles { get; set; } = [];
}

public class Vehicle
{
    public int Id { get; set; }
    public string VehicleId { get; set; } = "";
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }
    public string OperatingDays { get; set; } = "1,2,3,4,5";
    [JsonIgnore] public Terminal? Terminal { get; set; }
    public int? TransporterId { get; set; }
    [JsonIgnore] public Transporter? Transporter { get; set; }
}

public class Holiday
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class Driver
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }
    [JsonIgnore] public Terminal? Terminal { get; set; }
}

public class PalletType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool UserSelectable { get; set; } = true;
}

public class PalletReceipt
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }

    public int? VehicleId { get; set; }
    [JsonIgnore] public Vehicle? Vehicle { get; set; }
    public int? DriverId { get; set; }
    [JsonIgnore] public Driver? Driver { get; set; }

    public string VehicleSnapshot { get; set; } = "";
    public string DriverSnapshot { get; set; } = "";
    public string TransporterSnapshot { get; set; } = "";

    public string Direction { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    public int SubmittedByUserId { get; set; }
    public AppUser? SubmittedByUser { get; set; }

    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }

    public List<PalletReceiptItem> Items { get; set; } = [];
    public List<ReceiptAction> Actions { get; set; } = [];
}

public class PalletReceiptItem
{
    public int Id { get; set; }
    public int PalletReceiptId { get; set; }
    [JsonIgnore] public PalletReceipt? PalletReceipt { get; set; }
    public int PalletTypeId { get; set; }
    public PalletType? PalletType { get; set; }
    public int Quantity { get; set; }
}

public class ReceiptAction
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    [JsonIgnore] public PalletReceipt? Receipt { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
}

public class WarningEvent
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int TerminalId { get; set; }

    public int? ReceiptId { get; set; }
    public PalletReceipt? Receipt { get; set; }

    public int TriggeredByUserId { get; set; }
    public AppUser? TriggeredByUser { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public AppUser? AcknowledgedByUser { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}


public class TerminalSettings
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public bool AllowUsersAddDrivers { get; set; } = true;
    public bool LargeInEnabled { get; set; } = true;
    public int LargeInThreshold { get; set; } = 20;
    public bool LargeOutEnabled { get; set; } = true;
    public int LargeOutThreshold { get; set; } = 20;
    public bool RecentVehicleEnabled { get; set; } = true;
    public int RecentVehicleMinutes { get; set; } = 5;
    public bool RecentDriverEnabled { get; set; } = true;
    public int RecentDriverMinutes { get; set; } = 5;
    public bool DuplicateEnabled { get; set; } = true;
    public int DuplicateMinutes { get; set; } = 5;
    public bool RapidSubmissionsEnabled { get; set; } = true;
    public int RapidSubmissionCount { get; set; } = 3;
    public int RapidSubmissionMinutes { get; set; } = 10;
    public bool DailyTotalEnabled { get; set; } = true;
    public int DailyTotalThreshold { get; set; } = 60;
    public bool CancellationWarningEnabled { get; set; } = true;
    public bool CancellationReversedWarningEnabled { get; set; } = true;
    public bool MilestoneNotificationsEnabled { get; set; } = true;
    public int MonthlyMilestoneStep { get; set; } = 100;
    public bool LeaderboardNotificationsEnabled { get; set; } = true;
    public bool BalanceNotificationsEnabled { get; set; } = true;
    public int DriverUnmatchedInDeduction { get; set; } = 15;

    public static TerminalSettings FromGlobal(int terminalId, AppSettings g) => new()
    {
        TerminalId = terminalId,
        AllowUsersAddDrivers = g.AllowUsersAddDrivers,
        LargeInEnabled = g.LargeInEnabled, LargeInThreshold = g.LargeInThreshold,
        LargeOutEnabled = g.LargeOutEnabled, LargeOutThreshold = g.LargeOutThreshold,
        RecentVehicleEnabled = g.RecentVehicleEnabled, RecentVehicleMinutes = g.RecentVehicleMinutes,
        RecentDriverEnabled = g.RecentDriverEnabled, RecentDriverMinutes = g.RecentDriverMinutes,
        DuplicateEnabled = g.DuplicateEnabled, DuplicateMinutes = g.DuplicateMinutes,
        RapidSubmissionsEnabled = g.RapidSubmissionsEnabled, RapidSubmissionCount = g.RapidSubmissionCount, RapidSubmissionMinutes = g.RapidSubmissionMinutes,
        DailyTotalEnabled = g.DailyTotalEnabled, DailyTotalThreshold = g.DailyTotalThreshold,
        CancellationWarningEnabled = g.CancellationWarningEnabled, CancellationReversedWarningEnabled = g.CancellationReversedWarningEnabled,
        MilestoneNotificationsEnabled = g.MilestoneNotificationsEnabled, MonthlyMilestoneStep = g.MonthlyMilestoneStep,
        LeaderboardNotificationsEnabled = g.LeaderboardNotificationsEnabled, BalanceNotificationsEnabled = g.BalanceNotificationsEnabled,
        DriverUnmatchedInDeduction = g.DriverUnmatchedInDeduction
    };
}

public class LinehaulCommentOption
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public string Text { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class LinehaulReceipt
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public int OwnerTerminalId { get; set; }
    public int FromTerminalId { get; set; }
    public int ToTerminalId { get; set; }
    public string FromTerminalSnapshot { get; set; } = "";
    public string ToTerminalSnapshot { get; set; } = "";
    public string UnitReference { get; set; } = "";
    public string PalletReceiptNumber { get; set; } = "";
    public int PalletCount { get; set; }
    public string CommentOptionSnapshot { get; set; } = "";
    public string FreeComment { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public int SubmittedByUserId { get; set; }
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
}

public static class ReceivedControlStatus
{
    public const string NoReceipt = "NO_RECEIPT";
    public const string ReceiptHigher = "RECEIPT_HIGHER";
    public const string ReceiptLower = "RECEIPT_LOWER";
    public const string Exact = "EXACT";
    public static string Resolve(bool received, int? receiptQty, int actualQty)
    {
        if (!received) return NoReceipt;
        if ((receiptQty ?? 0) > actualQty) return ReceiptHigher;
        if ((receiptQty ?? 0) < actualQty) return ReceiptLower;
        return Exact;
    }
}

public class ReceivedControlEntry
{
    public int Id { get; set; }
    public string ControlNumber { get; set; } = "";
    public int TerminalId { get; set; }
    public int FromTerminalId { get; set; }
    public string FromTerminalSnapshot { get; set; } = "";
    public string UnitReference { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool PalletReceiptReceived { get; set; }
    public int? ReceiptPalletCount { get; set; }
    public int ActualPalletCount { get; set; }
    public string Result { get; set; } = ReceivedControlStatus.NoReceipt;
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public int SubmittedByUserId { get; set; }
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
}

public class ReceivedControlWarning
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public int EntryId { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public int? AcknowledgedByUserId { get; set; }
}

public class AppSettings
{
    public int Id { get; set; } = 1;
    public bool AllowUsersAddDrivers { get; set; } = true;

    public bool LargeInEnabled { get; set; } = true;
    public int LargeInThreshold { get; set; } = 20;
    public bool LargeOutEnabled { get; set; } = true;
    public int LargeOutThreshold { get; set; } = 20;

    public bool RecentVehicleEnabled { get; set; } = true;
    public int RecentVehicleMinutes { get; set; } = 5;
    public bool RecentDriverEnabled { get; set; } = true;
    public int RecentDriverMinutes { get; set; } = 5;

    public bool DuplicateEnabled { get; set; } = true;
    public int DuplicateMinutes { get; set; } = 5;

    public bool RapidSubmissionsEnabled { get; set; } = true;
    public int RapidSubmissionCount { get; set; } = 3;
    public int RapidSubmissionMinutes { get; set; } = 10;

    public bool DailyTotalEnabled { get; set; } = true;
    public int DailyTotalThreshold { get; set; } = 60;

    public bool CancellationWarningEnabled { get; set; } = true;
    public bool CancellationReversedWarningEnabled { get; set; } = true;

    public bool MilestoneNotificationsEnabled { get; set; } = true;
    public int MonthlyMilestoneStep { get; set; } = 100;
    public bool LeaderboardNotificationsEnabled { get; set; } = true;
    public bool BalanceNotificationsEnabled { get; set; } = true;

    // Driver statistics adjustment: every unmatched IN receipt deducts this many pallets.
    public int DriverUnmatchedInDeduction { get; set; } = 15;
}

public class VehicleComplianceRow
{
    public DateOnly Date { get; set; }
    public int VehicleId { get; set; }
    public string Vehicle { get; set; } = "";
    public string Transporter { get; set; } = "";
    public List<int> InDriverIds { get; set; } = [];
    public List<int> OutDriverIds { get; set; } = [];
    public List<string> InDrivers { get; set; } = [];
    public List<string> OutDrivers { get; set; } = [];
    public bool HasIn { get; set; }
    public bool HasOut { get; set; }
    public bool Complete { get; set; }
    public bool IsToday { get; set; }
    public string Status { get; set; } = "";
}

public class StatisticsRow
{
    public string Transporter { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public string PalletType { get; set; } = "";
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int Balance { get; set; }
    public int Movement { get; set; }
}

public class DriverStatisticsRow
{
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicles { get; set; } = "";
    public int InReceipts { get; set; }
    public int OutReceipts { get; set; }
    public int UnmatchedInReceipts { get; set; }
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int RawBalance { get; set; }
    public int Deduction { get; set; }
    public int AdjustedBalance { get; set; }
    public int Movement { get; set; }
}

public class DriverAdjustmentDetail
{
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public DateOnly Date { get; set; }
    public int InReceipts { get; set; }
    public int OutReceipts { get; set; }
    public int UnmatchedInReceipts { get; set; }
    public int Deduction { get; set; }
}

public class DriverLeaderboardRow
{
    public int Rank { get; set; }
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int Balance { get; set; }
    public int Movement { get; set; }
}

public record SubmissionWarningDto(string Type, string Severity, string Message);
public record ReceiptValidation(string? Error, Vehicle? Vehicle, Driver? Driver, List<ReceiptItemRequest> PositiveItems)
{
    public static ReceiptValidation Fail(string error) => new(error, null, null, []);
}

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username, string DisplayName, string Role, int TerminalId, string TerminalCode, bool ShowDriverStatisticsTab, bool ShowDailyCheckTab, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl);
public record QuickDriverRequest(string Name);
public record ReceiptItemRequest(int PalletTypeId, int Quantity);
public record CreateReceiptRequest(string IdempotencyKey, int VehicleId, int DriverId, string Direction, List<ReceiptItemRequest> Items, bool ConfirmWarnings = false, DateOnly? BusinessDate = null);
public record CancelRequest(string Reason);
public record ReverseCancellationRequest(string? Reason);
public record UserPreferenceRequest(bool ShowMilestoneNotifications, bool ShowLeaderboardNotifications, bool ShowBalanceNotifications);

public record AdminTransporterRequest(string Name);
public record AdminVehicleRequest(string VehicleId, int TerminalId, int TransporterId);
public record VehicleTransporterRequest(int TransporterId);
public record VehicleScheduleRequest(List<int>? Days);
public record AdminHolidayRequest(DateOnly Date, string? Name);
public record AdminDriverRequest(string Name, int TerminalId);
public record AdminPalletTypeRequest(string Name, bool UserSelectable);
public record AdminPalletTypeUpdate(bool Active, bool UserSelectable);
public record AdminUserRequest(string Username, string DisplayName, string Password, string Role, int TerminalId, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl, bool ShowDriverStatisticsTab = true, bool ShowDailyCheckTab = true);
public record AdminUserUpdateRequest(string DisplayName, string Role, int TerminalId, bool Active, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl, bool ShowDriverStatisticsTab = true, bool ShowDailyCheckTab = true);
public record AdminPasswordRequest(string Password);
public record AdminTabAccessRequest(bool ShowDriverStatisticsTab, bool ShowDailyCheckTab);
public record AdminActiveRequest(bool Active);
public record AdminSettingsRequest(
    bool AllowUsersAddDrivers,
    bool LargeInEnabled,
    int LargeInThreshold,
    bool LargeOutEnabled,
    int LargeOutThreshold,
    bool RecentVehicleEnabled,
    int RecentVehicleMinutes,
    bool RecentDriverEnabled,
    int RecentDriverMinutes,
    bool DuplicateEnabled,
    int DuplicateMinutes,
    bool RapidSubmissionsEnabled,
    int RapidSubmissionCount,
    int RapidSubmissionMinutes,
    bool DailyTotalEnabled,
    int DailyTotalThreshold,
    bool CancellationWarningEnabled,
    bool CancellationReversedWarningEnabled,
    bool MilestoneNotificationsEnabled,
    int MonthlyMilestoneStep,
    bool LeaderboardNotificationsEnabled,
    bool BalanceNotificationsEnabled,
    int? DriverUnmatchedInDeduction);


public record AdminTerminalRequest(string? Code, string? Name, string? Aliases);
public record AdminTerminalUpdateRequest(string? Code, string? Name, string? Aliases, bool Active);
public record AdminLinehaulCommentRequest(int? TerminalId, string? Text);
public record CreateLinehaulReceiptRequest(string? UnitReference, string? PalletReceiptNumber, int PalletCount, int FromTerminalId, int ToTerminalId, int? CommentOptionId, string? FreeComment, DateOnly? BusinessDate);
public record CreateReceivedControlRequest(int FromTerminalId, string? UnitReference, string? Comment, bool PalletReceiptReceived, int? ReceiptPalletCount, int ActualPalletCount, DateOnly? BusinessDate);
public sealed record ImportIssue(int Row, string Message);
public sealed record ImportDataRow(int RowNumber, Dictionary<string, string> Values);
public sealed record ImportGrid(List<string> Headers, List<ImportDataRow> Rows);
public sealed record PendingLinehaulImport(int RowNumber, DateOnly BusinessDate, string UnitReference, string PalletReceiptNumber, int PalletCount, int FromTerminalId, int ToTerminalId, string FromTerminalCode, string ToTerminalCode, string StandardComment, string FreeComment, string DuplicateKey);
public sealed record PendingReceivedControlImport(int RowNumber, DateOnly BusinessDate, int FromTerminalId, string FromTerminalCode, string UnitReference, bool PalletReceiptReceived, int? ReceiptPalletCount, int ActualPalletCount, string Comment, string DuplicateKey);

public partial class Program { }
