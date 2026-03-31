$ErrorActionPreference = 'Stop'
$baseUrl = 'http://localhost:5002'

function Invoke-JsonPost {
  param([string]$Url, [hashtable]$Body, [hashtable]$Headers)
  return Invoke-RestMethod -Method Post -Uri $Url -ContentType 'application/json' -Headers $Headers -Body ($Body | ConvertTo-Json -Depth 6)
}

Write-Host '1) Health check...' -ForegroundColor Cyan
$health = Invoke-RestMethod -Uri "$baseUrl/health"
if ($health.status -ne 'ok') { throw 'Health check failed' }

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$email = "dryrun+$stamp@example.com"
$pass1 = 'Start1234!'
$pass2 = 'Reset1234!'

Write-Host '2) Register user...' -ForegroundColor Cyan
$reg = Invoke-JsonPost -Url "$baseUrl/api/auth/register" -Body @{ email = $email; password = $pass1; rememberMe = $false } -Headers @{}
$token = $reg.token
if (-not $token) { throw 'Registration failed: token missing' }
$auth = @{ Authorization = "Bearer $token" }

Write-Host '3) Load profile...' -ForegroundColor Cyan
$profile = Invoke-RestMethod -Uri "$baseUrl/api/profile" -Headers $auth
if (-not $profile.email) { throw 'Profile API failed' }

Write-Host '4) Run search...' -ForegroundColor Cyan
$search = Invoke-JsonPost -Url "$baseUrl/api/parts/search" -Headers $auth -Body @{ customerId = 1; brand = 'Dell'; model = 'XPS 15'; partType = 'Battery' }
if ($null -eq $search.listings) { throw 'Search API failed' }

Write-Host '5) Forgot password and reset...' -ForegroundColor Cyan
$forgot = Invoke-JsonPost -Url "$baseUrl/api/auth/forgot-password" -Headers @{} -Body @{ email = $email }
$devUrl = $forgot.developerResetUrl
if (-not $devUrl) {
  Write-Warning 'Developer reset URL not returned (likely non-development env or email-only mode). Skipping reset-password call.'
} else {
  $tokenParam = [System.Web.HttpUtility]::ParseQueryString(([uri]("$baseUrl$devUrl")).Query).Get('token')
  if (-not $tokenParam) { throw 'Unable to parse reset token' }
  $reset = Invoke-JsonPost -Url "$baseUrl/api/auth/reset-password" -Headers @{} -Body @{ token = $tokenParam; newPassword = $pass2 }
}

Write-Host '6) Login with latest password...' -ForegroundColor Cyan
$loginPassword = if ($devUrl) { $pass2 } else { $pass1 }
$login = Invoke-JsonPost -Url "$baseUrl/api/auth/login" -Headers @{} -Body @{ email = $email; password = $loginPassword; rememberMe = $false }
$token2 = $login.token
if (-not $token2) { throw 'Login failed after reset check' }

Write-Host '7) Analytics summary...' -ForegroundColor Cyan
$analyticsStatus = $null
try {
  $summary = Invoke-RestMethod -Uri "$baseUrl/api/analytics/summary?days=30" -Headers @{ Authorization = "Bearer $token2" }
  if ($null -eq $summary.totalSearches) { throw 'Analytics summary payload invalid' }
  $analyticsStatus = 'ok'
} catch {
  $status = $_.Exception.Response.StatusCode.value__
  if ($status -eq 401) {
    $analyticsStatus = 'unauthorized-expected-for-non-admin'
  } else {
    throw "Analytics summary check failed with status: $status"
  }
}

Write-Host ''
Write-Host 'Dry-run completed successfully.' -ForegroundColor Green
Write-Host "User: $email"
if ($analyticsStatus -eq 'ok') {
  Write-Host "Total searches (30d): $($summary.totalSearches)"
} else {
  Write-Host "Analytics endpoint status: $analyticsStatus"
}
