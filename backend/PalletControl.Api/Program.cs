using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(
        builder.Configuration.GetConnectionString("Default"),
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
    });

builder.Services.AddAuthorization();

var app = builder.Build();
app.UseCors("ui");
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    Seed(db);
}

app.MapGet("/", () => Results.Ok(new
{
    name = "Pallet Control API",
    status = "running",
    version = "5.4.2"
}));

// Public health endpoint. If this endpoint answers, the API process itself is online.
app.MapGet("/api/health", async (AppDbContext db) =>
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
                responseMs = (DateTime.UtcNow - started).TotalMilliseconds
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // A real read verifies more than simply opening a connection.
        _ = await db.Settings.AsNoTracking().CountAsync();

        return Results.Ok(new
        {
            status = "healthy",
            api = new { status = "online" },
            database = new { status = "online", provider = "SQLite" },
            checkedAtUtc = DateTime.UtcNow,
            responseMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1)
        });
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
        new("terminalCode", user.Terminal?.Code ?? "")
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
        user.Terminal?.Code ?? ""));
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
        terminalCode = user.Terminal?.Code ?? ""
    });
}).RequireAuthorization();

app.MapGet("/api/me/settings", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var user = await db.Users.FindAsync(UserId(principal));
    if (user is null) return Results.NotFound();

    return Results.Ok(new
    {
        user.ShowMilestoneNotifications,
        user.ShowLeaderboardNotifications,
        user.ShowBalanceNotifications
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

    return Results.Ok(new
    {
        user.ShowMilestoneNotifications,
        user.ShowLeaderboardNotifications,
        user.ShowBalanceNotifications
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

    var settings = await db.Settings.AsNoTracking().SingleAsync();

    return Results.Ok(new
    {
        vehicles,
        drivers,
        palletTypes,
        settings.AllowUsersAddDrivers
    });
}).RequireAuthorization();

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
}).RequireAuthorization();

app.MapPost("/api/drivers/quick-add", async (
    QuickDriverRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var settings = await db.Settings.SingleAsync();
    if (!settings.AllowUsersAddDrivers)
        return Results.Forbid();

    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Driver name is required." });

    var terminalId = TerminalId(principal);
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
}).RequireAuthorization();

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
}).RequireAuthorization();

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
}).RequireAuthorization();

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

    if (role != Roles.Admin)
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
}).RequireAuthorization();

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

    var settings = await db.Settings.AsNoTracking().SingleAsync();
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
}).RequireAuthorization(AdminOrSuperuser());

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

    var settings = await db.Settings.AsNoTracking().SingleAsync();
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
}).RequireAuthorization(AdminOrSuperuser());

app.MapGet("/api/statistics/options", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var terminalId = TerminalId(principal);
    var role = Role(principal);

    var transporterQuery = db.Transporters.AsNoTracking().Where(x => x.Active);
    var vehicleQuery = db.Vehicles.AsNoTracking().Where(x => x.Active && x.TransporterId != null).Include(x => x.Transporter).AsQueryable();
    var driverQuery = db.Drivers.AsNoTracking().Where(x => x.Active).AsQueryable();

    if (role != Roles.Admin)
    {
        vehicleQuery = vehicleQuery.Where(x => x.TerminalId == terminalId);
        driverQuery = driverQuery.Where(x => x.TerminalId == terminalId);
        var transporterIds = await vehicleQuery
            .Where(x => x.TransporterId != null)
            .Select(x => x.TransporterId!.Value)
            .Distinct()
            .ToListAsync();
        transporterQuery = transporterQuery.Where(x => transporterIds.Contains(x.Id));
    }

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
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    var palletTypes = await db.PalletTypes.AsNoTracking()
        .Where(x => x.Active)
        .OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name })
        .ToListAsync();

    return Results.Ok(new { transporters, vehicles, drivers, palletTypes });
}).RequireAuthorization();

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
    var role = Role(principal);

    var query = db.Receipts
        .AsNoTracking()
        .Include(r => r.Vehicle).ThenInclude(v => v!.Transporter)
        .Include(r => r.Driver)
        .Include(r => r.Items).ThenInclude(i => i.PalletType)
        .Where(r =>
            r.Status == ReceiptStatus.Active &&
            r.BusinessDate >= from.Value &&
            r.BusinessDate <= to.Value);

    if (role != Roles.Admin)
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
}).RequireAuthorization();

