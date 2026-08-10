$base = "http://localhost:5000/api"

Write-Host "1. Health Check..."
$health = Invoke-RestMethod -Uri "$base/health"
if ($health.status -ne "healthy") { throw "Health Check failed" }
Write-Host "   PASS: API and database healthy"

Write-Host "2. Logging in as admin..."
$login = Invoke-RestMethod -Method Post -Uri "$base/auth/login" -ContentType "application/json" -Body (@{username="admin";password="admin123"} | ConvertTo-Json)
$h = @{Authorization="Bearer $($login.token)"}

Write-Host "3. Loading registration setup..."
$setup = Invoke-RestMethod -Uri "$base/setup/register" -Headers $h
if ($setup.vehicles.Count -lt 1) { throw "No vehicles returned" }
if ($setup.drivers.Count -lt 1) { throw "No drivers returned" }
if ($setup.palletTypes.Count -lt 1) { throw "No pallet types returned" }

$key = [guid]::NewGuid().ToString()
$payload = @{
  idempotencyKey=$key
  vehicleId=$setup.vehicles[0].id
  driverId=$setup.drivers[0].id
  direction="IN"
  items=@(@{palletTypeId=$setup.palletTypes[0].id;quantity=7})
  confirmWarnings=$true
}
$body = $payload | ConvertTo-Json -Depth 5

Write-Host "4. Creating receipt..."
$r1 = Invoke-RestMethod -Method Post -Uri "$base/receipts" -Headers $h -ContentType "application/json" -Body $body
if (-not $r1.receipt.id) { throw "Receipt was not returned" }

Write-Host "5. Sending exact same request again to test duplicate protection..."
$r2 = Invoke-RestMethod -Method Post -Uri "$base/receipts" -Headers $h -ContentType "application/json" -Body $body
if ($r1.receipt.id -ne $r2.receipt.id) { throw "Duplicate protection FAILED" }
Write-Host "   PASS: same receipt ID returned: $($r1.receipt.receiptNumber)"

Write-Host "6. Checking statistics..."
$stats = Invoke-RestMethod -Uri "$base/statistics" -Headers $h
Write-Host "   PASS: statistics endpoint responded with $($stats.rows.Count) row(s)"

Write-Host "7. Checking best-driver leaderboard..."
$leaders = Invoke-RestMethod -Uri "$base/statistics/best-drivers?period=thisMonth" -Headers $h
if ($leaders.drivers.Count -lt 1) { throw "Leaderboard returned no rows" }
Write-Host "   PASS: leaderboard endpoint responded"

Write-Host "8. Cancelling and reversing receipt..."
$cancel = @{reason="Smoke test cancellation"} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/receipts/$($r1.receipt.id)/cancel" -Headers $h -ContentType "application/json" -Body $cancel | Out-Null
$reverse = @{reason="Smoke test reversal"} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/receipts/$($r1.receipt.id)/reverse-cancellation" -Headers $h -ContentType "application/json" -Body $reverse | Out-Null
Write-Host "   PASS: cancellation + reversal"

Write-Host "9. Checking warning center..."
$warnings = Invoke-RestMethod -Uri "$base/warnings?unacknowledgedOnly=false" -Headers $h
Write-Host "   PASS: warning center responded with $($warnings.warnings.Count) warning(s)"

Write-Host "10. Checking normal user cannot access admin..."
$userLogin = Invoke-RestMethod -Method Post -Uri "$base/auth/login" -ContentType "application/json" -Body (@{username="user";password="user123"} | ConvertTo-Json)
try {
  Invoke-RestMethod -Uri "$base/admin/all" -Headers @{Authorization="Bearer $($userLogin.token)"}
  throw "Role protection FAILED"
} catch {
  if ($_.Exception.Response.StatusCode.value__ -eq 403) {
    Write-Host "   PASS: User received 403 Forbidden"
  } else { throw }
}

Write-Host ""
Write-Host "ALL SMOKE TESTS PASSED"
