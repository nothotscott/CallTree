# CallTree — Progress

What has been built so far. Remaining work is in [TODO.md](TODO.md); the overview is in
[README.md](README.md).

## Phase 0 — Foundation ✅

- Backend split into layered projects under `CallTree.Core/`, all in `CallTree.Core.slnx`:
  - **CallTree.Domain** — entities, value objects, enums, domain events; zero dependencies.
  - **CallTree.Application** — ports (`ICallRepository`), use-case services, call commands.
  - **CallTree.Infrastructure** — EF Core + SQLite persistence.
  - **CallTree.Telephony** — SIPSorcery/NAudio; hosted service that owns the SIP user agent.
  - **CallTree.Api** — ASP.NET Core host; wires DI and hosts telephony.
  - **CallTree.Tests** — xUnit v3.
- Domain model: `Call` aggregate root with an enforced state machine
  (`Ringing → Screening → Dialing → InProgress → Completed/ScreenedOut/Missed/Failed`), `CallLeg` (one SIP
  dialog per leg; bridged calls will have two), `Recording` with a `FinalizedAt` crash marker, a
  `PhoneNumber` E.164 value object, and domain events (`CallStarted/CallAnswered/CallBridged/CallEnded`).
- Persistence: `CallTreeDbContext` (enums stored as strings), `CallRepository`, `InitialCreate` migration
  applied automatically at startup, database directory created if missing.
- Configuration binding for the `Trunk`, `Telephony` and `Storage` sections; user secrets for sensitive
  values in development.
- `/health` endpoint; OpenAPI via Scalar in development.

## Phase 1 — SIP signalling ✅ (validated over a real trunk)

- `TelephonyBackgroundService` binds UDP (and optionally TCP) SIP channels, registers via
  `SIPRegistrationUserAgent` with all four registration events logged, routes SIPSorcery's internal logging
  into the host pipeline, and answers `OPTIONS` keepalives so the trunk sees the endpoint as reachable.
- Inbound handling logs every INVITE, classifies the caller against `Telephony:MyCellNumber`
  (`Outbound/CallerIdMatch` vs `Inbound/Default`), answers, and persists the `Call` aggregate. Remote-BYE
  and local-hangup races are guarded so exactly one terminal state is recorded.
- INVITEs not addressed to `Telephony:DidNumber` are rejected with 404 before any row is created.
- `CallLifecycleService` drives the aggregate from telephony events, reached through `ICallCommands` so the
  SIP code never handles DI scoping itself.

### Trunk bring-up — four stacked faults

Worth recording, because each one masked the next and all four present as "the phone just rings busy".

1. **The REGISTER `Contact` had no user part.** SIPSorcery's `sendUsernameInContactHeader` defaults to
   *false*. The registrar answered `200 OK` because digest auth was valid, so registration looked perfectly
   healthy — but the provider could not tie the binding to a connection, its portal reported the connection
   unregistered with every field null, and inbound calls had no destination.
2. **`Contact` and the SDP advertised the LAN address.** Added `Telephony:PublicHost`, applied via
   `SIPTransport.ContactHost`.
3. **The SDP still advertised the LAN address** even after passing `publicIpAddress:` to `Answer` — that
   argument is only a fallback, and `RTPSession.GetSdpConnectionAddress` prefers the local address that
   routes to the offer's connection address. Fixed with `NatAwareVoIPMediaSession`, which rewrites the
   answer after the base class builds it.
4. **The trunk account's tier refused every call in both directions** until the test mobile was added as a
   verified number. The provider generated the busy tone itself (`486`, blank connection id in the CDR), so
   no INVITE was ever sent — which is why nothing appeared locally regardless of router or transport
   changes.

`Telephony:TraceSip` (full SIP wire logging) is what made any of this visible and is the first thing to
turn on when signalling misbehaves.

## Phase 2 — Media out + DTMF in ✅ (validated by phone)

Validated over the trunk: a call from a third-party number was classified `Inbound/Default`, heard the
greeting, and passed the gate on digit 1 — `DTMF digit 1 (160ms)` → `screening passed` → `Completed`, with
audio clearly audible on the handset. That exercises the PCM→G.711 encode path and the NAT-corrected SDP
end to end.

- **Prompt playback.** `WavAudio` is a small RIFF reader that walks chunks rather than assuming fixed
  offsets and returns raw 16-bit PCM, because `AudioExtrasSource.SendAudioFromStream` takes raw samples —
  hand it a `.wav` and the 44-byte header plays as noise. `PromptLibrary` decodes every prompt once at
  startup so a bad file is a boot-time error rather than silence mid-call.
- **The gate.** `ScreeningGate` plays the greeting, accepts barge-in, and waits up to
  `Telephony:ScreeningTimeoutSeconds` for `Telephony:ScreeningDigit`. A single keypress raises `OnDtmfTone`
  more than once, so the first tone is latched. Passing plays `accepted` and ends `Completed`; a wrong key
  or a timeout plays `rejected` and ends `ScreenedOut`.
- **Codec pinned to PCMU** via `AudioExtrasSource.RestrictFormats`. Unrestricted, the answer echoed the
  trunk's full offer and G.722 was selected, which would have broken the Phase 3 decode and Phase 4 payload
  relay. DTMF is unaffected — `MediaStream` adds the RFC 4733 telephone-event payload separately.
- **Domain**: `Call.CompleteScreening` covers "passed the gate but there is nothing to bridge to yet", and
  is replaced by `BeginDialing` + `Bridge` in Phase 4.
