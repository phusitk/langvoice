# LangVoice sound maker: preview Thai TTS variants, or record your own voice, then save as thai.wav / english.wav.
# ASCII-only on purpose (Thai text is built from code points).
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'LangVoice'
$soundsDir = if ($here -eq $dest) { $dest } else { Join-Path $here 'sounds' }
New-Item -ItemType Directory -Force -Path $soundsDir | Out-Null
$preview = Join-Path $env:TEMP 'langvoice_preview.wav'

Add-Type -AssemblyName System.Speech
Add-Type @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class LvMci {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
    public static void Send(string cmd) {
        int r = mciSendString(cmd, null, 0, IntPtr.Zero);
        if (r != 0) throw new Exception("MCI error " + r + " on: " + cmd);
    }
}

public static class LvWav {
    // Trim leading/trailing silence and normalize a 16-bit PCM WAV file in place.
    public static string TrimAndNormalize(string path) {
        byte[] b = File.ReadAllBytes(path);
        int pos = 12, fmtPos = -1, dataPos = -1, dataLen = 0;
        while (pos + 8 <= b.Length) {
            string id = Encoding.ASCII.GetString(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") fmtPos = pos + 8;
            if (id == "data") { dataPos = pos + 8; dataLen = Math.Min(len, b.Length - dataPos); break; }
            pos += 8 + len + (len & 1);
        }
        if (fmtPos < 0 || dataPos < 0) return "not a PCM wav";
        short channels = BitConverter.ToInt16(b, fmtPos + 2);
        int rate = BitConverter.ToInt32(b, fmtPos + 4);
        short bits = BitConverter.ToInt16(b, fmtPos + 14);
        if (bits != 16) return "not 16-bit, left unchanged";

        int n = dataLen / 2;
        short[] s = new short[n];
        Buffer.BlockCopy(b, dataPos, s, 0, n * 2);
        int peak = 1;
        for (int i = 0; i < n; i++) { int a = Math.Abs((int)s[i]); if (a > peak) peak = a; }
        int thr = Math.Max(400, peak / 10);
        int first = -1, last = -1;
        for (int i = 0; i < n; i++) if (Math.Abs((int)s[i]) > thr) { first = i; break; }
        for (int i = n - 1; i >= 0; i--) if (Math.Abs((int)s[i]) > thr) { last = i; break; }
        if (first < 0) return "only silence was recorded";
        int frame = channels;
        first = Math.Max(0, first - rate * frame * 40 / 1000);
        last = Math.Min(n - 1, last + rate * frame * 120 / 1000);
        first -= first % frame;
        int count = last - first + 1; count -= count % frame;
        double gain = Math.Min(8.0, 29000.0 / peak);

        using (BinaryWriter w = new BinaryWriter(File.Create(path))) {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + count * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)1); w.Write(channels); w.Write(rate);
            w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(count * 2);
            for (int i = first; i < first + count; i++) {
                int v = (int)Math.Round(s[i] * gain);
                w.Write((short)Math.Max(-32768, Math.Min(32767, v)));
            }
        }
        return "ok, " + (count * 1000 / (rate * channels)) + " ms";
    }
}
"@

# ---------- voices ----------
$winrt = $false
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
    $asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    $winrt = $true
} catch { }

