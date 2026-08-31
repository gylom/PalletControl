$secure = Read-Host 'Password for the first local superadmin account' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $env:BootstrapAdmin__Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
Write-Host 'BootstrapAdmin__Password is set for this PowerShell process only.' -ForegroundColor Green
Write-Host 'Start the backend from this same PowerShell process: dotnet run --project backend\PalletControl.Api\PalletControl.Api.csproj'
