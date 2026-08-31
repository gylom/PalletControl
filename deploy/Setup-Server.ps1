param(
    [string]$RepoPath = 'C:\PalletControl',
    [string]$PublishPath = 'C:\inetpub\PalletControl',
    [string]$DataPath = 'C:\PalletControlData',
    [string]$BackupPath = 'C:\PalletControlBackups',
    [string]$SiteName = 'PalletControl',
    [string]$AppPoolName = 'PalletControl',
    [Parameter(Mandatory=$true)][string]$HostName,
    [switch]$RequireHttps,
    [string]$OtlpEndpoint = ''
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run PowerShell as Administrator.'
    }
}

function New-RandomSecret([int]$bytes = 64) {
    $buffer = New-Object byte[] $bytes
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try {
        $rng.GetBytes($buffer)
    }
    finally {
        $rng.Dispose()
    }
    [Convert]::ToBase64String($buffer)
}

function Read-PlainPassword {
    $secure = Read-Host 'Initial SuperAdmin password (10+ chars, use upper/lower/number/special)' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Assert-StrongPassword([string]$Password) {
    if ([string]::IsNullOrWhiteSpace($Password) -or $Password.Length -lt 10) {
        throw 'Bootstrap password must be at least 10 characters.'
    }
    $groups = 0
    if ($Password -cmatch '[a-z]') { $groups++ }
    if ($Password -cmatch '[A-Z]') { $groups++ }
    if ($Password -match '\d') { $groups++ }
    if ($Password -match '[^A-Za-z0-9]') { $groups++ }
    if ($groups -lt 3) {
        throw 'Bootstrap password must use at least 3 of: lowercase, uppercase, number, special character.'
    }
}

Assert-Administrator

if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    Install-WindowsFeature Web-Server, Web-Mgmt-Tools -IncludeManagementTools | Out-Null
}

$runtime = (& dotnet --list-runtimes 2>$null) -join "`n"
if ($runtime -notmatch 'Microsoft\.AspNetCore\.App 10\.') {
    throw 'ASP.NET Core 10 runtime/Hosting Bundle is not installed. Install the .NET 10 Hosting Bundle first.'
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required on the server.' }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is required because server updates build the React frontend.' }
if (-not ((& dotnet --list-sdks 2>$null) -join "`n" -match '^10\.')) { throw '.NET 10 SDK is required because updates publish from source on the server.' }

New-Item -ItemType Directory -Force -Path $RepoPath,$PublishPath,$DataPath,$BackupPath | Out-Null

$jwtKey = [Environment]::GetEnvironmentVariable('Jwt__Key','Machine')
if ([string]::IsNullOrWhiteSpace($jwtKey)) {
    $jwtKey = New-RandomSecret
    [Environment]::SetEnvironmentVariable('Jwt__Key',$jwtKey,'Machine')
}

$bootstrapPassword = Read-PlainPassword
Assert-StrongPassword $bootstrapPassword

[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT','Production','Machine')
[Environment]::SetEnvironmentVariable('ConnectionStrings__Default',"Data Source=$DataPath\palletcontrol.db;Cache=Shared",'Machine')
[Environment]::SetEnvironmentVariable('Database__BackupDirectory',$BackupPath,'Machine')
[Environment]::SetEnvironmentVariable('AllowedHosts',$HostName,'Machine')
[Environment]::SetEnvironmentVariable('Security__RequireHttps',$(if($RequireHttps){'true'}else{'false'}),'Machine')
[Environment]::SetEnvironmentVariable('BootstrapAdmin__Username','superadmin','Machine')
[Environment]::SetEnvironmentVariable('BootstrapAdmin__DisplayName','Super Administrator','Machine')
[Environment]::SetEnvironmentVariable('BootstrapAdmin__Password',$bootstrapPassword,'Machine')
[Environment]::SetEnvironmentVariable('OpenTelemetry__OtlpEndpoint',$OtlpEndpoint,'Machine')

# Mirror the same values into this PowerShell process. Machine-level values are for
# IIS/Windows persistence; process values make any immediate commands deterministic.
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ConnectionStrings__Default = "Data Source=$DataPath\palletcontrol.db;Cache=Shared"
$env:Database__BackupDirectory = $BackupPath
$env:AllowedHosts = $HostName
$env:Security__RequireHttps = $(if($RequireHttps){'true'}else{'false'})
$env:BootstrapAdmin__Username = 'superadmin'
$env:BootstrapAdmin__DisplayName = 'Super Administrator'
$env:BootstrapAdmin__Password = $bootstrapPassword
$env:OpenTelemetry__OtlpEndpoint = $OtlpEndpoint

Import-Module WebAdministration
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity

if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -Port 80 -HostHeader $HostName -PhysicalPath $PublishPath -ApplicationPool $AppPoolName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PublishPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

# Restart IIS services once so newly-created machine environment variables are visible
# to future worker processes. The application itself is deployed in the next step.
& iisreset /noforce | Out-Null

Write-Host ''
Write-Host 'Server base configuration created.' -ForegroundColor Green
Write-Host "Repository: $RepoPath"
Write-Host "IIS publish: $PublishPath"
Write-Host "Database: $DataPath\palletcontrol.db"
Write-Host "Backups: $BackupPath"
Write-Host "Host: $HostName"
Write-Host ''
Write-Host 'Next: clone/pull the GitHub repo, then run deploy\Update-PalletControl.ps1.' -ForegroundColor Cyan
Write-Host 'After you have logged in successfully once, run deploy\Clear-BootstrapPassword.ps1.' -ForegroundColor Yellow
Write-Host 'Machine environment changes may require IIS/WAS restart; Update-PalletControl.ps1 performs an app-pool restart.'
