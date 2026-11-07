$proc = Start-Process -FilePath "installer\dist_app\IDMLite.exe" -PassThru
Start-Sleep -Seconds 3
$info = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, MainWindowTitle, Responding
if ($info) {
    Write-Host "IDMLite is RUNNING! Window Title: $($info.MainWindowTitle), Responding: $($info.Responding)" -ForegroundColor Green
    Stop-Process -Id $proc.Id -Force
} else {
    Write-Host "IDMLite process did not stay running!" -ForegroundColor Red
}