app.MapGet("/api/statistics/best-drivers", async (
    string? period,
    int? palletTypeId,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var (from, to, normalizedPeriod) = ResolvePeriod(period);
    var terminalId = TerminalId(principal);
    var role = Role(principal);

    var query = db.Receipts
        .AsNoTracking()
        .Include(x => x.Items)
        .Where(x => x.Status == ReceiptStatus.Active &&
                    x.BusinessDate >= from && x.BusinessDate <= to);

    if (role != Roles.Admin)
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
}).RequireAuthorization();

app.MapGet("/api/warnings", async (
    bool? unacknowledgedOnly,
    int? limit,
    string? search,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var role = Role(principal);
    var terminalId = TerminalId(principal);
    var take = Math.Clamp(limit ?? 100, 1, 500);

    var query = db.WarningEvents
        .AsNoTracking()
        .Include(x => x.Receipt)
        .Include(x => x.TriggeredByUser)
        .Include(x => x.AcknowledgedByUser)
        .AsQueryable();

    if (role != Roles.Admin)
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

    var openCountQuery = db.WarningEvents.AsNoTracking().Where(x => x.AcknowledgedAtUtc == null);
    if (role != Roles.Admin)
        openCountQuery = openCountQuery.Where(x => x.TerminalId == terminalId);

    return Results.Ok(new { openCount = await openCountQuery.CountAsync(), warnings = rows });
}).RequireAuthorization(AdminOrSuperuser());

app.MapPost("/api/warnings/{id:int}/acknowledge", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var warning = await db.WarningEvents.FindAsync(id);
    if (warning is null) return Results.NotFound();

    if (Role(principal) != Roles.Admin && warning.TerminalId != TerminalId(principal))
        return Results.Forbid();

    if (warning.AcknowledgedAtUtc == null)
    {
        warning.AcknowledgedAtUtc = DateTime.UtcNow;
        warning.AcknowledgedByUserId = UserId(principal);
        await db.SaveChangesAsync();
        await Audit(db, principal, "WARNING_ACK", $"Acknowledged warning #{warning.Id}");
    }

    return Results.Ok();
}).RequireAuthorization(AdminOrSuperuser());

app.MapGet("/api/export", async (
    DateOnly from,
    DateOnly to,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    if (to < from)
        return Results.BadRequest(new { message = "To date cannot be before From date." });

    var terminalId = TerminalId(principal);
    var role = Role(principal);

    var q = db.Receipts
        .AsNoTracking()
        .Include(x => x.Terminal)
        .Include(x => x.SubmittedByUser)
        .Include(x => x.Items).ThenInclude(x => x.PalletType)
        .Where(x => x.BusinessDate >= from && x.BusinessDate <= to);

    if (role != Roles.Admin)
        q = q.Where(x => x.TerminalId == terminalId);

    var rows = await q.OrderBy(x => x.BusinessDate).ThenBy(x => x.SubmittedAtUtc).ToListAsync();
    var sb = new StringBuilder();
    sb.AppendLine("Receipt ID,Terminal,Transporter,Vehicle,Date,Driver,Direction,Pallet Type,Quantity,Submitted At UTC,Submitted By,Status");

    foreach (var r in rows)
    foreach (var i in r.Items)
    {
        sb.AppendLine(string.Join(",",
            Csv(r.ReceiptNumber),
            Csv(r.Terminal?.Code ?? ""),
            Csv(r.TransporterSnapshot),
            Csv(r.VehicleSnapshot),
            Csv(r.BusinessDate.ToString("yyyy-MM-dd")),
            Csv(r.DriverSnapshot),
            Csv(r.Direction),
            Csv(i.PalletType?.Name ?? ""),
            i.Quantity.ToString(CultureInfo.InvariantCulture),
            Csv(r.SubmittedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")),
            Csv(r.SubmittedByUser?.Username ?? ""),
            Csv(r.Status)));
    }

    await Audit(db, principal, "EXPORT", $"Exported {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
    return Results.File(
        Encoding.UTF8.GetBytes(sb.ToString()),
        "text/csv",
        $"PalletExport_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.csv");
}).RequireAuthorization(AdminOrSuperuser());

// ---------------- ADMIN ----------------

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
                x.ShowBalanceNotifications
            }).ToListAsync(),
        settings
    });
}).RequireAuthorization(AdminOnly());