- **Verified locally** with a scripted caller sending real RFC 4733 DTMF: PCMU negotiated, non-silent RTP
  received (so the prompt genuinely streamed), and all three outcomes landing correctly in SQLite.

## Phase 3 — Recording calls from my own number ✅ (validated by phone)

Calls classified `Outbound` are answered, optionally gated by a PIN, and recorded to a mono 16-bit WAV.
Only received audio is captured, which is the whole design: the operator adds the other party with the
handset's own three-way merge, so by the time it matters this single leg already carries both voices.

**Validated over the trunk**: the notice played, the PIN gate worked, and the recorded `.wav` played back
correctly at the right length.

### The RTP timestamp is the clock

The original plan called for a paced 20 ms write clock. There isn't one, and the reason is worth keeping:
for PCMU the RTP timestamp *counts samples at 8 kHz*, so it is already the sender's sample clock and says
exactly where every packet belongs. Writing packets back to back as they arrive would silently compress
every pause in the conversation; pacing off a wall clock instead would drift whenever the two clocks
disagree, and would still need reconciling against the timestamps. Writing from the timestamp directly
means gaps are *measurable*, and they are filled with real silence so the recording stays in step with
what was said.

Two guards fall out of that:

- A timestamp jump larger than ten seconds is treated as a discontinuity — a clock reset, a stray packet
  from an old stream — and resynchronised rather than filled. Without the cap, one bogus timestamp asks
  for however many gigabytes of silence it implies.
- A packet that arrives after its place in the file has been written is dropped and counted. A WAV cannot
  be inserted into; dropping costs 20 ms, rewinding would corrupt everything after it.

Timestamps are compared as signed differences throughout, so the 32-bit wrap (about six days of
continuous audio) reorders nothing.

Phase 5 cannot reuse this. Two legs have two unrelated RTP clocks with nothing to align them to, which is
the case that genuinely needs the shared wall clock this phase did without.

### The rest of the pipeline

- **`RtpJitterBuffer` is a reordering buffer, not a playout buffer.** A softphone needs a playout clock
  because it has a speaker to feed; a recorder has no deadline. So the release rule is depth-based: a
  frame is handed on once a frame at least `Telephony:JitterBufferMilliseconds` newer has arrived, which
  is the point after which nothing earlier can still be expected.
- **`G711` is written out rather than taken from NAudio**, since understanding the wire format is half
  the point of the project, and the test asserts it against NAudio's decoder for all 256 codes — the
  whole input domain, so nothing is left to sample. That turned up a genuine disagreement: NAudio's table
  decodes 0x7F, the negative-zero code, to −1 where the ITU reference expansion computes 0. One LSB on a
  code that means zero, inaudible either way, but a table that cannot represent silence as silence is the
  wrong one to inherit. The test pins the difference down so it is not rediscovered as a bug.
- **Non-PCMU payloads are ignored at the door.** Payload 101 shares the RTP session; decoding the RFC
  4733 telephone-event stream as audio would write a burst of noise into the recording on every keypress.
- **The header is re-patched every five seconds.** A process killed mid-call then leaves a file that
  still plays up to the last flush, instead of one that every tool reads as empty. Asserted by a test
  that reads the file while the recorder still holds it open.
- **Finalizing cannot race the hangup.** `FinalizeRecording` touches only the `Recording` entity, so EF
  emits no `UPDATE` for the `Call` row and cannot overwrite the terminal status that the hangup handler
  is writing concurrently in its own scope.

### The PIN, and what Screening now means

`Telephony:OutboundPin` gates the recording path, blank by default. Caller ID is trivially forged, and
this is the path that answers automatically and records without asking; a successful spoof here costs disk
and nothing else, since this path never dials anywhere itself. Worth deciding regardless, now that Phase 4
means the codebase does place real outbound calls elsewhere and a forged identity is worth taking
seriously project-wide.

`Call.Answer` now takes `requireScreening` rather than deriving it from `Source`, so `Screening` means
"this caller is being gated" on either path. That is what lets a failed PIN land in `ScreenedOut` — a
spoofing attempt is distinguishable in the call log from a call that simply finished, which it would not
be if the gate lived outside the state machine. `Call.PassScreening` covers the Outbound case where the
caller clears the gate and there is nothing to dial.

Multi-digit entry needs a debounce the single-digit gate did not: one keypress raises `OnDtmfTone`
several times because RFC 4733 retransmits the end-of-event packet three times. Those repeats arrive
within a packet interval or two, so a repeat of the same digit inside 250 ms counts as one keypress —
which is what stops a PIN like 1112 collapsing to 12.

### Disclosure, and the gap that cannot be closed in software

`recording-reminder.wav` (named `recording-notice.wav` until the outbound proxy dial addendum repurposed
that name — see below) plays before the recorder opens, so the disclosure never lands inside the file it
is disclosing. But it reaches **only the operator**: a party added via the handset's native three-way merge
is invisible to CallTree. No prompt can reach them that way.

The only mechanical disclosure available to a natively-merged party is a periodic tone, added as
`Telephony:RecordingToneIntervalSeconds` (a generated 1400 Hz tone, sent not received, so it does not
appear in the recording). **Decided (2026-08-22): left off.** The operator confirmed the spoken notice on
each path (`greeting.wav` Inbound, `recording-reminder.wav` Outbound) plays correctly on real calls and is
the chosen disclosure; the tone is not turned on. The structural gap above is unchanged by that decision —
it is a property of the design (the native handset merge is invisible to CallTree), not something the tone
would have closed for that party anyway once it was declined. (The later outbound proxy dial addendum
closes an *adjacent* gap for a different party — one CallTree dials itself — without changing this one.)

