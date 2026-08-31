# CallTree — Remaining Work

Ordered by build phase; each telephony phase is validated with real phone calls before the next one starts.
Completed work is in [PROGRESS.md](PROGRESS.md); the project overview is in [README.md](README.md).

**Phases are not being worked in numeric order.** Frontend work (Phase 8) has been brought forward ahead of
Phase 3. The numbers stay as they are because they are referenced throughout these documents; treat them as
labels, not a schedule. Phase 7's REST API is the UI's data source, so until it exists the UI has nothing
real to render.

## Immediate

- [x] Choose and add a **licence** — MIT, in [LICENSE.md](LICENSE.md).
- [x] Decide the **consent disclosure approach**. Operator decision: the spoken notice on each path
      (`greeting.wav` for Inbound, `recording-reminder.wav` for Outbound) is sufficient — confirmed audible
      on real calls — and the periodic **recording tone stays off** (`Telephony:RecordingToneIntervalSeconds`
      remains `0`, not just its default). The structural gap is unchanged and still applies: on the
      Outbound path this reminder reaches only the operator, never a party added later via the handset's
      *native* merge — see [Recording consent](README.md#recording-consent--read-this). (A party reached
      through the later-added outbound proxy dial, `*{NUMBER}#`, is a separate case: CallTree placed that
      leg itself and does disclose to them directly, via a differently-worded `recording-notice.wav`.)
- [ ] Replace the synthesised placeholder prompts with real recordings. Now eight, not three —
      `recording-reminder`, `recording-notice`, `pin-request`, `apology` and `ringing` were added across
      later phases, and `recording-tone` is a generated tone rather than speech. Regenerate with
      `tools/generate-prompts.ps1` if it ever needs to change.

## Security — before real inbound traffic is bridged out to the trunk

Phase 4 can now place a real outbound leg, so a spoofed or successfully-guessed screening pass turns a
probe into a phone bill rather than just disk usage — see PROGRESS.md.

- [x] **Reject INVITEs not addressed to our DID** (`Telephony:DidNumber`, 404 before any Call row is
      created). Scanners sweep dial-plan prefixes (`011…`, `9011…`, `00…`, bare) aimed at premium-rate
      international destinations — textbook toll-fraud enumeration. Before the filter every probe was
      answered and heard 12 s of greeting, which both confirms a live PBX to the attacker and fills the
      database with junk.
- [ ] **Restrict the router port-forwards by source address.** This is the cheaper layer: it drops probes
      before they reach the host. Importable lists are in [`deploy/firewall/`](deploy/firewall/). The probes
      do *not* arrive via the trunk — they are sent straight to the public IP on 5060, so no provider-side
      account setting can stop them.
- [ ] Optionally mirror that allowlist in `HandleIncomingCallAsync` as defence in depth, for when the
      forward gets widened or the host moves.
- [ ] Consider rate-limiting or temporarily blocklisting sources that are repeatedly rejected — probes
      arrive in bursts and each still costs a SIP transaction.

## Phase 3 — Outbound-source path + mono recording ✅ (validated by phone)

Confirmed against a real call: the notice played, the PIN gate worked, and the resulting `.wav` played
back correctly.

- [x] Auto-answer calls classified `Outbound` (caller ID matches the configured mobile) and record:
      G.711 → PCM decode, reordering buffer (`Telephony:JitterBufferMilliseconds`, 60 ms), silence-fill
      for gaps, NAudio `WaveFileWriter`.
      **Deviation from the original plan:** there is no paced 20 ms write clock. The RTP timestamp *is*
      the sample clock for PCMU, so the file is written from it directly — no drift against the sender,
      and gaps are exactly as long as the packets that went missing. A wall clock would have to be
      reconciled with the RTP clock anyway, and would compress or stretch the recording when they differ.
      Phase 5 is the case that genuinely needs one, because two legs have two unrelated RTP clocks.
- [x] Finalize the WAV on hangup; persist `Recording` (path, duration, size, `FinalizedAt`).
- [x] Decide: caller-ID match alone, or caller ID plus a DTMF PIN, before recording starts. Built as
      `Telephony:OutboundPin`, **blank by default** so the phase can be brought up without settling it.
      A failed PIN lands the call in `ScreenedOut`, so a spoofing attempt is distinguishable in the log
      from a call that simply finished. Still worth an explicit decision before Phase 4.
