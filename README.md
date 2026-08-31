# PalletControl v5.9.1

Production-hardening, monitoring and deployment update based directly on the uploaded `PalletControl-main (4).zip` working v5.8.11 project.

## v5.9.1 SuperAdmin administration fix

v5.9.1 fixes the SuperAdmin regressions found after the first v5.9.0 test:

- the Admin page now has one explicit **Manage terminal** selector for terminal-specific administration
- SuperAdmin can target SRD, ARE or KRS for Users, Vehicles, Drivers, Transporters, Terminal settings, Linehaul comments and Linehaul/Mottatt locations
- Users/Vehicles/Drivers Admin APIs now accept and validate an explicit `terminalId` instead of silently falling back to the current session terminal
- creating a user after selecting ARE/KRS keeps that selected terminal instead of resetting to the first terminal in the returned list
- the header selector is labelled **Active** to distinguish the operational terminal from the Admin **Manage terminal** selector
- `/api/version` was added so it is easy to confirm which backend process is actually running
- startup logs explicitly confirm that `/api/admin/system/overview` was mapped
- the non-destructive smoke test now checks System Health, Admin scope for every terminal, and SRD/ARE/KRS active-terminal switching

If the frontend shows v5.9.1 but `/api/version` reports an older version, stop the old `dotnet run` process and restart the backend. A successful `dotnet build` does not replace an already-running process.

## Production/security/monitoring work retained from v5.9.0

### Production-safe first start
A fresh database now creates only the minimum structural data needed by the application:

- operating terminals: SRD, ARE and KRS
- standard pallet types: EUR pallet, Half pallet, One-time pallet
- one bootstrap `SuperAdmin`

It does **not** create demo Admin/Superuser/User accounts, demo transporters, vehicles or driver names.

The login page no longer shows or pre-fills demo credentials.

The first SuperAdmin defaults to username `superadmin`. Its password is never stored in Git. Before the first startup set:

```powershell
$env:BootstrapAdmin__Password = "Your-Strong-Password-Here"
```

The password must be at least 10 characters and use at least 3 of: lowercase, uppercase, number, special character.

For production use the server setup script. After the first successful login, remove the bootstrap password from the server environment with:

```powershell
.\deploy\Clear-BootstrapPassword.ps1
```

Existing databases and existing users are not replaced by the seed process.

### Security hardening

- JWT signing key validation; a development/example key is rejected in Production.
- JWT roles, terminal and module access are refreshed from the SQLite user record on each authenticated request.
- Global request rate limiting per client IP.
- Separate stricter login rate limit.
- Failed-login lockout by username + client IP.
- Production exception responses do not expose stack traces.
- Browser security headers: CSP, `X-Content-Type-Options`, `X-Frame-Options`, referrer policy and permissions policy.
- Optional/production HTTPS redirection and HSTS.
- Kestrel server header disabled.
- Configurable request-body limit.
- CORS restricted to explicitly configured origins; production is intended to be same-origin through IIS.
- Stronger password validation for new/reset/self-changed passwords.
- `AllowedHosts` is no longer `*` in the checked-in configuration.

For initial intranet deployment you can run HTTP while the IIS hostname/certificate is being prepared. Before Internet exposure, use HTTPS and set `Security__RequireHttps=true`. Security hardening reduces risk but no web application can be guaranteed impossible to compromise; keep Windows/.NET/npm dependencies patched and restrict network access as tightly as practical.

### SuperAdmin terminal switching

`SuperAdmin` can switch the active operating terminal between **SRD**, **ARE** and **KRS** from the header without logging out. The selected terminal is stored in the signed session token and revalidated by the backend on every request. It does not change the SuperAdmin account's stored/home terminal and does not affect other logged-in sessions.

Regular Admin, Superuser, User and Viewer accounts remain restricted to their configured scope.

### SuperAdmin System Health

A new **Admin → System health** category is visible only to `SuperAdmin`.

It includes:

- backend uptime
- CPU usage graph
- process RAM graph
- managed memory
- requests/minute graph
- average API response-time graph
- HTTP 4xx / 5xx totals
- 401 / 403 / 429 security counters
- active users seen in the last 15 minutes
- most-used endpoints, average response time and error counts
- recent server exception summaries
- SQLite connection and `PRAGMA quick_check`
- live database path and size
- backup directory, interval, retention, latest backup and backup count
- free disk space
- registrations/activity today
- current security configuration status
- OpenTelemetry status
- manual **Backup now** action

Secrets such as JWT keys and passwords are never returned by the monitoring API.

The built-in graph history is in memory and clears when the backend restarts.

### OpenTelemetry

v5.9.1 uses OpenTelemetry 1.18.0 packages for ASP.NET Core tracing, outgoing HTTP tracing, runtime metrics, OTLP exporting and optional OTLP logging.

No external monitoring server is required for the in-app System Health page. To send telemetry to an OpenTelemetry Collector / Grafana stack later, configure for example:

```text
OpenTelemetry__OtlpEndpoint=http://127.0.0.1:4317
```

If no OTLP endpoint is configured, PalletControl still provides its own in-app monitoring graphs.

