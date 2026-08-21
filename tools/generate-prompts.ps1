<#
  Regenerates the IVR prompt WAVs with the Windows speech synthesiser.

  These are PLACEHOLDERS. The wording — especially the recording disclosure — is an open decision.
  Recording law varies by jurisdiction and several require every party to a call to consent, so what the
  caller is told, and when, is a legal choice for whoever runs this. Edit $prompts below and re-run.

  Output format is 8 kHz / 16-bit / mono PCM, which is exactly what G.711 carries: no resampling on the
  way out, and PromptLibrary rejects anything else rather than playing it as noise.

  Usage (from the repo root):
    powershell -ExecutionPolicy Bypass -File tools\generate-prompts.ps1
    powershell -ExecutionPolicy Bypass -Command "& tools\generate-prompts.ps1 -Only greeting,rejected"

  Use -Command for -Only: with -File every argument arrives as one string, so the list would bind as a
  single name that matches nothing and the run would quietly generate nothing at all.

  -Only exists so that adding one prompt does not rewrite the others: every run of the synthesiser
  produces slightly different bytes, which would otherwise churn files nobody edited.
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\CallTree.Core\CallTree.Api\prompts"),
    [string]$Voice = "",
    [string[]]$Only = @()
)

Add-Type -AssemblyName System.Speech

$prompts = [ordered]@{
    greeting          = "Thank you for calling. This call will be recorded. To be connected, press 1."
    accepted          = "Thank you. Connecting you now."
    rejected          = "No selection was received. Goodbye."
    # Heard only by the operator: the party added later joins via the handset's own three-way merge,
    # which CallTree is never told about. Telling them is the operator's job, hence the reminder.
    "recording-notice" = "Recording has started. Please tell anyone you add to this call that it is being recorded."
    "pin-request"      = "Please enter your PIN, followed by the pound key."
}

# Not speech: a periodic tone is the only disclosure the outbound path can make to a party who is merged
# in later. 1400 Hz is the tone long associated with recorded lines. Off unless an interval is configured.
$tones = [ordered]@{
    "recording-tone" = @{ Frequency = 1400; Seconds = 0.4 }
}

<#
  Writes an 8 kHz / 16-bit / mono PCM WAV holding a single tone, with a short fade at each end so it
  starts and stops without a click.
#>
function Write-ToneWav {
    param([string]$Path, [double]$Frequency, [double]$Seconds)

    $rate = 8000
    $count = [int]($rate * $Seconds)
    $fade = [int]($rate * 0.01)
    $samples = [int16[]]::new($count)

    for ($i = 0; $i -lt $count; $i++) {
        $gain = 1.0
        if ($i -lt $fade) { $gain = $i / $fade }
        elseif ($i -ge ($count - $fade)) { $gain = ($count - 1 - $i) / $fade }
        $value = [math]::Sin(2 * [math]::PI * $Frequency * $i / $rate) * 12000 * $gain
        $samples[$i] = [int16][math]::Round($value)
    }

    $data = New-Object byte[] ($count * 2)
    [System.Buffer]::BlockCopy($samples, 0, $data, 0, $data.Length)

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
        $writer.Write([int](36 + $data.Length))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
        $writer.Write([int]16)
        $writer.Write([int16]1)          # PCM
        $writer.Write([int16]1)          # mono
        $writer.Write([int]$rate)
        $writer.Write([int]($rate * 2))  # byte rate
        $writer.Write([int16]2)          # block align
        $writer.Write([int16]16)         # bits per sample
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
        $writer.Write([int]$data.Length)
        $writer.Write($data)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
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
        if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }

        $path = Join-Path $OutputDirectory "$name.wav"
        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($prompts[$name])
        $synth.SetOutputToNull()

        $size = (Get-Item $path).Length
        # 8 kHz * 2 bytes per sample; subtract a nominal 44-byte header for the duration estimate.
        $seconds = [math]::Round(($size - 44) / 16000.0, 1)
        Write-Host ("  {0,-17} {1,6:N1}s  {2,7:N0} bytes  ""{3}""" -f $name, $seconds, $size, $prompts[$name])
    }

    foreach ($name in $tones.Keys) {
        if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }

        $tone = $tones[$name]
        $path = Join-Path $OutputDirectory "$name.wav"
        Write-ToneWav -Path $path -Frequency $tone.Frequency -Seconds $tone.Seconds

        $size = (Get-Item $path).Length
        Write-Host ("  {0,-17} {1,6:N1}s  {2,7:N0} bytes  {3} Hz tone" -f $name, $tone.Seconds, $size, $tone.Frequency)
    }
}
finally {
    $synth.Dispose()
}

Write-Host "`nDone. Available voices:"
(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() |
    ForEach-Object { "  " + $_.VoiceInfo.Name }
