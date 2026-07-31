<#
  Regenerates the IVR prompt WAVs with the Windows speech synthesiser.

  These are PLACEHOLDERS. The wording — especially the recording disclosure — is an open decision.
  Recording law varies by jurisdiction and several require every party to a call to consent, so what the
  caller is told, and when, is a legal choice for whoever runs this. Edit $prompts below and re-run.

  Output format is 8 kHz / 16-bit / mono PCM, which is exactly what G.711 carries: no resampling on the
  way out, and PromptLibrary rejects anything else rather than playing it as noise.

  Usage (from the repo root):
    powershell -ExecutionPolicy Bypass -File tools\generate-prompts.ps1
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\CallTree.Core\CallTree.Api\prompts"),
    [string]$Voice = ""
)

Add-Type -AssemblyName System.Speech

$prompts = [ordered]@{
    greeting = "Thank you for calling. This call will be recorded. To be connected, press 1."
    accepted = "Thank you. Connecting you now."
    rejected = "No selection was received. Goodbye."
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    8000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    if ($Voice) { $synth.SelectVoice($Voice) }
    Write-Host ("Voice: {0}" -f $synth.Voice.Name)
    Write-Host ("Output: {0}`n" -f $OutputDirectory)

    foreach ($name in $prompts.Keys) {
        $path = Join-Path $OutputDirectory "$name.wav"
        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($prompts[$name])
        $synth.SetOutputToNull()

        $size = (Get-Item $path).Length
        # 8 kHz * 2 bytes per sample; subtract a nominal 44-byte header for the duration estimate.
        $seconds = [math]::Round(($size - 44) / 16000.0, 1)
        Write-Host ("  {0,-10} {1,6:N1}s  {2,7:N0} bytes  ""{3}""" -f $name, $seconds, $size, $prompts[$name])
    }
}
finally {
    $synth.Dispose()
}

Write-Host "`nDone. Available voices:"
(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() |
    ForEach-Object { "  " + $_.VoiceInfo.Name }
