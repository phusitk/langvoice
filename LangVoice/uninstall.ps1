# LangVoice uninstaller
$dest = Join-Path $env:LOCALAPPDATA 'LangVoice'
$lnk  = Join-Path ([Environment]::GetFolderPath('Startup')) 'LangVoice.lnk'
Get-Process LangVoice -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $lnk  -Force -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'LangVoice has been removed.' -ForegroundColor Green
