param(
    [string]$TempRoot = "C:\MonolithTemp",
    [string]$VenvPath = "C:\MonolithTemp\luam-piper-venv",
    [string]$VoiceDir = "C:\MonolithTemp\luam-piper-voices",
    [string]$Voice = "ru_RU-irina-medium",
    [string]$Voices = "ru_RU-irina-medium,ru_RU-denis-medium,ru_RU-dmitri-medium,ru_RU-ruslan-medium",
    [string]$Python = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Python)) {
    $Python = (Get-Command python).Source
}

New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
New-Item -ItemType Directory -Force -Path $VoiceDir | Out-Null

$venvPython = Join-Path $VenvPath "Scripts\python.exe"
if (-not (Test-Path -LiteralPath $venvPython)) {
    & $Python -m venv $VenvPath
}

& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install --upgrade piper-tts

$voiceList = $Voices -split "," | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
if ($voiceList.Count -eq 0) {
    $voiceList = @($Voice)
}

& $venvPython -m piper.download_voices --data-dir $VoiceDir @voiceList

$model = Get-ChildItem -LiteralPath $VoiceDir -Recurse -Filter ($Voice + ".onnx") -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $model) {
    throw "Piper voice model was not downloaded: $Voice under $VoiceDir"
}

foreach ($voiceName in $voiceList) {
    $voiceModel = Get-ChildItem -LiteralPath $VoiceDir -Recurse -Filter ($voiceName + ".onnx") -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $voiceModel) {
        throw "Piper voice model was not downloaded: $voiceName under $VoiceDir"
    }
}

Write-Host "LuaM Piper TTS is ready."
Write-Host "Python: $venvPython"
Write-Host "Voice:  $($model.FullName)"
Write-Host "Voices: $($voiceList -join ', ')"
Write-Host "Start:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset -PiperTts"