### Elsewhere

- `StorageOptions` moved from Infrastructure to Application: Infrastructure owns the database and
  Telephony writes the recordings, neither may reference the other, and binding one section into two
  option types would put the same setting in two places.
- `RecordingStore` groups files by month and names them `<utc-timestamp>-<call-id>.wav`, storing a
  forward-slashed relative path so a database written on Windows still resolves in the container. It also
  owns the traversal check for Phase 7's streaming endpoint, next to the path construction rather than
  wherever gets there first.
- `PromptPlayer` was extracted from `ScreeningGate` before `PinGate` copied it a second time.
- The settings page grew a Recording section. The PIN is write-only like the trunk password, with a
  switch beside it, because "blank means unchanged" leaves no way to express "turn the gate off".

## Phase 4 + 5 — Inbound bridge and stereo recording ✅ (validated by phone)

A screened-in Inbound caller is now bridged to `Telephony:MyCellNumber` and both legs are recorded to one
stereo WAV, instead of the Phase 2 stand-in (`Call.CompleteScreening`, now deleted) that just ended the
call once the gate was passed.

**Validated over the trunk**: bridge connects with two-way audio, a caller hangup ends the mobile leg, a
mobile hangup ends the caller leg, and an unanswered ring lands in `Missed` with the apology prompt heard
— all four confirmed on real calls.

### Bring-up fault: the outbound leg's caller ID

Worth recording, in the same spirit as Phase 1's stacked faults — the first real dial attempt failed with
`403 Caller Origination Number is Invalid` from Telnyx. `SIPUserAgent.Call(dst, username, password,
mediaSession, ringTimeout)` — the simple overload used for the first cut — builds its own
`SIPCallDescriptor` with no `From` set, which left the trunk to infer a caller ID from the SIP registration
username rather than a phone number. Telnyx validates the calling number against what is actually
provisioned on the account, so anything else is rejected before the call ever rings.

Fixed by constructing the `SIPCallDescriptor` explicitly and pinning `From` to `Telephony:DidNumber`:

```csharp
var from = $"<sip:{did.Value.TrimStart('+')}@{server}>";
var callDescriptor = new SIPCallDescriptor(
    trunk.Username, trunk.Password, dst, from,
    to: null, routeSet: null, customHeaders: null, authUsername: null,
    callDirection: SIPCallDirection.Out, contentType: null, content: null, mangleIPAddress: null);
```

