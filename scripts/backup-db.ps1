$ErrorActionPreference = 'Stop'
$dbPath = 'c:\RepairPartsPro\repairpartspro.db'
if (-not (Test-Path $dbPath)) {
  Write-Host 'Database file not found at c:\RepairPartsPro\repairpartspro.db' -ForegroundColor Red
  exit 1
}
$backupDir = 'c:\RepairPartsPro\backups'
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dest = Join-Path $backupDir "repairpartspro-$stamp.db"
Copy-Item $dbPath $dest -Force
Write-Host "Backup created: $dest" -ForegroundColor Green
