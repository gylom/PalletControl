$ErrorActionPreference = 'Stop'

[Environment]::SetEnvironmentVariable('BootstrapAdmin__Password',$null,'Machine')
$env:BootstrapAdmin__Password = $null

# IIS/WAS keeps a copy of the machine environment. Restart once so new worker
# processes no longer inherit the bootstrap password.
& iisreset /noforce | Out-Null

Write-Host 'BootstrapAdmin__Password removed from the machine environment. Existing SuperAdmin account is unchanged.' -ForegroundColor Green