`BridgeToMobileAsync` now fails loudly with a clear log line if `Telephony:DidNumber` isn't configured,
rather than dialing out with a caller ID the trunk will reject anyway. Confirmed via reflection against the
installed 10.0.12 package (`SIPCallDescriptor`'s fields, not just its XML docs) before writing the fix,
per the project's standing rule to verify SIPSorcery v10 signatures against the real package rather than
older docs or memory.

### Ringback while the outbound leg rings

Phone testing surfaced a real gap: the caller heard dead silence for up to `Telephony:DialTimeoutSeconds`
while the mobile rang. `PlayRingingAsync` loops `ringing.wav` (440+480 Hz, the North American ringback
tone) to the caller for as long as the dial attempt is in flight, on its own linked cancellation token so
it stops the instant the dial resolves — answered, failed, or abandoned — rather than racing
`apology.wav` onto the same audio source. The prompt itself is the ~2s "on" portion; the 4s "off" gap is a
plain `Task.Delay` in the loop, the same split `WaitForHangupAsync` already uses for
`recording-tone`'s interval. `generate-prompts.ps1`'s tone generator grew a second-frequency parameter to
produce it, since a single sine wave doesn't read as a phone ringing.

### Scope: deliberately smaller than the original Phase 4 TODO

Matching how Phases 1–3 were kept inline, this pass does **not** do the full refactor the TODO once
described:

- **No `CallSession`/`ActiveCallRegistry`.** The bridge for one call is a call-local method
  (`TelephonyBackgroundService.BridgeToMobileAsync` / `RunBridgeAsync`), not a global registry. The
  pre-existing limitation that the one long-lived `listenerUserAgent` can't cleanly handle two
  *simultaneous inbound* INVITEs is untouched by this change — the bridge's outbound leg uses its own,
  separate, per-call `SIPUserAgent`, so it neither worsens nor fixes that.
- **No DTMF passthrough** from the caller to the mobile leg during the bridge — RFC 4733 (payload 101) is
  dropped at the relay, same as it always was at the recorder.
- **No trunk concurrency/duration-cap check** — that's a provider-account setting to verify, not code.

### The bridge itself

- `Call.BeginDialing` / `Call.Bridge` / `Call.MarkMissed` — already written in the Phase 2 domain model,
  unused until now — drive `Dialing` and the eventual `InProgress`/`Missed` outcome. Two new
  `CallCommand`s, `BeginDialing` and `BridgeCall`, carry those transitions through `ICallCommands` the
  same way every other telephony event does.
- The outbound leg is a fresh `SIPUserAgent` placing an explicit `SIPCallDescriptor` (see the caller-ID
  fault below — this is not the simple string-destination overload) against the trunk — the same registrar
  the DID is registered through, since that is what actually routes a PSTN destination. The outbound leg's
  SIP Call-ID persisted on `BeginDialing` is a locally-generated correlation id, not the real one
  SIPSorcery mints internally when the INVITE is sent — that value isn't surfaced by the public API before
  the call resolves. Left as-is: the persisted id is only ever used for our own forensic correlation
  (matching a `CallLeg` row to a `Telephony:TraceSip` capture by hand), never compared against the wire
  Call-ID programmatically, so the mismatch costs nothing functionally.
- If the caller hangs up while the mobile is still ringing, the dial attempt is raced against the same
  hangup-cancellation token the inbound leg already used for the screening gate, and the outbound agent is
  hung up rather than left ringing.
- Once both legs are up, RTP is relayed raw each direction with `SendRtpRaw` — no transcode, since both
  legs are always PCMU. Whichever side hangs up first ends the call exactly once (the existing
  `Interlocked.Exchange`-guarded `EndOnceAsync`) and explicitly hangs up the other leg, so a caller hangup
  can't leave the mobile leg connected and vice versa.
- No answer within `Telephony:DialTimeoutSeconds` (default 25s, live-reloaded like
  `ScreeningTimeoutSeconds`) plays a new `apology.wav` to the caller and ends the call `Missed`.

### `BridgedCallRecorder`

`CallRecorder` (Phase 3) could not be reused: its file position is driven by one leg's own RTP timestamp,
and a bridge has two legs with two unrelated RTP clocks and nothing to align them to. `BridgedCallRecorder`
gives each leg its own reorder buffer and silence-fill (identical rules to `CallRecorder` — a gap is
filled, a jump over ten seconds is a discontinuity and resynchronises instead of filling), but each leg
accumulates decoded samples into its own queue rather than writing straight to the WAV. The *shared wall
clock* is wherever packet arrival on **either** leg drives a drain of both queues together, not either
leg's RTP clock directly — a leg with nothing queued at drain time is still understood to be advancing in
real time, because its own silence-fill keeps its queue topped up during a gap on that leg alone. At
`Close()`, whichever leg still has a residual tail is padded with silence rather than truncated, since a
stereo WAV cannot have mismatched channel lengths. Left channel is the caller, right is the mobile,
matching `ChannelLayout.StereoPerLeg`'s existing doc comment. The header is re-patched every five seconds
like `CallRecorder`'s, for the same reason: a process killed mid-bridge should still leave a file that
plays up to the last flush.

### Elsewhere

- `Call.CompleteScreening` is deleted — it was explicitly the Phase 2 stand-in, documented as due for
  removal once bridging landed.
- `CallLifecycleService.ScreeningCompletedAsync` now only ever records a *failed* gate; a pass never
  reaches it — throwing if it is ever called with `Passed` catches that invariant breaking rather than
  silently completing a call that should have been bridged.
- `Telephony:DialTimeoutSeconds` is wired through the same settings stack as every other numeric Telephony
  setting (options, the settings DTOs, the config-file merge, the `/settings` UI field).

## Phase 4 addendum — Outbound proxy dial ⚠️ (functionally validated; audio-quality fix pending re-validation)

A self-hosted outbound calling proxy on the Outbound-source path: while on a call from `MyCellNumber`
(already answered, already recording), dialing `*{NUMBER}#` places a *new* leg from the DID to `{NUMBER}`,
so the far end sees the DID rather than the operator's real mobile number.

### The harder half: one continuous recording, not a new one

The obvious approach — reuse `BridgedCallRecorder` for the proxy segment — would mean finalizing the
already-open mono `Recording` and starting a second, stereo one the moment a proxy dial connects. That
means a `Call` gaining more than one `Recording`, which the domain doesn't support (`Call.StartRecording`
throws if `Recording` is already set) and which would have rippled into the recordings API and UI built
earlier (both currently assume one recording per call). Decided against, with the user, in favour of
mixing the proxy leg's audio live into the *same* mono file the operator's own leg has been writing to
since the call started — no domain change, no new `Recording` row, works no matter how many times a proxy
leg comes and goes in one call.

This works because the design already had a documented reason two legs *can't* share a recorder as-is:
`CallRecorder`'s original remark said Phase 5's stereo case "cannot reuse this directly: two legs have two
unrelated RTP clocks." The fix for the Inbound bridge was to keep the legs on *separate* channels
(`BridgedCallRecorder`). The fix here is almost the same shared-wall-clock machinery, but **summed into
one channel instead of separated into two** — reordering, gap-fill and discontinuity handling extracted
into `RtpLegAccumulator` (`CallTree.Telephony/Audio/RtpLegAccumulator.cs`), shared by both recorders so the
rules exist in exactly one place. `CallRecorder` now holds one always-present primary accumulator plus an
optional secondary:

- `AttachSecondaryLeg()` / `AcceptSecondary(...)` / `DetachSecondaryLeg()` — the secondary can be attached
  and detached any number of times per call (each proxy dial gets a fresh accumulator, since it's an
  unrelated RTP stream with its own clock origin). With no secondary attached, behavior is byte-identical
  to the original single-leg `CallRecorder` — same tests, same output, for every call that never touches
  this feature.
- While attached, a sample is only written once *both* legs have one for that position (packet arrival on
  either leg is what advances the file, same principle as `BridgedCallRecorder`), summed and **clamped**
  to `short` range rather than wrapped. In practice PCMU's own dynamic range (µ-law tops out around ±8000)
  keeps a two-voice sum well inside `short`'s range without clipping — the clamp is a defensive guard, not
  something realistic audio exercises, which is why there is no dedicated "clipping" unit test: one
  couldn't be constructed through the real decode path.
- `DetachSecondaryLeg()` flushes the secondary's jitter buffer and pairs whatever it can against the
  primary; any secondary tail left over with no primary counterpart yet is written alone rather than lost.
  This can make the file up to one jitter-buffer depth (`Telephony:JitterBufferMilliseconds`, default 60ms)
  longer than the primary leg's own real-time span — the same bounded reconciliation
  `BridgedCallRecorder.Close()` already does for its channel-length mismatch, just manifesting as extra
  length instead of stereo padding. `CallRecorderTests.cs` asserts this as a range, not an exact value, for
  exactly that reason.

