# CallTree — Remaining Work

Ordered by build phase; each phase is validated with real phone calls before the next one starts.
Completed work is in [PROGRESS.md](PROGRESS.md); the project overview is in [README.md](README.md).

## Immediate

- [ ] Choose and add a **licence**. Until then default copyright applies and nobody else may use this.
- [ ] Decide the **consent disclosure wording** and regenerate the prompts with
      `tools/generate-prompts.ps1`. The current greeting ("This call will be recorded") is a placeholder
      chosen to err toward disclosing. Recording law varies by jurisdiction and several require *all*
      parties to consent — this is a decision the operator has to make, not one to inherit from a default.
- [ ] Replace the synthesised placeholder prompts with real recordings.

## Security — before Phase 4

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

## Phase 3 — Outbound-source path + mono recording

- [ ] Auto-answer calls classified `Outbound` (caller ID matches the configured mobile) and record:
      G.711 → PCM decode, jitter buffer (~60 ms), paced 20 ms write clock with silence-fill for gaps,
      NAudio `WaveFileWriter`.
- [ ] Finalize the WAV on hangup; persist `Recording` (path, duration, size, `FinalizedAt`).
- [ ] Decide: caller-ID match alone, or caller ID plus a DTMF PIN, before recording starts. Caller ID is
      trivially spoofable and this path auto-answers and records.
- [ ] The consent disclosure decision is needed by here — spoken notice, periodic tone, or both, and on
      which paths.

## Phase 4 — Inbound bridge

- [ ] On a successful gate: place an outbound leg to the configured mobile (second `SIPUserAgent`) and
      bridge RTP both directions (payload relay; no transcode while both legs are PCMU).
- [ ] Failure handling: no-answer timeout → apology prompt → `Missed`; either side hangs up → clean teardown.
- [ ] Refactor per-call handling out of `TelephonyBackgroundService` into a `CallSession` runtime class plus
      an `ActiveCallRegistry`. Phases 1–2 deliberately kept it inline.
- [ ] Replace `Call.CompleteScreening` (the Phase 2 stand-in for "passed the gate, nothing to bridge to")
      with `BeginDialing` + `Bridge`.
- [ ] Replace the single long-lived `SIPUserAgent` with a per-call agent. One agent holds one dialogue, so a
      second concurrent call — or a dialogue left behind by an abnormal teardown — is *silently dropped*
      with no log line, which is indistinguishable from a network fault.
- [ ] Re-check the trunk account's concurrency and per-call duration caps. A 10-minute ceiling would
      truncate recordings; some providers apply one on lower tiers.

## Phase 5 — Bridged-call recording

- [ ] Tap the decoded PCM of each leg into one stereo WAV (left = caller, right = the mobile) on a shared
      20 ms clock.

## Phase 6 — Trunk resilience

- [ ] Registration resilience: backoff tuning, network-blip recovery, registration state exposed on `/health`.
- [ ] `Trunk:AuthUsername` is currently warned about but not honoured. Wiring it up means moving to the long
      `SIPRegistrationUserAgent` overload (AOR, realm, contact URI, custom headers). Only needed for
      providers that split the SIP and auth usernames.
- [ ] Startup sweep to repair unfinalized WAVs — recompute the RIFF sizes from the file length and close the
      `FinalizedAt` gap left by a crash mid-write.
- [ ] `Telephony:PublicHost` is a static value. Residential IPs change; switch to a DDNS hostname or
      discover the public address at startup via SIPSorcery's `STUNClient` and re-check it periodically.

## Phase 7 — REST API

- [ ] List and filter calls (source, date range, number, duration), plus a call detail endpoint.
- [ ] Stream recordings with HTTP range support so playback can seek.
- [ ] Decide the auth posture: LAN-only, or authenticated remote access.

## Phase 8 — Frontend

- [ ] Next.js recordings browser and player. Read the guides under `CallTree.UI/node_modules/next/dist/docs/`
      first — the installed version has breaking changes relative to older conventions.
- [ ] Dockerfile for the frontend (Next standalone output) and add it to the Compose file. The backend
      container already exists in [`deploy/`](deploy/).
- [ ] Retention policy: keep forever, delete after N days, or cap by size? Optionally transcode older
      recordings to Opus or MP3. Stereo PCM WAV runs about 1.9 MB per minute.

## Open decisions

1. Consent disclosure approach and wording — needed by Phase 3.
2. Outbound-path authentication: caller-ID match only, or caller ID plus a PIN. A PIN is the safer default
   given how easily caller ID is spoofed.
3. Retention policy — Phase 8.
4. API and UI exposure and authentication — Phases 7–8. Recordings are sensitive; the default posture is
   LAN-only with no external exposure.
5. Licence.