app.MapGet("/api/admin/transporters", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        transporters = await db.Transporters.AsNoTracking().OrderBy(x => x.Name).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/vehicles", async (AppDbContext db) =>
{
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
            }).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/drivers", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        terminals = await db.Terminals.AsNoTracking().OrderBy(x => x.Code).ToListAsync(),
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
            }).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/pallet-types", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        palletTypes = await db.PalletTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/users", async (AppDbContext db) =>
{
    return Results.Ok(new
    {
        terminals = await db.Terminals.AsNoTracking().OrderBy(x => x.Code).ToListAsync(),
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
                x.ShowBalanceNotifications
            }).ToListAsync()
    });
}).RequireAuthorization(AdminOnly());

app.MapGet("/api/admin/settings", async (AppDbContext db) =>
{
    return Results.Ok(await db.Settings.AsNoTracking().SingleAsync());
}).RequireAuthorization(AdminOnly());

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
}).RequireAuthorization(AdminOnly());

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
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/vehicles", async (
    AdminVehicleRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
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
        Active = true
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

    if (!await db.Transporters.AnyAsync(x => x.Id == req.TransporterId && x.Active))
        return Results.BadRequest(new { message = "Transporter not found." });

    row.TransporterId = req.TransporterId;
    await db.SaveChangesAsync();
    await Audit(db, principal, "VEHICLE_TRANSPORTER", $"Changed transporter for {row.VehicleId}");
    return Results.Ok();
}).RequireAuthorization(AdminOnly());

app.MapDelete("/api/admin/vehicles/{id:int}", async (
    int id,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var row = await db.Vehicles.FindAsync(id);
    if (row is null) return Results.NotFound();

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
    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { message = "Driver name is required." });

    if (await db.Drivers.AnyAsync(x => x.TerminalId == req.TerminalId && x.Name.ToLower() == name.ToLower()))
        return Results.BadRequest(new { message = "Driver already exists for this terminal." });

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

    var name = row.Name;
    db.Drivers.Remove(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "DRIVER_DELETE", $"Deleted driver {name}");
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
}).RequireAuthorization(AdminOnly());

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
}).RequireAuthorization(AdminOnly());

app.MapPost("/api/admin/users", async (
    AdminUserRequest req,
    ClaimsPrincipal principal,
    AppDbContext db) =>
{
    var username = req.Username.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Username and password are required." });
    if (!ValidRole(req.Role))
        return Results.BadRequest(new { message = "Invalid role." });
    if (await db.Users.AnyAsync(x => x.Username == username))
        return Results.BadRequest(new { message = "Username already exists." });

    var row = new AppUser
    {
        Username = username,
        DisplayName = req.DisplayName.Trim(),
        Role = req.Role,
        TerminalId = req.TerminalId,
        Active = true
    };
    row.PasswordHash = new PasswordHasher<AppUser>().HashPassword(row, req.Password);
    db.Users.Add(row);
    await db.SaveChangesAsync();
    await Audit(db, principal, "USER_CREATE", $"Created user {username} ({req.Role})");
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
    if (!ValidRole(req.Role))
        return Results.BadRequest(new { message = "Invalid role." });

    row.DisplayName = req.DisplayName.Trim();
    row.Role = req.Role;
    row.TerminalId = req.TerminalId;
    row.Active = req.Active;
    await db.SaveChangesAsync();
    await Audit(db, principal, "USER_UPDATE", $"Updated user {row.Username}");
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

    await db.SaveChangesAsync();
    await Audit(db, principal, "SETTINGS_UPDATE", "Updated warning and notification settings");
    return Results.Ok(s);
}).RequireAuthorization(AdminOnly());

app.Run("http://0.0.0.0:5000");

// ---------------- HELPERS ----------------

static AuthorizeAttribute AdminOnly() => new() { Roles = Roles.Admin };
static AuthorizeAttribute AdminOrSuperuser() => new() { Roles = $"{Roles.Admin},{Roles.Superuser}" };

static int UserId(ClaimsPrincipal principal) =>
    int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? throw new InvalidOperationException("User ID claim missing."));

static int TerminalId(ClaimsPrincipal principal) =>
    int.Parse(principal.FindFirstValue("terminalId")
              ?? throw new InvalidOperationException("Terminal claim missing."));

static string Role(ClaimsPrincipal principal) =>
    principal.FindFirstValue(ClaimTypes.Role) ?? Roles.User;

static bool ValidRole(string role) => role is Roles.Admin or Roles.Superuser or Roles.User;

static List<int> ParseIds(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var id) ? id : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();