### The dial primitive, shared with Phase 4

`BridgeToMobileAsync`'s dial mechanics (build the `SIPCallDescriptor` with `From` pinned to the DID, play
ring-back for the duration, race `Call(...)` against caller-abandonment) were extracted into
`PlaceOutboundLegAsync(callId, target, callerId, ringbackPlayer, cancellationToken)` — trigger-agnostic by
design, since the user wants a future Web-softphone phase to place calls the same way, just from an HTTP
request instead of DTMF. `BridgeToMobileAsync` now calls it instead of inlining; no observable behavior
change, confirmed by rerunning the full suite plus a boot smoke test. One consequence: `outboundAgent
.OnCallHungup` is now subscribed *after* the primitive reports the leg answered rather than before dialing
starts, since `PlaceOutboundLegAsync` owns agent creation internally. `OnCallHungup` fires for an
established dialogue being torn down, which cannot happen before answer, so this is safe — documented
inline rather than left implicit.

### `ProxyDialCollector`

Unlike `ScreeningGate`/`PinGate` (run once, pass or fail the whole call), this is a persistent listener:
`*` (re)starts collection, digits accumulate, `#` attempts to parse a `PhoneNumber` — a bad entry logs and
resets to idle rather than ending anything, and the same call keeps listening for the next attempt. No new
timeout setting: bounded only by the call's own hangup. Deliberately **not** run while a proxy leg is
already connected, so DTMF meant for the proxy-dialed party (navigating their own phone menu) is never
mistaken for a new dial attempt — matches Phase 4's own DTMF-passthrough non-goal.

### Bring-up fault (partially fixed, see Known Issue below): the live relay was choppy, with lag that grew over the call

First phone test of this addendum surfaced it, but the bug was actually in Phase 4's bridge too, copied
into this feature along with the rest of the relay code: `RunBridgeAsync` and `RunProxyDialAsync` both
relayed a packet to the other leg **the instant it arrived** — `OnRtpPacketReceived` straight into
`SendRtpRaw`, no buffering at all. The give-away that this was a relay problem and not, say, a Telnyx
outbound-profile setting: the *recording* was clean while the *live* call was choppy, and the recording
path already reorders through `RtpJitterBuffer` (via `RtpLegAccumulator`) — the relay was the one place
that didn't.

