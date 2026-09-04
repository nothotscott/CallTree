# CallTree.SipHarness

A real SIP client that drives a **spoofing-mode** CallTree instance over real SIP and real RTP, so call
handling can be exercised without a phone, a provider, or a phone bill — and so a question that a single
phone call can never answer ("what happens when three people call at once?") can be answered at all.

Nothing in it is a mock. It negotiates SDP, sends RFC 4733 DTMF the real screening gate has to debounce,
and puts mu-law frames on the wire on a 20 ms cadence that the real recorder has to reorder and write.
The only fiction is the caller ID, which is the point: CallTree classifies a call by comparing the `From`
user against `Telephony:MyCellNumber`, so the number a leg claims to be is what decides whether it takes
the Outbound path or faces the spam gate.

## What makes it more than "did audio flow"

Every leg the harness owns plays **its own distinct sine tone**, and it listens to what comes back with a
Goertzel detector scored against the tones it handed out. That turns every check into an identity check:

- the far end of caller 3 must hear *caller 3's* tone and nothing else;
- caller 3 must hear *that same far end's* tone back;
- channel 0 of caller 3's stereo recording must contain caller 3's tone, channel 1 the mobile's.

Crossed bridges, a relay wired to the wrong session, a recorder handed the wrong stream — all of them
still move packets in both directions and still produce a playable file. They show up here as the wrong
number.

It also measures **peak simultaneous callers**. Calls that each behaved perfectly but never once
overlapped are sequential calls, and the timestamps are the only thing that says so.

## Setting up the instance under test

Spoofing mode refuses to start if a trunk is configured — an instance must never be half-simulated
against a real line — so `Trunk:Host` has to be blank. Note that in Development user secrets supply the
real trunk credentials, so blank it explicitly.

```bash
export Trunk__Host=""                          # required: spoofing refuses to run beside a trunk
export Spoof__Enabled=true
export Spoof__LoopbackHost="127.0.0.1:5070"    # must match the harness's --listen
export Telephony__DidNumber="+15551234567"
export Telephony__MyCellNumber="+15559876543"
export Telephony__SipListenPort=5061
export Telephony__RtpPortStart=12000
export Telephony__RtpPortEnd=12400             # allow ~8 ports per concurrent leg
export Storage__RecordingsRoot="/tmp/ct/recordings"
export ConnectionStrings__CallTree="Data Source=/tmp/ct/calltree.db"

dotnet run --project CallTree.Api --no-launch-profile
```

The startup log says so unmistakably:

```
warn: SPOOFING MODE: no trunk, no registration. Outbound legs are dialled at 127.0.0.1:5070 instead of
      a provider, and INVITEs are accepted from loopback only. Everything else - the DID filter,
      screening, recording, relaying - runs exactly as it does on a real line.
```

`GET /api/telephony/status` reports it as `spoofing: true`, which is worth checking before believing
anything else on that page: every other field on it describes a line that cannot receive a real call.

## Running it

```bash
dotnet run --project CallTree.SipHarness -- \
  --did +15551234567 --cell +15559876543 \
  --scenario inbound --calls 3 --duration 12 \
  --recordings /tmp/ct/recordings
```

Exit code is 0 on PASS, 1 on FAIL, 2 on bad arguments. `--help` lists every option.

### Scenarios

| Scenario   | Caller ID | What it exercises |
|------------|-----------|-------------------|
| `inbound`  | stranger  | Screening gate, bridge to the mobile, two-way relay, stereo recording. The most code per run. |
| `outbound` | `--cell`  | Caller-ID classification, auto-answer, the recording reminder, mono recording. Add `--proxy` for the `*{NUMBER}#` dial. |
| `screened` | stranger  | Wrong digit: the call must end and must never reach the mobile, and must record nothing. |
| `missed`   | stranger  | Passes screening, mobile never picks up. Must place a leg, leave it ringing, and record nothing. |

Two timing settings matter and both default to something that works:

- `--dtmf-delay` (default 2 s) — CallTree persists the answer before attaching the DTMF gate, so a digit
  sent the instant the call connects can arrive at a call not yet listening and vanish. A real caller is
  reacting to a prompt and cannot come close to that window.
- `--proxy-delay` (default 10 s) — the Outbound path plays a ~6 s recording reminder *before* it opens the
  recorder and the `ProxyDialCollector`. Digits keyed in during the reminder are lost, and because the
  collector needs the leading `*` to start a sequence, losing that one digit silently discards the whole
  dial.

For `missed`, give `--duration` more than `Telephony:DialTimeoutSeconds` or the caller hangs up before
the dial times out and you exercise a different path.

## Reading a report

```
  caller 1
      outcome    held for the full duration
      answered   after 0.4s
      played     320 Hz, 600 frames
      heard      724 frames, 590 Hz (confidence 1.00, rms 0.213)
  ...
  peak simultaneous callers: 1 of 3

  FAIL
      - only 1 call(s) were ever up at once out of 3 - the rest were serialised.
```

- **`heard ... no media`** — no RTP arrived at all. Signalling, SDP, or a firewall.
- **`heard ... no tone (rms 0.000)`** — RTP arrived and was digital silence. Normal on an Outbound-source
  call, where CallTree is listening rather than talking.
- **`heard ... no tone (rms 0.2)`** — audio arrived that is not a clean single tone. Usually a prompt
  caught in the analysis window; on a bridged leg it can also be two tones at once.
- **`320 + 410 Hz mixed`** — two tones in one channel. Correct and expected on the Outbound path's mono
  recording, where a proxy dial is mixed into the same file rather than opening a second `Recording`.
  Anywhere else it is a crossed call.

Only the last few seconds of a leg are analysed, because the start of any call is prompts and ringback.

## What it deliberately does not do

- **No trunk, no NAT.** Everything is on loopback, so `NatAwareVoIPMediaSession`'s address rewriting is
  not exercised — that path can only be proven against a real trunk from behind a real router.
- **No provider quirks.** Codec preferences, re-INVITEs, session timers and the `403 Caller Origination
  Number is Invalid` class of trunk-side rejection are all things a harness on loopback cannot show you.
- **No audio quality judgement.** It measures which tone arrived, not whether a human would find the call
  pleasant. Chop and lag that a listener hears easily can leave the dominant frequency untouched.

So it does not replace the maintainer's "validate each phase by phone" rule. It makes the phone call the
*last* test rather than the first, and it answers the concurrency question, which a phone call with one
phone cannot.
