param(
    [string]$RepoPath = 'C:\PalletControl',
    [string]$PublishPath = 'C:\inetpub\PalletControl',
    [string]$AppPoolName = 'PalletControl',
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
$frontend = Join-Path $RepoPath 'frontend'
$backend = Join-Path $RepoPath 'backend\PalletControl.Api'
$tempPublish = Join-Path $env:TEMP 'PalletControlPublish'

if (-not (Test-Path (Join-Path $RepoPath '.git'))) { throw "$RepoPath is not a Git repository." }
if ((git -C $RepoPath status --porcelain)) { throw 'Git working tree has local changes. Commit/stash them before server update.' }

Write-Host '1/6 Pulling GitHub...' -ForegroundColor Cyan
git -C $RepoPath fetch origin
if ($LASTEXITCODE -ne 0) { throw 'git fetch failed.' }
git -C $RepoPath checkout $Branch
if ($LASTEXITCODE -ne 0) { throw 'git checkout failed.' }
git -C $RepoPath pull --ff-only origin $Branch
if ($LASTEXITCODE -ne 0) { throw 'git pull failed.' }

Write-Host '2/6 Building React...' -ForegroundColor Cyan
Push-Location $frontend
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm build failed.' }
} finally { Pop-Location }

Write-Host '3/6 Preparing ASP.NET wwwroot...' -ForegroundColor Cyan
$wwwroot = Join-Path $backend 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item (Join-Path $frontend 'dist\*') $wwwroot -Recurse -Force

Write-Host '4/6 Publishing .NET...' -ForegroundColor Cyan
if (Test-Path $tempPublish) { Remove-Item $tempPublish -Recurse -Force }
dotnet publish (Join-Path $backend 'PalletControl.Api.csproj') -c Release -o $tempPublish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Import-Module WebAdministration
$poolExists = Test-Path "IIS:\AppPools\$AppPoolName"
Write-Host '5/6 Replacing IIS files...' -ForegroundColor Cyan
if ($poolExists) { Stop-WebAppPool -Name $AppPoolName }
try {
    New-Item -ItemType Directory -Force -Path $PublishPath | Out-Null
    & robocopy $tempPublish $PublishPath /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
} finally {
    if ($poolExists) { Start-WebAppPool -Name $AppPoolName }
}

Write-Host '6/6 Update complete.' -ForegroundColor Green
Write-Host 'The live SQLite database and backups were not touched because they are configured outside the Git/IIS directories.'
