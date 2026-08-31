param(
    [string]$BaseUrl = 'http://localhost:5000/api',
    [string]$Username = $env:PALLETCONTROL_TEST_USER,
    [string]$Password = $env:PALLETCONTROL_TEST_PASSWORD
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = Read-Host 'PalletControl test username (SuperAdmin recommended)'
}
if ([string]::IsNullOrWhiteSpace($Password)) {
    $secure = Read-Host 'Password' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

Write-Host '1. Health check...' -ForegroundColor Cyan
$health = Invoke-RestMethod -Uri "$BaseUrl/health"
if ($health.status -ne 'healthy') { throw 'Health check failed.' }
Write-Host '   PASS: API + SQLite healthy'

Write-Host '2. Backend version...' -ForegroundColor Cyan
$version = Invoke-RestMethod -Uri "$BaseUrl/version"
Write-Host "   PASS: backend v$($version.version)"

Write-Host '3. Login...' -ForegroundColor Cyan
$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/auth/login" -ContentType 'application/json' -Body (@{
    username = $Username
    password = $Password
} | ConvertTo-Json)
if (-not $login.token) { throw 'Login did not return a token.' }
$headers = @{ Authorization = "Bearer $($login.token)" }
Write-Host "   PASS: $($login.username) / $($login.role) / $($login.terminalCode)"

Write-Host '4. Current-session endpoint...' -ForegroundColor Cyan
$me = Invoke-RestMethod -Uri "$BaseUrl/me" -Headers $headers
if ($me.username -ne $login.username) { throw '/api/me returned the wrong user.' }
Write-Host "   PASS: /api/me -> $($me.terminalCode)"

Write-Host '5. Registration setup endpoint...' -ForegroundColor Cyan
$setup = Invoke-RestMethod -Uri "$BaseUrl/setup/register" -Headers $headers
if ($null -eq $setup.palletTypes) { throw 'Registration setup did not return pallet types.' }
Write-Host "   PASS: vehicles=$($setup.vehicles.Count), drivers=$($setup.drivers.Count), palletTypes=$($setup.palletTypes.Count)"
Write-Host '   NOTE: zero vehicles/drivers is valid on a clean production database.'

if ($login.role -eq 'SuperAdmin') {
    Write-Host '6. SuperAdmin System Health...' -ForegroundColor Cyan
    $system = Invoke-RestMethod -Uri "$BaseUrl/admin/system/overview" -Headers $headers
    if (-not $system.version) { throw 'System Health did not return a version.' }
    Write-Host "   PASS: system health v$($system.version)"

    Write-Host '7. SuperAdmin terminal switching...' -ForegroundColor Cyan
    $terminals = @(Invoke-RestMethod -Uri "$BaseUrl/me/operating-terminals" -Headers $headers)
    foreach ($terminal in $terminals) {
        Write-Host "   Checking Admin scope for $($terminal.code)..." -ForegroundColor DarkCyan
        $terminalSettings = Invoke-RestMethod -Uri "$BaseUrl/admin/terminal-settings?terminalId=$($terminal.id)" -Headers $headers
        if ([int]$terminalSettings.terminalId -ne [int]$terminal.id) { throw "Terminal settings scope failed for $($terminal.code)." }
        $adminUsers = Invoke-RestMethod -Uri "$BaseUrl/admin/users?terminalId=$($terminal.id)" -Headers $headers
        if ([int]$adminUsers.terminalId -ne [int]$terminal.id) { throw "User Admin scope failed for $($terminal.code)." }
        $adminVehicles = Invoke-RestMethod -Uri "$BaseUrl/admin/vehicles?terminalId=$($terminal.id)" -Headers $headers
        if ([int]$adminVehicles.terminalId -ne [int]$terminal.id) { throw "Vehicle Admin scope failed for $($terminal.code)." }
        $adminDrivers = Invoke-RestMethod -Uri "$BaseUrl/admin/drivers?terminalId=$($terminal.id)" -Headers $headers
        if ([int]$adminDrivers.terminalId -ne [int]$terminal.id) { throw "Driver Admin scope failed for $($terminal.code)." }
        Write-Host "   PASS: Admin can target $($terminal.code)"

        $switched = Invoke-RestMethod -Method Post -Uri "$BaseUrl/me/terminal" -Headers $headers -ContentType 'application/json' -Body (@{
            terminalId = [int]$terminal.id
        } | ConvertTo-Json)
        if ($switched.terminalCode -ne $terminal.code) {
            throw "Terminal switch failed for $($terminal.code)."
        }
        $headers = @{ Authorization = "Bearer $($switched.token)" }
        $after = Invoke-RestMethod -Uri "$BaseUrl/me" -Headers $headers
        if ($after.terminalCode -ne $terminal.code) {
            throw "/api/me did not preserve active terminal $($terminal.code)."
        }
        Write-Host "   PASS: active terminal $($terminal.code)"
    }
} else {
    Write-Host '6-7. Skipped SuperAdmin-only checks (logged-in role is not SuperAdmin).' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'NON-DESTRUCTIVE SMOKE TESTS PASSED' -ForegroundColor Green