- [x] **Validate by phone**: call the DID from the mobile, confirm the notice plays, hang up, and check
      the WAV plays back at the right length with both sides audible after a three-way merge.
- [x] The consent disclosure decision — see Immediate. Decided: the spoken notice
      (`recording-reminder.wav`) is sufficient; the periodic tone via `Telephony:RecordingToneIntervalSeconds`
      stays off.

## Phase 4 — Inbound bridge ✅ (validated by phone)

Confirmed against real calls: the bridge connects with two-way audio, a caller hangup ends the mobile leg,
a mobile hangup ends the caller leg, and an unanswered ring lands in `Missed` with the apology prompt
heard. See PROGRESS.md for what shipped and what was deliberately left out of this pass (no
`CallSession`/`ActiveCallRegistry` refactor, no DTMF passthrough) and remains open below. **See also the
known residual audio-choppiness/lag issue documented under the Phase 4 addendum below** — it affects this
bridge too (the caller→`MyCellNumber` direction specifically flagged as worst), deferred, not yet diagnosed.

- [x] On a successful gate: place an outbound leg to the configured mobile (second `SIPUserAgent`) and
      bridge RTP both directions (payload relay; no transcode while both legs are PCMU).
- [x] Failure handling: no-answer timeout (`Telephony:DialTimeoutSeconds`) → apology prompt → `Missed`;
      either side hangs up → the other is hung up too, exactly once.
- [x] Replace `Call.CompleteScreening` (the Phase 2 stand-in for "passed the gate, nothing to bridge to")
      with `BeginDialing` + `Bridge`.
- [x] Ringback tone (`ringing.wav`, 440+480 Hz looped with a 4s gap) plays to the caller for as long as the
      outbound leg is ringing, so they aren't in dead silence while `Telephony:DialTimeoutSeconds` runs.
      Added after phone testing surfaced the silence as worth fixing before calling the phase done.
- [ ] Refactor per-call handling out of `TelephonyBackgroundService` into a `CallSession` runtime class plus
      an `ActiveCallRegistry`. Deliberately deferred — see PROGRESS.md's scope note. Still worth doing before
      Phase 6 needs to reason about more than one active call.
- [ ] Replace the single long-lived `SIPUserAgent` with a per-call agent. One agent holds one dialogue, so a
      second concurrent *inbound* call — or a dialogue left behind by an abnormal teardown — is *silently
      dropped* with no log line, which is indistinguishable from a network fault. Pre-existing since Phase 1;
      not worsened by the bridge, which uses its own separate per-call agent for the outbound leg.
- [ ] Re-check the trunk account's concurrency and per-call duration caps. A 10-minute ceiling would
      truncate recordings; some providers apply one on lower tiers.

### Phase 4 addendum — Outbound proxy dial ✅ (validated by phone; known residual audio-quality issue, see below)

A self-hosted outbound calling proxy on the Outbound-source path: while on a call from `MyCellNumber`,
dialing `*{NUMBER}#` places a *new* leg from the DID to `{NUMBER}`, so the far end sees the DID rather than
the operator's real mobile number. Reuses Phase 4's dial/ringback primitives (see PROGRESS.md for the
`PlaceOutboundLegAsync` extraction) and mixes the proxy party's audio live into the same continuous mono
recording rather than starting a second one — see PROGRESS.md for why that was the harder half of this.

- [x] `*{NUMBER}#` DTMF collection (`ProxyDialCollector`), persistent for the call's whole duration rather
      than one-shot like `ScreeningGate`/`PinGate` — a bad entry resets to idle instead of ending the call.
- [x] Reused ring-back (`ringing.wav`) plays to the operator while the proxy leg dials.
- [x] New `recording-notice.wav` ("This call is being recorded") plays to the proxy-dialed party on
      connect — the one leg on this path CallTree can actually disclose to directly. The prompt previously
      at that name (played to the operator) is renamed `recording-reminder.wav`, same wording.
- [x] `CallRecorder` gained an attachable/detachable secondary leg, summed (clamped) into the same mono
      file live rather than starting a new `Recording` — the operator can dial `*{NUMBER}#` more than once
      in the same call.