**First attempt (insufficient): reorder before relaying.** Giving each relay direction its own
`RtpJitterBuffer` and only relaying what it released, in order, fixed correctness but not the actual
symptom — real testing afterward showed the call still choppy, with **lag that grew as the call went on**.
That second symptom is the tell for what reordering alone can't fix: a burst of packets arriving close
together was still relayed as a burst, sent back-to-back with zero pacing between them, and a receiving
endpoint's own adaptive jitter buffer responds to that burstiness by growing its buffering target —
generally ratcheting delay up quickly and shrinking it back down slowly, which is exactly what "lag that
gradually increases" looks like from the outside, even though nothing on CallTree's side is literally
accumulating a backlog. A SIPSorcery GitHub issue (#1474, "Audio jitter/choppy echo when returning RTP
audio... recorded audio files play back cleanly") describes the identical shape with no resolution posted,
confirming this needed a real fix here rather than a config change anywhere.

**Actual fix: pace the send side, not just the arrival side.** `PacedRtpRelay`
(`CallTree.Telephony/Audio/PacedRtpRelay.cs`) buffers incoming frames in the same `RtpJitterBuffer`, but
releases **at most one frame per fixed 20ms tick** via a `PeriodicTimer`, completely decoupled from how
bursty arrival was. A tick with nothing ready is simply skipped (PCMU tolerates gaps; the far end's own
loss concealment handles it, same as any two ordinary phones) rather than sending silence. `DisposeAsync`
stops the timer and awaits the pump loop before returning, so the caller can safely close the destination
media session immediately after — `PeriodicTimer.Dispose()` makes a pending or future
`WaitForNextTickAsync()` return `false` rather than throw, which is what lets the pump loop exit on its
own without a `try`/`catch`. Both `RunBridgeAsync` and `RunProxyDialAsync` now use this in place of the
first attempt's `RelayReordered`/`FlushRelay` helpers, which are gone.

Since this fixes shared code, it applies to Phase 4's inbound bridge as much as to this addendum. The four
call-control scenarios already validated there (connect, hangup from each side, unanswered → `Missed`) are
unaffected by any of this, which is about audio smoothness, not call control. Not unit-tested:
`PacedRtpRelay`'s send path needs a live `NatAwareVoIPMediaSession`, and its real behavior is about
wall-clock pacing, which is exactly the category of thing this project's testing philosophy reserves for
real phone calls rather than unit tests.

### ⚠️ KNOWN ISSUE (open, deferred 2026-08-22): `PacedRtpRelay` helped but did not fully fix relay audio quality

**Do not assume this is closed.** The operator re-tested after the `PacedRtpRelay` fix above and confirmed
Phase 4 and the Outbound-proxy addendum both work end to end (correctly connecting, disclosing, recording),
and asked to mark both **done on that basis** — but reported the live call still shows *some* chop/lag on
both bridging paths, and specifically flagged **the caller→`MyCellNumber` direction of the Inbound bridge
(`RunBridgeAsync`'s `toInboundRelay`) as the worst of the affected legs**. Diagnosis was explicitly deferred
to a future session rather than chased further in this one. Read this whole section before touching
`PacedRtpRelay`/`RunBridgeAsync`/`RunProxyDialAsync`/`RtpJitterBuffer` again.

What is already ruled out or already fixed, so a future session doesn't redo this work:

- Sending the instant a packet arrives, with no reordering — fixed (first attempt).
- Sending bursts back-to-back with no pacing between them — fixed (`PacedRtpRelay`'s 20ms tick).
- A Telnyx outbound-voice-profile setting — unlikely as the primary cause: the symptom is asymmetric
  between legs and directions in a way a single account-level setting on one trunk connection wouldn't
  produce, and the original diagnostic signal (recording clean, live choppy) already pointed at the relay,
  not the trunk.

Hypotheses worth investigating first, roughly in order of how cheap they are to test:

1. **`Telephony:JitterBufferMilliseconds` (default 60ms / 3 frames) may be too shallow for real network
   jitter on this path**, especially mobile-network legs. `PacedRtpRelay`'s buffer only smooths what it has
   time to reorder before the next tick demands a frame; if real jitter routinely exceeds the buffer depth,
   the paced tick finds nothing ready more often than it should, which sounds identical to choppiness from
   the listener's side even though the pacing fix is doing its job correctly. Cheap to test: temporarily
   raise the setting (it is a live, per-call setting, no restart needed) and listen for whether it helps -
   if it does, the real fix is either raising the default or making it adaptive, not architecture surgery.
2. **No clock-drift correction over the length of a call.** `PacedRtpRelay`'s `PeriodicTimer` runs on
   CallTree's own wall clock; the sender's RTP stream runs on *its* clock. No two independent clocks agree
   exactly, and nothing here periodically resynchronises the two - a production jitter buffer (e.g. WebRTC's
   NetEQ) adaptively drops or duplicates a frame occasionally to correct for this. Without it, a long
   enough call could still drift, just far more slowly than before this fix. Test by comparing a short call
   against a long one - if choppiness/lag is roughly constant regardless of call length now, this is
   unlikely to be the (remaining) cause; if it still visibly worsens over minutes, this is the next thing
   to build (needs an actual adaptive resync strategy, not a bigger fixed buffer).
3. **The two relay directions may not be as symmetric in practice as the code is.** `RunBridgeAsync`
   constructs `toOutboundRelay` (caller→mobile) and `toInboundRelay` (mobile→caller) identically - same
   jitter depth, same pacing class - so a *code* asymmetry is not the obvious explanation for one leg
   being reported worse. More likely candidates: the caller-side network path (arbitrary inbound callers,
   often mobile, on networks CallTree has no visibility into) is simply jitterier than the trunk-to-DID
   path Phase 3's already-validated Outbound-source recording relies on; or `Telephony:MyCellNumber`'s own
   carrier/handset runs a smaller or more aggressive jitter buffer that is more sensitive to whatever
   residual irregularity remains. Worth confirming with `Telephony:TraceSip` plus a packet capture on both
   legs of the same call, comparing actual inter-arrival timing - if the *inbound* leg's arrivals are
   already irregular at the source, no amount of relay-side pacing fully hides that, and the fix would need
   to be a deeper/adaptive buffer specifically sized to what that leg actually needs, not a uniform 20ms
   assumption applied to both directions alike.
4. Worth double-checking `SendRtpRaw`'s marker-bit argument (currently always `0`) and whether the
   receiving side's own jitter buffer treats a correctly-marked first-packet-after-silence any differently -
   low-probability but cheap to check once the higher-probability items above are ruled out.

### Elsewhere

- `RecordOutboundSourceAsync` starts one long-lived `WaitForHangupAsync` (the periodic recording-tone loop)
  for the whole call and races a fresh `ProxyDialCollector` wait against it each idle period — the tone
  task is created once, not per iteration, so two overlapping tone loops can't fight over the same audio
  source.
- The recording disclosure to the proxy-dialed party (`recording-notice.wav`, new wording: "This call is
  being recorded") plays before `AttachSecondaryLeg()`, mirroring the existing rule that a disclosure must
  never land inside the file it discloses. The prompt previously at that name — played to the operator —
  is renamed `recording-reminder.wav`, wording unchanged. Both are in `PromptLibrary.RequiredPrompts`.
- Scope, matching Phase 4's own non-goals: no DTMF passthrough to the proxy-dialed party; no per-call
  history of which numbers were proxy-dialed beyond structured logs; no audible feedback on an unanswered
  proxy dial beyond a log line (`apology.wav` would be wrong here — it ends "...Goodbye", which doesn't
  apply since the main call keeps going); no extra authentication gate before honoring `*{NUMBER}#` —
  reaching that point already means the Outbound-source path's own caller-ID match (+ optional PIN) passed.

## Cross-cutting work

- **Call commands.** Telephony describes what happened as a `CallCommand` and hands it to `ICallCommands`,
  which owns opening a DI scope per command. Telephony callbacks outlive any request while `DbContext` is
  scoped, and the earlier approach — passing lambdas into a generic `WithLifecycleAsync` helper — put that
  plumbing in front of the reader on every call site.
- **Security.** An open SIP port draws continuous toll-fraud probing; one 40-minute window saw 276 rejected
  INVITEs from four independent sources sweeping international dial prefixes. `Telephony:DidNumber` turns
  these away in-process; importable router allowlists are in [`deploy/firewall/`](deploy/firewall/).
- **Deployment.** Dockerfile, Compose file and a GitHub Actions workflow publishing to the GitHub Container
  Registry live in [`deploy/`](deploy/), with notes for running under a Proxmox LXC. Host networking is
  required because SIP carries addresses inside the message body.

## Call log — API and UI (2026-08-05, out of phase order)

The first vertical slice through the whole stack: a paginated call log, read-only.

- **`GET /api/calls`** returns `PagedResult<CallSummary>`, most recent first, filterable by `source` and
  `status`. Paging clamps rather than rejects — page 0 means page 1, and page size is capped at 200 so one
  request cannot read the table into memory — while an unknown enum name is still a 400 from model binding.
- **Read and write paths are separate ports.** `ICallQueries` returns untracked `CallSummary` read models;
  `ICallRepository` keeps loading whole aggregates for mutation. The API never serializes the `Call`
  aggregate, so the domain's transition surface stays out of the HTTP contract.
- **Enums serialize as names.** `JsonStringEnumConverter` is registered globally: numeric enums would
  silently change meaning the first time a member is inserted mid-enum.
- **UI** at `/calls` — table with source/status filters and a pager. Filter and page state live in the URL,
  so views are linkable and the back button behaves; the filter form is a plain GET form and the pager is
  ordinary links, so it works before hydration. The load function returns fetch failures instead of
  throwing, because "the backend isn't running" is the overwhelmingly likely cause in development and an
  error page cannot say so.
- **Same-origin in development.** Vite proxies `/api` to the API on 5146, so there is no CORS policy in the
  backend and none is needed. Whether production is same-origin is still open — see TODO.md.

### Shipping as one container

The UI is `@sveltejs/adapter-static` in SPA mode, built in a Node stage of `deploy/Dockerfile` and copied
into the API's `wwwroot`. ASP.NET serves it and falls back to `index.html` for unmatched paths, so deep
links work on a cold load. One container, one port, one origin — no CORS policy exists anywhere in the
codebase, and none is needed. The cost is no server-side rendering, which for a LAN-only call log behind a
fetch is invisible.

Two things that had to be got right, both verified by running the built image:

- **Unknown `/api/*` paths must 404**, not fall through to the SPA shell. Left alone, `MapFallbackToFile`
  answered `/api/nope` with `200 text/html`, so a mistyped endpoint looked like success and failed later
  at `JSON.parse`.
- **A locally staged `wwwroot` must not reach the build.** It did on the first attempt: `dotnet publish`
  precompressed it to `.br`/`.gz`, and those survived the later `COPY` of the real UI, because `COPY`
  overwrites files without removing them. The image would have served a stale compressed page to any
  browser sending `Accept-Encoding` while `curl` saw the right one. Now excluded in `.dockerignore`.

Verified end to end in the container: `/`, `/calls`, `/api/calls`, `/health`, a 404 on an unknown API path,
`curl` present for the Compose healthcheck, and telephony correctly idle without trunk configuration.

## Configuration from the UI (2026-08-07)

Trunk and telephony settings are now editable at `/settings` instead of only through environment
variables, which is what makes a container's data volume the whole instance rather than half of it.

- **Three layers**: `appsettings.json` (what ships) < `Storage:ConfigFile` (`/data/config.json`, written
  by the UI, `reloadOnChange`) < environment and user secrets (what this host demands). The file is
  inserted as a configuration source in `Program.AddWritableConfiguration`, and it goes into the volume
  beside the database and the recordings, so moving the volume moves the trunk with it.
- **Ordering is the whole trick, and getting it wrong fails quietly.** The source has to be inserted
  before the *unprefixed* environment source: the host adds `ASPNETCORE_` and `DOTNET_` sources of the
  same type ahead of the appsettings files, so matching on type alone puts the config file *underneath*
  `appsettings.json`. The first run did exactly that, and the symptom was partial — `DidNumber` and
  `PublicHost` (absent from appsettings) saved correctly while `SipListenPort`, `TraceSip` and the whole
  `Trunk` section were silently ignored. It reads as a flaky writer, not an ordering bug.
- **The password is write-only.** It is never returned; a `PUT` that omits it leaves the configured value
  alone. Writing an empty string instead would override the same key coming from user secrets or the
  environment, so saving any unrelated field would blank a working credential.
- **Honest reporting beats silent acceptance.** Sockets are bound and the trunk registered once, at
  startup, and rebinding mid-flight would drop calls and hand the provider's binding to nobody. So those
  settings are deliberately restart-only, and `TelephonySettingsWatcher` — one list, used by both the
  hosted service's log line and the API response — says which saved keys the running stack has not
  picked up. The UI also marks fields an environment variable is overriding, because an edit there
  saves fine and changes nothing.
- **Live settings** are the numbers, the screening digit and timeout, and SIP tracing. The hosted service
  moved from `IOptions` to `IOptionsMonitor` and re-reads them per call.

`.env.example` and `casaos-compose.yml` now ship with the trunk and telephony blocks commented out. For
CasaOS in particular that removes the step where credentials are pasted into a Compose file that ends up
root-readable at `/var/lib/casaos/apps/calltree/`.

### Telephony status page

`GET /api/telephony/status` and a page at `/status` that polls it every 5 seconds. Registration state
used to exist only as four log lines, so "is the trunk up?" meant finding and reading the log.

`TelephonyStatus` holds an immutable snapshot that the registration events replace wholesale — the
events arrive on SIPSorcery's threads while the API reads on request threads, and a page showing a
registered state next to a failure message would be worse than no page.

What it shows is chosen from the bring-up faults in Phase 1, all four of which presented identically as
a caller hearing a busy tone with nothing whatsoever in the log:

- **The Contact the registrar echoed back in its 200 OK** — the address it will actually dial. This is
  the fault that hides best: registration looks perfectly healthy locally while the binding points at a
  LAN address or has no user part at all.
- What we advertise in `Contact` and in SDP, separately, because they fail differently — the first
  stops calls arriving, the second connects the call and then sends the audio nowhere.
- The bound SIP endpoints, the RTP range, whether the DID filter is active, and which prompts loaded.
- Settings saved but waiting on a restart, from the same `TelephonySettingsWatcher` the settings page
  uses.

The last known binding is deliberately kept across a later failure: "registered at 09:12 as this
contact, failing since" reads better than a blank field.

### Telephony:TraceSip is now the only SIP-trace switch

It used to take two settings that had to agree — `Telephony:TraceSip` to attach the handlers and a
`Logging:LogLevel` entry to let Trace through. Setting one without the other produced no output at all,
which looks exactly like a packet that never reached the process: the one conclusion SIP tracing exists
to rule out.

`SipTraceLogLevel`, an `IConfigureOptions<LoggerFilterOptions>` registered after the rules built from the
`Logging` section, raises that category to Trace whenever `TraceSip` is on. Registering it as configured
options rather than a one-off `AddFilter` is what makes it reload: filter options are recomputed on the
`Logging` section's change token and the logger factory refreshes its filters, so the setting can be
flipped from the UI during a misbehaving call rather than needing a restart that drops the registration
and the call being investigated. The trace handlers are attached unconditionally and check
`IsEnabled(Trace)` first, which is what allows that.

### wwwroot has to exist before the builder does

A clean clone would not start in Development: `wwwroot` holds the built UI, only the container build
produces it, so it is gitignored — but the build still emits a static web assets manifest naming it, and
the host reads that manifest *inside* `WebApplication.CreateBuilder`. The result is an unhandled
`DirectoryNotFoundException` before a line of `Main` runs, which no runtime guard can catch. Fixed with
an MSBuild target that creates the directory.

### SQLite cannot sort a DateTimeOffset

Ordering the log by `StartedAt` failed outright: *"SQLite does not support expressions of type
'DateTimeOffset' in ORDER BY clauses."* SQLite has no date type, and EF refuses because rows written with
different UTC offsets would not sort by instant. Range filters have the same flaw but fail silently, which
would have hit Phase 7's date filters later and much less obviously.

Fixed at the mapping rather than in the query: `UtcDateTimeOffsetConverter` normalizes to UTC and stores
text in the exact format EF already used, applied to every `DateTimeOffset` in the model through
`ConfigureConventions`. All 5,376 existing rows read back unchanged and `has-pending-model-changes`
reports nothing — the column was always TEXT, so there is no migration. Ordering by text is now ordering
by instant, because the offset is invariant and a trimmed fraction compares as the zeros it stands for
(`+` sorts before every digit).

Verified against the real database: 5,376 calls, correct ordering across page boundaries, filters, clamping,
and a 400 on a bad enum. 29 new unit tests cover the paging arithmetic and the converter's round-trip and
sort order (63 total).

## Frontend — SvelteKit (scaffolded 2026-08-05)

The UI stack changed from **Next.js to SvelteKit** (Svelte 5 + TypeScript + Tailwind 4, scaffolded with
`sv create`). Nothing had been built on the Next.js scaffold — it was still the unmodified
`create-next-app` output — so this cost nothing and there was no migration to perform.

The frontend is being worked ahead of Phase 3 at the maintainer's request, so the phases are no longer
running in numeric order. Conventions specific to this scaffold that contradict most published Svelte
material are recorded in [`CallTree.UI/AGENTS.md`](CallTree.UI/AGENTS.md).

Currently: the default template page, type-checking clean (`pnpm check`, 0 errors). No CallTree features
yet — there is no API to read from until Phase 7.

## Environment / tooling decisions

- A repo-local `NuGet.config` maps all packages to nuget.org. Without it, machines with a restrictive
  user-level package source mapping fail package restore with NU1100.
- `Microsoft.OpenApi` is pinned to **2.x**: the `Microsoft.AspNetCore.OpenApi` source generator compiles
  against the 2.x object model and fails with CS0200 on 3.x.
- Transitive packages with known advisories are pinned to patched versions.
- `dotnet-ef` is installed via the `dotnet-tools.json` manifest.

## Validation status

- Phase 0: validated (build, tests, boot, migration, `/health`).
- Phase 1: validated end to end over a real trunk.
- Phase 2: validated by phone — prompt audible, DTMF detected, all three outcomes persisted correctly.
- Phase 3: validated by phone — notice played, PIN gate worked, recording played back correctly.
- Phase 4 + 5 (inbound bridge, stereo recording): validated by phone — bridge connects with two-way audio,
  hangup from either side ends the call cleanly, an unanswered ring lands in `Missed` with the apology
  prompt, and the resulting stereo recording plays back with both sides on their correct channel.
- Phase 4 addendum (Outbound proxy dial, `*{NUMBER}#`): validated by phone and operator-confirmed done —
  connect, notice, both sides audible and recorded, `PacedRtpRelay` measurably improved the choppy/growing-lag
  bug found on the first test. **Known open issue** (deferred, not blocking "done"): some chop/lag still
  audible on both bridging paths, worst on the Inbound bridge's caller→`MyCellNumber` direction — see the
  dedicated Known Issue section above before touching the relay code again.
- Configuration layering, hot reload, the password merge rules and the restart-required reporting:
  verified against a running instance, not just unit-tested.
- Unit tests: 129 passing.