function Get-Voices([string]$lang) {
    $list = @()
    if ($winrt) {
        foreach ($v in [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices) {
            if ($v.Language -like "$lang*") { $list += [pscustomobject]@{ Kind = 'WinRT'; Name = $v.DisplayName; Obj = $v } }
        }
    }
    $sapi = New-Object System.Speech.Synthesis.SpeechSynthesizer
    foreach ($v in $sapi.GetInstalledVoices()) {
        if ($v.Enabled -and $v.VoiceInfo.Culture.Name -like "$lang*") {
            $list += [pscustomobject]@{ Kind = 'SAPI'; Name = $v.VoiceInfo.Name; Obj = $v.VoiceInfo.Name }
        }
    }
    $sapi.Dispose()
    return ,$list
}

function Speak-ToFile($voice, [string]$text, [double]$rate, [string]$path) {
    if ($voice.Kind -eq 'WinRT') {
        $synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
        $synth.Voice = $voice.Obj
        try { $synth.Options.SpeakingRate = $rate } catch { }
        $task = $asTaskGeneric.MakeGenericMethod([Windows.Media.SpeechSynthesis.SpeechSynthesisStream]).Invoke($null, @($synth.SynthesizeTextToStreamAsync($text)))
        $null = $task.Wait(-1)
        $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($task.Result)
        $fs = [System.IO.File]::Create($path)
        try { $net.CopyTo($fs) } finally { $fs.Close(); $net.Close() }
    } else {
        $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
        $s.SelectVoice($voice.Obj)
        $s.Rate = [int][Math]::Round(($rate - 1.0) * 10)
        $s.SetOutputToWaveFile($path)
        $s.Speak($text)
        $s.Dispose()
    }
}

function Play([string]$path) {
    $p = New-Object System.Media.SoundPlayer $path
    $p.PlaySync()
    $p.Dispose()
}

function Save-As([string]$name) {
    if (-not (Test-Path $preview)) { Write-Host 'Nothing to save yet - preview or record something first.' -ForegroundColor Yellow; return }
    Copy-Item $preview (Join-Path $soundsDir $name) -Force
    if (Test-Path $dest) {
        Copy-Item $preview (Join-Path $dest $name) -Force
        Copy-Item $preview (Join-Path $dest "custom-$name") -Force   # kept across reinstalls
    }
    $ini = Join-Path $dest 'settings.ini'
    $lines = @(); if (Test-Path $ini) { $lines = @(Get-Content $ini | Where-Object { $_ -notmatch '^voice=' }) }
    Set-Content -Path $ini -Value ($lines + 'voice=custom') -Encoding ASCII
    Write-Host "Saved $name" -ForegroundColor Green
    $exe = Join-Path $dest 'LangVoice.exe'
    if (Test-Path $exe) {
        Get-Process LangVoice -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        Start-Process $exe -WorkingDirectory $dest
        Write-Host 'LangVoice restarted with the new sound.' -ForegroundColor Green
    }
}

function Record-Voice {
    try { [LvMci]::mciSendString('close lvrec', $null, 0, [IntPtr]::Zero) | Out-Null } catch { }
    $raw = Join-Path $env:TEMP 'langvoice_rec.wav'
    Read-Host 'Press Enter to START recording'
    [LvMci]::Send('open new type waveaudio alias lvrec')
    [LvMci]::Send('set lvrec bitspersample 16 channels 1 samplespersec 22050')
    [LvMci]::Send('record lvrec')
    Read-Host 'Recording... say the word now, then press Enter to STOP'
    [LvMci]::Send('stop lvrec')
    [LvMci]::Send("save lvrec `"$raw`"")
    [LvMci]::Send('close lvrec')
    $r = [LvWav]::TrimAndNormalize($raw)
    Write-Host "Recorded ($r)."
    Copy-Item $raw $preview -Force
    Play $preview
}

# ---------- candidates ----------
function T([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }
$thaiOptions = @(
    [pscustomobject]@{ Label = 'thai  - official spelling (sara ai + tho thahan + yo yak)'; Text = (T 0x0E44, 0x0E17, 0x0E22) },
    [pscustomobject]@{ Label = 'tai   - phonetic spelling, no silent letter';                 Text = (T 0x0E44, 0x0E17) },
    [pscustomobject]@{ Label = 'phasa thai - "Thai language"';                                 Text = (T 0x0E20, 0x0E32, 0x0E29, 0x0E32, 0x0E44, 0x0E17, 0x0E22) },
    [pscustomobject]@{ Label = 'thai with trailing space/period (helps some voices)';          Text = ((T 0x0E44, 0x0E17, 0x0E22) + '.') }
)

$thaiVoices = Get-Voices 'th'
$enVoices   = Get-Voices 'en'
$vi = 0
$rate = 1.0

Write-Host ''
Write-Host '=== LangVoice sound maker ===' -ForegroundColor Cyan
if ($thaiVoices.Count -eq 0) {
    Write-Host 'No Thai text-to-speech voice is installed on this PC.' -ForegroundColor Yellow
    Write-Host 'That is why the old sound was wrong. Either:' -ForegroundColor Yellow
    Write-Host '  - install one: Settings > Time & language > Speech > Add voices > Thai, then run this again, or' -ForegroundColor Yellow
    Write-Host '  - press R below to record your own voice (recommended, sounds most natural).' -ForegroundColor Yellow
}

while ($true) {
    Write-Host ''
    if ($thaiVoices.Count -gt 0) {
        Write-Host ("Thai voice: {0} [{1}]   speed: {2}" -f $thaiVoices[$vi].Name, $thaiVoices[$vi].Kind, $rate) -ForegroundColor Cyan
        for ($i = 0; $i -lt $thaiOptions.Count; $i++) { Write-Host ("  [{0}] preview: {1}" -f ($i + 1), $thaiOptions[$i].Label) }
        Write-Host '  [V] next Thai voice   [F] faster   [L] slower'
    }
    Write-Host '  [R] record your own voice from the microphone'
    Write-Host '  [P] play the last preview/recording again'
    Write-Host '  [T] SAVE last preview/recording as the THAI sound'
    Write-Host '  [E] SAVE last preview/recording as the ENGLISH sound'
    Write-Host '  [G] generate a fresh English sound ("English") and preview it'
    Write-Host '  [Q] quit'
    $c = (Read-Host 'Choose').Trim().ToUpper()
    try {
        if ($c -match '^[1-9]$' -and $thaiVoices.Count -gt 0 -and [int]$c -le $thaiOptions.Count) {
            Speak-ToFile $thaiVoices[$vi] $thaiOptions[[int]$c - 1].Text $rate $preview
            Play $preview
        }
        elseif ($c -eq 'V' -and $thaiVoices.Count -gt 0) { $vi = ($vi + 1) % $thaiVoices.Count }
        elseif ($c -eq 'F') { $rate = [Math]::Min(2.0, $rate + 0.1) }
        elseif ($c -eq 'L') { $rate = [Math]::Max(0.5, $rate - 0.1) }
        elseif ($c -eq 'R') { Record-Voice }
        elseif ($c -eq 'P') { if (Test-Path $preview) { Play $preview } }
        elseif ($c -eq 'T') { Save-As 'thai.wav' }
        elseif ($c -eq 'E') { Save-As 'english.wav' }
        elseif ($c -eq 'G') {
            if ($enVoices.Count -eq 0) { Write-Host 'No English voice found.' -ForegroundColor Yellow }
            else { Speak-ToFile $enVoices[0] 'English' $rate $preview; Play $preview }
        }
        elseif ($c -eq 'Q') { break }
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    }
}
