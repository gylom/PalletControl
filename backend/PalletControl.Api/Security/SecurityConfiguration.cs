using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

public sealed record SecurityRuntimeOptions(
    bool RequireHttps,
    int JwtLifetimeMinutes,
    int MaxRequestBodyMb,
    int ApiRequestsPerMinute,
    int LoginRequestsPerMinute,
    int LoginFailureLimit,
    int LoginLockoutMinutes,
    string[] AllowedOrigins);

public static class SecurityConfiguration
{
    private const string DevelopmentJwtPrefix = "DEVELOPMENT-ONLY";

    public static SecurityRuntimeOptions AddPalletSecurity(
        this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var environment = builder.Environment;

        var jwtKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey) && environment.IsDevelopment())
            jwtKey = "DEVELOPMENT-ONLY-PalletControl-Key-Do-Not-Use-In-Production-2026";
        if (string.IsNullOrWhiteSpace(jwtKey))
            throw new InvalidOperationException("Jwt:Key is missing. In production set Jwt__Key as a server environment variable.");
        if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 bytes.");
        if (environment.IsProduction() && jwtKey.StartsWith(DevelopmentJwtPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The development JWT key cannot be used in Production. Set Jwt__Key on the server.");

        var jwtIssuer = config["Jwt:Issuer"] ?? "PalletControl";
        var jwtAudience = config["Jwt:Audience"] ?? "PalletControl";
        var jwtLifetimeMinutes = Math.Clamp(config.GetValue<int?>("Jwt:LifetimeMinutes") ?? 720, 15, 1440);
        var maxRequestBodyMb = Math.Clamp(config.GetValue<int?>("Security:MaxRequestBodyMb") ?? 20, 1, 100);
        var apiRequestsPerMinute = Math.Clamp(config.GetValue<int?>("Security:ApiRequestsPerMinute") ?? 600, 60, 10000);
        var loginRequestsPerMinute = Math.Clamp(config.GetValue<int?>("Security:LoginRequestsPerMinute") ?? 10, 3, 100);
        var loginFailureLimit = Math.Clamp(config.GetValue<int?>("Security:LoginFailureLimit") ?? 5, 3, 20);
        var loginLockoutMinutes = Math.Clamp(config.GetValue<int?>("Security:LoginLockoutMinutes") ?? 15, 1, 120);
        var requireHttps = config.GetValue<bool?>("Security:RequireHttps") ?? environment.IsProduction();
        var allowedOrigins = config.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];

        var runtime = new SecurityRuntimeOptions(
            requireHttps,
            jwtLifetimeMinutes,
            maxRequestBodyMb,
            apiRequestsPerMinute,
            loginRequestsPerMinute,
            loginFailureLimit,
            loginLockoutMinutes,
            allowedOrigins);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<LoginAttemptGuard>();
        builder.Services.AddSingleton<JwtTokenService>();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = maxRequestBodyMb * 1024L * 1024L;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        builder.Services.AddCors(options => options.AddPolicy("ui", policy =>
        {
            var origins = allowedOrigins.ToList();
            if (environment.IsDevelopment())
            {
                origins.Add("http://localhost:5173");
                origins.Add("https://localhost:5173");
                origins.Add("http://127.0.0.1:5173");
            }

            origins = origins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (origins.Count > 0)
                policy.WithOrigins(origins.ToArray()).AllowAnyHeader().AllowAnyMethod();
        }));

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var key = ClientKey(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = apiRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.AddPolicy("login", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(httpContext), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { message = "Too many requests. Please try again later." }, cancellationToken);
            };
        });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
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

                options.Events = new JwtBearerEvents
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
                        var currentUser = await db.Users.AsNoTracking().Include(x => x.Terminal)
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

                        ReplaceClaim(identity, ClaimTypes.Role, currentUser.Role);

                        // SuperAdmin may work in SRD, ARE or KRS without changing the account's
                        // stored/home terminal. The active terminal is carried in the signed JWT,
                        // but is revalidated against the database on every request. Other roles
                        // are always forced back to their assigned terminal.
                        var activeTerminalId = currentUser.TerminalId;
                        var activeTerminalCode = currentUser.Terminal?.Code ?? "";
                        if (currentUser.Role == Roles.SuperAdmin &&
                            int.TryParse(principal.FindFirstValue("terminalId"), out var requestedTerminalId))
                        {
                            var requestedTerminal = await db.Terminals.AsNoTracking()
                                .SingleOrDefaultAsync(x => x.Id == requestedTerminalId && x.IsOperatingTerminal && x.Active);
                            if (requestedTerminal is not null)
                            {
                                activeTerminalId = requestedTerminal.Id;
                                activeTerminalCode = requestedTerminal.Code;
                            }
                        }

                        ReplaceClaim(identity, "terminalId", activeTerminalId.ToString(CultureInfo.InvariantCulture));
                        ReplaceClaim(identity, "terminalCode", activeTerminalCode);
                        var isViewer = currentUser.Role == Roles.Viewer;
                        ReplaceClaim(identity, "moduleInternal", isViewer || currentUser.HasInternalPalletAccounting ? "1" : "0");
                        ReplaceClaim(identity, "moduleLinehaul", !isViewer && currentUser.HasLinehaul ? "1" : "0");
                        ReplaceClaim(identity, "moduleReceivedControl", !isViewer && currentUser.HasReceivedControl ? "1" : "0");
                    }
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("InternalModule", policy => policy.RequireClaim("moduleInternal", "1"));
            options.AddPolicy("InternalWrite", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindFirstValue("moduleInternal") == "1" && !ctx.User.IsInRole(Roles.Viewer)));
            options.AddPolicy("InternalElevated", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindFirstValue("moduleInternal") == "1" &&
                (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.Admin) ||
                 ctx.User.IsInRole(Roles.LegacyTerminalAdmin) || ctx.User.IsInRole(Roles.Superuser))));
            options.AddPolicy("LinehaulModule", policy => policy.RequireClaim("moduleLinehaul", "1"));
            options.AddPolicy("LinehaulAdmin", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindFirstValue("moduleLinehaul") == "1" &&
                (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.Admin) || ctx.User.IsInRole(Roles.LegacyTerminalAdmin))));
            options.AddPolicy("ReceivedControlModule", policy => policy.RequireClaim("moduleReceivedControl", "1"));
            options.AddPolicy("ReceivedControlAdmin", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindFirstValue("moduleReceivedControl") == "1" &&
                (ctx.User.IsInRole(Roles.SuperAdmin) || ctx.User.IsInRole(Roles.Admin) || ctx.User.IsInRole(Roles.LegacyTerminalAdmin))));
        });

        return runtime;
    }

    public static void UsePalletSecurity(this WebApplication app)
    {
        var runtime = app.Services.GetRequiredService<SecurityRuntimeOptions>();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { message = "An unexpected server error occurred." });
            }));
        }

        if (runtime.RequireHttps)
        {
            if (!app.Environment.IsDevelopment()) app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                if (!app.Environment.IsDevelopment())
                {
                    context.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                        "script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
                }
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });

        app.UseRateLimiter();
        app.UseCors("ui");
        app.UseAuthentication();
        app.UseAuthorization();
    }

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void ReplaceClaim(ClaimsIdentity identity, string claimType, string value)
    {
        foreach (var claim in identity.FindAll(claimType).ToList()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(claimType, value));
    }
}