static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

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

    var positiveItems = req.Items
        .Where(x => x.Quantity > 0)
        .GroupBy(x => x.PalletTypeId)
        .Select(g => new ReceiptItemRequest(g.Key, g.Sum(x => x.Quantity)))
        .ToList();

    if (positiveItems.Count == 0)
        return ReceiptValidation.Fail("Enter at least one pallet quantity.");

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
    var settings = await db.Settings.AsNoTracking().SingleAsync();
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
    var settings = await db.Settings.AsNoTracking().SingleAsync();
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

    if (!isFreshDatabase)
    {
        // v5.4.3 adds ARE as a real terminal. Add it once to existing databases
        // without recreating any master data that an Admin intentionally deleted.
        if (!db.Terminals.Any(x => x.Code == "ARE"))
        {
            db.Terminals.Add(new Terminal { Code = "ARE", Name = "Arendal" });
            db.SaveChanges();
        }
        return;
    }

    var srd = new Terminal { Code = "SRD", Name = "Sandefjord" };
    var krs = new Terminal { Code = "KRS", Name = "Kristiansand" };
    var are = new Terminal { Code = "ARE", Name = "Arendal" };
    db.Terminals.AddRange(srd, krs, are);

    db.PalletTypes.AddRange(
        new PalletType { Name = "EUR pallet", Active = true, UserSelectable = true },
        new PalletType { Name = "Half pallet", Active = true, UserSelectable = true },
        new PalletType { Name = "One-time pallet", Active = true, UserSelectable = true });

    var telemark = new Transporter { Name = "Telemark TransportService", Active = true };
    var frode = new Transporter { Name = "Frode Bjønnes", Active = true };
    db.Transporters.AddRange(telemark, frode);

    db.SaveChanges();

    db.Vehicles.AddRange(
        new Vehicle { VehicleId = "VTM3241", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTM3755", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTA3754", TerminalId = srd.Id, TransporterId = telemark.Id, Active = true },
        new Vehicle { VehicleId = "VTM3771", TerminalId = srd.Id, TransporterId = frode.Id, Active = true });

    db.Drivers.AddRange(
        new Driver { Name = "Test Driver", TerminalId = srd.Id, Active = true },
        new Driver { Name = "John Smith", TerminalId = srd.Id, Active = true });

    AddUser("admin", "Administrator", Roles.Admin, "admin123");
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

// ---------------- TYPES ----------------

public static class Roles
{
    public const string Admin = "Admin";
    public const string Superuser = "Superuser";
    public const string User = "User";
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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.VehicleId).IsUnique();
        b.Entity<Transporter>().HasIndex(x => x.Name).IsUnique();
        b.Entity<PalletType>().HasIndex(x => x.Name).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.ReceiptNumber).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<WarningEvent>().HasIndex(x => new { x.TerminalId, x.AcknowledgedAtUtc, x.CreatedAtUtc });

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

    [JsonIgnore] public Terminal? Terminal { get; set; }
}

public class Terminal
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
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
    [JsonIgnore] public Terminal? Terminal { get; set; }
    public int? TransporterId { get; set; }
    [JsonIgnore] public Transporter? Transporter { get; set; }
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
public record LoginResponse(string Token, string Username, string DisplayName, string Role, int TerminalId, string TerminalCode);
public record QuickDriverRequest(string Name);
public record ReceiptItemRequest(int PalletTypeId, int Quantity);
public record CreateReceiptRequest(string IdempotencyKey, int VehicleId, int DriverId, string Direction, List<ReceiptItemRequest> Items, bool ConfirmWarnings = false, DateOnly? BusinessDate = null);
public record CancelRequest(string Reason);
public record ReverseCancellationRequest(string? Reason);
public record UserPreferenceRequest(bool ShowMilestoneNotifications, bool ShowLeaderboardNotifications, bool ShowBalanceNotifications);

public record AdminTransporterRequest(string Name);
public record AdminVehicleRequest(string VehicleId, int TerminalId, int TransporterId);
public record VehicleTransporterRequest(int TransporterId);
public record AdminDriverRequest(string Name, int TerminalId);
public record AdminPalletTypeRequest(string Name, bool UserSelectable);
public record AdminPalletTypeUpdate(bool Active, bool UserSelectable);
public record AdminUserRequest(string Username, string DisplayName, string Password, string Role, int TerminalId);
public record AdminUserUpdateRequest(string DisplayName, string Role, int TerminalId, bool Active);
public record AdminPasswordRequest(string Password);
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
    bool BalanceNotificationsEnabled);

public partial class Program { }