- [x] `PlaceOutboundLegAsync` extracted from `BridgeToMobileAsync` as a shared, trigger-agnostic dial
      primitive — written for the future Web-softphone phase to call the same way.
- [x] **Validate by phone**: confirmed working — ring-back, the notice playing to the answering party, both
      sides audible, both present in the recording.
- [x] Relay audio quality: a first fix (reorder before relaying) turned out insufficient — real testing
      still found the live call choppy, with lag that grew as the call went on. Root cause was pacing, not
      ordering: bursty arrival was still relayed as a burst, and the far end's own adaptive jitter buffer
      grows its buffering target in response. Fixed with `PacedRtpRelay` (fixed 20ms send cadence via a
      `PeriodicTimer`, decoupled from arrival timing) — see PROGRESS.md's bring-up fault. Also applies to
      Phase 4's inbound bridge, sharing the same relay code.
- [x] **Re-validated by phone** (2026-08-22): the operator confirmed Phase 4 and this addendum are both
      working and directed calling them done, despite the known residual audio-quality issue below — that
      issue is tracked separately rather than blocking completion.
- [ ] No audible feedback on an unanswered proxy dial beyond a log line — see PROGRESS.md's scope note.
      A purpose-built prompt is a reasonable small follow-up, not required for this to be considered done.

#### ⚠️ KNOWN ISSUE (open, deferred): residual audio choppiness/lag survives `PacedRtpRelay`

**Not fixed. Explicitly deferred by the operator on 2026-08-22 — diagnose in a dedicated future session,
do not assume closed.** `PacedRtpRelay` (fixed 20ms send cadence) measurably helped but did not eliminate
the problem: real calls still show some chop/lag, on **both** the Inbound bridge and the Outbound proxy
dial, and the operator specifically flagged the **caller→`MyCellNumber` direction of the Inbound bridge as
the worst** of the affected legs. See PROGRESS.md for the full write-up and diagnostic hypotheses to start
from (buffer depth too shallow for real network jitter, long-call clock-drift between our `PeriodicTimer`
and the sender's RTP clock with no resync mechanism, and the possibility that the two relay directions
are not actually symmetric in practice even though the code is). Read PROGRESS.md's "Known issue" section
in full before touching `PacedRtpRelay`/`RunBridgeAsync`/`RunProxyDialAsync` again.

## Phase 5 — Bridged-call recording ✅ (the slice built alongside Phase 4, validated by phone)