public sealed class LoginAttemptGuard
{
    private sealed record Entry(int Failures, DateTime FirstFailureUtc, DateTime? LockedUntilUtc);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecurityRuntimeOptions _options;

    public LoginAttemptGuard(SecurityRuntimeOptions options) => _options = options;

    public bool IsBlocked(string username, string clientKey, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var key = Key(username, clientKey);
        if (!_entries.TryGetValue(key, out var entry) || entry.LockedUntilUtc is null) return false;
        if (entry.LockedUntilUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        retryAfter = entry.LockedUntilUtc.Value - DateTime.UtcNow;
        return true;
    }

    public void RecordFailure(string username, string clientKey)
    {
        var now = DateTime.UtcNow;
        var key = Key(username, clientKey);
        _entries.AddOrUpdate(key,
            _ => new Entry(1, now, null),
            (_, old) =>
            {
                if (now - old.FirstFailureUtc > TimeSpan.FromMinutes(_options.LoginLockoutMinutes))
                    return new Entry(1, now, null);
                var failures = old.Failures + 1;
                var locked = failures >= _options.LoginFailureLimit
                    ? now.AddMinutes(_options.LoginLockoutMinutes)
                    : old.LockedUntilUtc;
                return new Entry(failures, old.FirstFailureUtc, locked);
            });
    }

    public void RecordSuccess(string username, string clientKey) => _entries.TryRemove(Key(username, clientKey), out _);

    private static string Key(string username, string clientKey) => $"{clientKey}|{username.Trim().ToLowerInvariant()}";
}

public static class PasswordSecurity
{
    public static string? Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "Password is required.";
        if (password.Length < 10) return "Password must be at least 10 characters.";
        var groups = 0;
        if (password.Any(char.IsLower)) groups++;
        if (password.Any(char.IsUpper)) groups++;
        if (password.Any(char.IsDigit)) groups++;
        if (password.Any(ch => !char.IsLetterOrDigit(ch))) groups++;
        return groups < 3 ? "Password must use at least 3 of: lowercase, uppercase, number, special character." : null;
    }
}


public sealed class JwtTokenService
{
    private readonly IConfiguration _configuration;
    private readonly SecurityRuntimeOptions _security;

    public JwtTokenService(IConfiguration configuration, SecurityRuntimeOptions security)
    {
        _configuration = configuration;
        _security = security;
    }

    public string CreateToken(AppUser user, Terminal? activeTerminal = null)
    {
        var key = _configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
            key = "DEVELOPMENT-ONLY-PalletControl-Key-Do-Not-Use-In-Production-2026";

        var tokenTerminal = user.Role == Roles.SuperAdmin && activeTerminal is not null
            ? activeTerminal
            : user.Terminal;
        var tokenTerminalId = tokenTerminal?.Id ?? user.TerminalId;
        var tokenTerminalCode = tokenTerminal?.Code ?? user.Terminal?.Code ?? "";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("terminalId", tokenTerminalId.ToString(CultureInfo.InvariantCulture)),
            new("terminalCode", tokenTerminalCode),
            new("moduleInternal", user.Role == Roles.Viewer || user.HasInternalPalletAccounting ? "1" : "0"),
            new("moduleLinehaul", user.Role != Roles.Viewer && user.HasLinehaul ? "1" : "0"),
            new("moduleReceivedControl", user.Role != Roles.Viewer && user.HasReceivedControl ? "1" : "0")
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _configuration["Jwt:Issuer"] ?? "PalletControl",
            _configuration["Jwt:Audience"] ?? "PalletControl",
            claims,
            expires: DateTime.UtcNow.AddMinutes(_security.JwtLifetimeMinutes),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
