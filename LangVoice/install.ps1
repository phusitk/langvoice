# LangVoice installer (Windows PowerShell 5.1). Kept ASCII-only on purpose so file encoding never matters.
$ErrorActionPreference = 'Stop'

$src     = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest    = Join-Path $env:LOCALAPPDATA 'LangVoice'
$exe     = Join-Path $dest 'LangVoice.exe'
$startup = [Environment]::GetFolderPath('Startup')
$lnk     = Join-Path $startup 'LangVoice.lnk'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Warn($msg) { Write-Host "    ! $msg" -ForegroundColor Yellow }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# 1. Stop a running copy so files can be replaced
Step 'Stopping running LangVoice (if any)'
Get-Process LangVoice -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# 2. Compile with the C# compiler that ships with .NET Framework 4.x
Step 'Compiling LangVoice.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) not found. Enable .NET Framework 4.8 in Windows Features.' }

$out = & $csc /nologo /codepage:65001 /optimize+ /target:winexe `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$exe" (Join-Path $src 'LangVoice.cs') 2>&1
if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw 'Compilation failed.' }
Ok "Built $exe"

# 3. Sound files: bundled neural voices (female/male) + optional own recordings from .\sounds
Step 'Installing voice files'
Copy-Item (Join-Path $src 'voices\*.wav') $dest -Force
Ok 'Installed female (Premwadee / Jenny) and male (Niwat / Andrew) voices'
foreach ($name in 'thai.wav', 'english.wav') {
    $mine = Join-Path $src "sounds\$name"
    if (Test-Path $mine) {
        Copy-Item $mine (Join-Path $dest "custom-$name") -Force
        Ok "Installed your recording sounds\$name (choose 'custom' voice in the tray menu)"
    }
}

# 4. Copy uninstaller and register auto-start
Step 'Registering auto-start'
Copy-Item (Join-Path $src 'uninstall.ps1') $dest -Force
Copy-Item (Join-Path $src 'make-sounds.ps1') $dest -Force
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.Description = 'Announce Thai/English keyboard language'
$sc.Save()
Ok "Startup shortcut: $lnk"

# 5. Launch
Step 'Starting LangVoice'
Start-Process $exe -WorkingDirectory $dest
Write-Host ''
Write-Host 'Done! Look for the TH / EN icon in the system tray (click ^ near the clock if hidden).' -ForegroundColor Green
Write-Host 'Switch the keyboard language to hear the announcement.' -ForegroundColor Green