- [x] Tap the decoded PCM of each leg into one stereo WAV (left = caller, right = the mobile) on a shared
      wall clock (`BridgedCallRecorder`, draining a per-leg accumulator on packet arrival from either leg
      rather than pacing off either leg's own RTP clock). `CallRecorder` was not reusable as-is: it writes
      from the RTP timestamp, and two legs have two unrelated RTP clocks with nothing to align them to.
      Confirmed by phone: both sides audible on their correct channel in the resulting recording.

## Phase 9 — SMS ✅ (receiving validated; sending blocked by 10DLC registration, not by code)

Texts on the same DID, classified by sender the way calls are classified by caller ID. This one does not
touch SIP at all: the provider delivers by HTTPS webhook and accepts sends over its REST API, so it lives
in its own `CallTree.Messaging` project alongside `CallTree.Telephony` rather than inside it.

- [x] `Message` aggregate + `Relay` (`Received → Relaying → Relayed | Rejected | Failed`), with delivery
      as a fact on the relay rather than a status on the message — same rule as `Recording` on a call.
- [x] Inbound: record and forward to `Telephony:MyCellNumber` with the sender's number on the front.
- [x] Outbound: `{RECIPIENT-NUMBER} Body of text` from the mobile, sent from the DID. `SmsCommand` parses
      at whitespace boundaries with an ordered pair of stop rules — see the gotcha in CLAUDE.md before
      touching it.
- [x] Ed25519 webhook signature verification, failing closed, with a replay window.
- [x] Idempotency on the provider's message id (unique index + check), because the webhook is retried.
- [x] Delivery receipts (`message.sent` / `message.finalized`), which never walk a verdict backwards.
- [x] `Messaging:` settings section, all of it live (no restart), with the API key write-only.
- [x] `GET /api/messages` and a `/messages` page in the UI, with source/status filters and a body search.
- [x] `LineOptions`: `Telephony:DidNumber` / `Telephony:MyCellNumber` moved to Application so Telephony
      and Messaging can both read them without one referencing the other. Config keys unchanged.
- [x] **Validated with real texts**, and the webhook exposed over HTTPS. Receiving works end to end.
- [x] Receive-only mode. Sending is refused by the carrier (`The sending number is not 10DLC-registered
      but is required to be by the carrier`), so a blank `Messaging:ApiKey` is now a first-class mode:
      messages end at the new terminal `MessageStatus.Recorded` rather than `Failed`, and the UI hides
      everything about relaying. See PROGRESS.md for why that status has to exist.
- [x] UI follows what the line can do: the Messages nav link only appears when SMS is enabled, and the
      Relayed/Source columns and their filters only when there is a key to send with
      (`GET /api/messages/capabilities`).
- [x] A **Send as well as receive** switch on the settings page, so an API key can be cleared from the UI
      at all — "blank means unchanged" made that impossible before, the same gap the outbound PIN had.
- [ ] **10DLC registration**, if outbound SMS is ever wanted. Brand + campaign registration through the
      provider; one-off fee plus a monthly campaign charge, and a sole proprietor can register without an
      EIN at a lower throughput tier. **No code work** — the send paths are written and tested; set
      `Messaging:ApiKey` once the number is approved and they take over.
- [ ] Forward MMS media rather than only counting it. Would mean re-sending the provider's media URLs at
      MMS rates, with its own failure modes — deliberately out of scope for the first cut.
- [ ] Consider a sticky reply target, so a reply to a forwarded message does not need the number typed
      again. Rejected for now: it is invisible state that decides who a message goes to, and getting it
      wrong sends the operator's text to the wrong person.
- [ ] Message detail view / conversation threading in the UI. The list shows the received body and what
      was relayed; there is no per-number thread.
- [ ] Retention: message bodies are the most sensitive thing in the database after the recordings, and
      nothing prunes them. Same open question as recording retention, below.

## Phase 6 — Trunk resilience

- [ ] Registration resilience: backoff tuning and network-blip recovery. Registration state is now
      tracked in `TelephonyStatus` and exposed at `GET /api/telephony/status`; surfacing it on `/health`
      as well would let a container orchestrator act on it, which the status page cannot.
- [ ] `Trunk:AuthUsername` is currently warned about but not honoured. Wiring it up means moving to the long
      `SIPRegistrationUserAgent` overload (AOR, realm, contact URI, custom headers). Only needed for
      providers that split the SIP and auth usernames.
- [ ] Startup sweep to repair unfinalized WAVs — recompute the RIFF sizes from the file length and close the
      `FinalizedAt` gap left by a crash mid-write. Less urgent than it was: `CallRecorder` re-patches the
      header every five seconds, so a killed process leaves a file that plays up to the last flush. What
      the sweep still fixes is the `Recording` row with a null `FinalizedAt`, and the last few seconds.
- [ ] `Telephony:PublicHost` is a static value. Residential IPs change; switch to a DDNS hostname or
      discover the public address at startup via SIPSorcery's `STUNClient` and re-check it periodically.

## Phase 7 — REST API (started, out of order)

- [x] `GET /api/calls` — paginated list, most recent first, filterable by source and status.
- [x] `GET`/`PUT /api/config` — read and save the Telephony and Trunk sections to a writable config
      file layered under the environment. The password is write-only.
- [x] `GET /api/telephony/status` — trunk registration state, the registrar's failure message, the
      Contact it echoed back, the bound endpoints, and what is advertised in Contact and SDP.
- [x] `PATCH /api/recordings/{id}` — rename a recording, plus a `search` filter on
      `GET /api/recordings` matching a case-insensitive substring of the name. Every recording is born
      with a name built from the caller and the recording date (`RecordingName`); rows predating the
      column were backfilled by the `AddRecordingName` migration. Blank is rejected rather than taken as
      "restore the default" — the caller and date it would be rebuilt from are fields of their own.
- [ ] Remaining filters: date range, number, duration.
- [ ] Call detail endpoint (`GET /api/calls/{id}`) exposing the legs, which the list summary flattens away.
- [ ] Stream recordings with HTTP range support so playback can seek.
- [ ] Decide the auth posture: LAN-only, or authenticated remote access. **There is currently no auth and
      no CORS policy**; the API is reachable by anything that can reach the port. The settings endpoint
      raises the stakes: `GET /api/config` discloses the DID, the mobile number, the public host and the
      trunk username, and `PUT` can repoint the trunk or clear the DID filter that turns away toll-fraud
      probes. This is now the strongest argument for auth, and it should land before any remote exposure.

## Phase 8 — Frontend (in progress, out of order)

The stack is **SvelteKit** (Svelte 5, TypeScript, Tailwind 4). It replaced the original Next.js scaffold on
2026-08-05; nothing had been built on that scaffold, so there was no migration to do. See
[`CallTree.UI/AGENTS.md`](CallTree.UI/AGENTS.md) for the conventions that differ from what most tutorials
show.

- [x] Scaffold the project (`sv create`, minimal template, prettier + eslint + tailwind).
- [x] Call log at `/calls`: paginated table, source and status filters, filter state in the URL. Reads
      `GET /api/calls` through the Vite dev proxy, so it is same-origin with no CORS.
- [x] Settings page at `/settings`: edits the Telephony, Recording and Trunk sections, marks the fields
      an environment variable is overriding, and reports which saved keys are waiting on a restart. The
      trunk password and the outbound PIN are both write-only.
- [x] Telephony status at `/status`: registration state, the registrar's failure message and the binding
      it echoed back, plus warnings for the failures that otherwise present identically (no public host,
      no DID filter, missing prompts, settings waiting on a restart). Polls every 5 s.
- [x] Name recordings from the UI: the detail page title *is* the name, edited in place the way Azure
      DevOps edits a work-item title (Enter or Save commits, Escape puts back what was last saved, the
      buttons appear only once it differs). The list gained a Name column beside Caller and a name
      search whose state lives in the URL, same as the call log's filters.
- [ ] Restart the service from the UI, so a trunk change does not need shell access. Needs care: the
      process supervises itself only under Compose's `restart: unless-stopped`, and there is no auth.
- [ ] Live call state on the status page — there is no runtime call registry until Phase 4's
      `CallSession`/`ActiveCallRegistry`, so "is a call up right now" cannot be answered yet.
- [ ] Call detail view, once the detail endpoint exists.
- [ ] Recording player. Phase 3 produces recordings now, so what is missing is the streaming endpoint
      (with range support) rather than the data.
- [x] Decide how the UI reaches the API in production: **same-origin**, served by the ASP.NET host from
      `wwwroot`. No CORS policy exists or is needed. Revisit only if SSR becomes worth a second container.
- [x] Replace `@sveltejs/adapter-auto` with a concrete adapter — now `adapter-static` in SPA mode.
- [x] Ship the UI inside the existing backend image rather than a second container: `deploy/Dockerfile`
      builds it in a Node stage and copies it into the API's `wwwroot`. One port, one origin, no CORS.
- [ ] Recordings browser and player, once Phase 7 exposes the data.
- [ ] Dockerfile for the frontend and add it to the Compose file. The backend container already exists in
      [`deploy/`](deploy/); the CasaOS variant will need the extra service too.
- [ ] Retention policy: keep forever, delete after N days, or cap by size? Optionally transcode older
      recordings to Opus or MP3. Stereo PCM WAV runs about 1.9 MB per minute.

## Open decisions

1. ~~Consent disclosure approach, and whether to turn the periodic tone on.~~ **Decided**: the spoken
   notice on each path is the disclosure; the tone stays off. See Immediate, above.
2. Outbound-path authentication: caller-ID match only, or caller ID plus a PIN. Both are now supported —
   `Telephony:OutboundPin`, blank by default — so this is a configuration decision rather than a build
   one. A PIN is the safer default given how easily caller ID is spoofed, and it matters more now that
   Phase 4 places a real outbound leg — a successful spam-gate pass now dials the trunk, at the trunk's
   cost, not just disk.
3. Retention policy — Phase 8.
4. API and UI exposure and authentication — Phases 7–8. Recordings are sensitive; the default posture is
   LAN-only with no external exposure. Note the UI and API now share one port, so exposing one exposes
   the other — there is no arrangement where the browser reaches the UI but not `/api`.