### Backend code structure

The backend is no longer entirely contained in `Program.cs`.

```text
backend/PalletControl.Api/
├── Data/
│   ├── Contracts.cs
│   └── DomainModel.cs
├── Endpoints/
│   └── SystemEndpoints.cs
├── Infrastructure/
│   ├── DatabaseBackup.cs
│   └── DatabaseConfiguration.cs
├── Observability/
│   ├── ObservabilityConfiguration.cs
│   └── SystemTelemetryService.cs
├── Security/
│   └── SecurityConfiguration.cs
├── Program.cs
├── appsettings.json
└── appsettings.Development.json
```

Operational routes remain in `Program.cs` for this release to avoid a risky all-at-once rewrite of working receipt/statistics/Linehaul logic. New infrastructure is separated, and later endpoint groups can be moved out incrementally.

## GitHub + IIS deployment model

Recommended server layout:

```text
C:\PalletControl\                   GitHub checkout / source code
C:\inetpub\PalletControl\          IIS published application
C:\PalletControlData\              live SQLite data
    palletcontrol.db
C:\PalletControlBackups\           SQLite backups
```

The database and backups are outside both Git and the IIS publish directory. A normal application update therefore cannot overwrite the live database.

### Server requirements

Install on the Windows Server:

1. Git
2. Node.js / npm
3. .NET 10 SDK
4. ASP.NET Core 10 Hosting Bundle
5. IIS + IIS Management Tools

The .NET SDK is needed because the update script performs `dotnet publish` on the server. The Hosting Bundle is needed for IIS hosting.

## First server setup

Clone the GitHub repository to:

```powershell
C:\PalletControl
```

Open **PowerShell as Administrator** and run:

```powershell
cd C:\PalletControl
Set-ExecutionPolicy -Scope Process Bypass

.\deploy\Setup-Server.ps1 -HostName "palletcontrol.your-internal-domain"
```

The script:

- checks IIS/.NET/Git/Node requirements
- creates the source, publish, database and backup directories
- generates a random JWT signing key
- asks securely for the first SuperAdmin password
- configures production database/backup paths as server environment settings
- sets `AllowedHosts`
- creates the IIS application pool and HTTP site

Then deploy the current GitHub version:

```powershell
.\deploy\Update-PalletControl.ps1
```

Sign in once as:

```text
Username: superadmin
Password: the password entered during Setup-Server.ps1
```

After confirming that login works:

```powershell
.\deploy\Clear-BootstrapPassword.ps1
```

This removes the bootstrap password from the machine environment. It does not change or delete the created SuperAdmin account.

## Updating PalletControl through GitHub

Normal development PC workflow:

```powershell
git add .
git commit -m "Describe update"
git push origin main
```

On the PalletControl server:

```powershell
cd C:\PalletControl
.\deploy\Update-PalletControl.ps1
```

The update script performs:

```text
git fetch / pull --ff-only
        ↓
npm ci
npm run build
        ↓
frontend/dist → backend/wwwroot
        ↓
dotnet publish -c Release
        ↓
stop PalletControl IIS app pool
        ↓
replace C:\inetpub\PalletControl
        ↓
start app pool
```

It deliberately refuses to pull if the server Git checkout contains uncommitted local changes.

## Production configuration

Do not put production secrets in GitHub.

Important environment settings:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Default=Data Source=C:\PalletControlData\palletcontrol.db;Cache=Shared
Database__BackupDirectory=C:\PalletControlBackups
Jwt__Key=<random secret generated on server>
AllowedHosts=<your IIS hostname>
Security__RequireHttps=false   # initial intranet HTTP only
OpenTelemetry__OtlpEndpoint=  # optional
```

Once IIS has a trusted HTTPS certificate/binding:

```powershell
[Environment]::SetEnvironmentVariable('Security__RequireHttps','true','Machine')
iisreset
```

For external Internet exposure also restrict Windows Firewall/network access as appropriate and use a trusted HTTPS certificate. Do not expose SQLite files or backup folders as IIS content.

## Non-destructive smoke test

The old smoke test no longer contains demo usernames/passwords and no longer creates/cancels receipts. Run it with a real account after the backend is running:

```powershell
.\smoke-test.ps1 -BaseUrl "http://localhost:5000/api"
```

For a SuperAdmin it also verifies System Health and switches through every active operating terminal, confirming that `/api/me` preserves each selection.

## Local development

The project uses Development mode from `Properties/launchSettings.json` and a development-only JWT key.

For a brand-new local database, set the first SuperAdmin password in the same PowerShell process:

```powershell
.\deploy\Set-DevelopmentBootstrap.ps1

dotnet run --project .\backend\PalletControl.Api\PalletControl.Api.csproj
```

In another terminal:

```powershell
cd frontend
npm install
npm run dev
```

Vite runs on port 5173 and proxies `/api` to the development API at `http://localhost:5000`.

## Important database note

Do not delete or replace the production database during updates:

```text
C:\PalletControlData\palletcontrol.db
```

The GitHub repository ignores SQLite database/WAL/SHM files and backup directories.

## Build version

Backend and frontend: **5.9.1**
