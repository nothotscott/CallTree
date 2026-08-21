# CallTree — Remaining Work

Ordered by build phase; each telephony phase is validated with real phone calls before the next one starts.
Completed work is in [PROGRESS.md](PROGRESS.md); the project overview is in [README.md](README.md).

**Phases are not being worked in numeric order.** Frontend work (Phase 8) has been brought forward ahead of
Phase 3. The numbers stay as they are because they are referenced throughout these documents; treat them as
labels, not a schedule. Phase 7's REST API is the UI's data source, so until it exists the UI has nothing
real to render.

## Immediate

- [x] Choose and add a **licence** — MIT, in [LICENSE.md](LICENSE.md).
- [ ] Decide the **consent disclosure wording** and regenerate the prompts with
      `tools/generate-prompts.ps1`. The current greeting ("This call will be recorded") is a placeholder
      chosen to err toward disclosing. Recording law varies by jurisdiction and several require *all*
      parties to consent — this is a decision the operator has to make, not one to inherit from a default.
- [ ] Decide whether to enable the **recording tone** (`Telephony:RecordingToneIntervalSeconds`, off by
      default) and at what interval. This is the *only* disclosure the outbound path can make to the
      party added by the handset's three-way merge: they never hear `recording-notice.wav`, because
      CallTree is not told the merge happened. Out of the box, telling them is the operator's job and
      has to be done out loud.
- [ ] Replace the synthesised placeholder prompts with real recordings. Six now, not three —
      `recording-notice` and `pin-request` were added in Phase 3, and `recording-tone` is a generated
      1400 Hz tone rather than speech.

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

**Written and unit-tested; not yet validated over the trunk.** The RTP tap, the DTMF PIN entry and the
new prompts have never run against a real call — only against unit tests that feed the recorder packets
directly. This phase is not done until a real call has been placed.

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
- [ ] **Validate by phone**: call the DID from the mobile, confirm the notice plays, hang up, and check
      the WAV plays back at the right length with both sides audible after a three-way merge.
- [ ] The consent disclosure decision is still open — see Immediate. The mechanisms now exist
      (`recording-notice.wav`, and a periodic tone via `Telephony:RecordingToneIntervalSeconds`); what
      is undecided is the wording, the interval, and whether a tone is required at all.

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
      20 ms clock. `CallRecorder` cannot be reused as-is: it writes from the RTP timestamp, and two legs
      have two unrelated RTP clocks with nothing to align them to. This is the case that needs the wall
      clock Phase 3 did without.

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

1. Consent disclosure approach and wording, and whether to turn the periodic tone on. The mechanisms are
   built; what they say and when is not decided.
2. Outbound-path authentication: caller-ID match only, or caller ID plus a PIN. Both are now supported —
   `Telephony:OutboundPin`, blank by default — so this is a configuration decision rather than a build
   one. A PIN is the safer default given how easily caller ID is spoofed, and it matters much more once
   Phase 4 can place an outbound leg.
3. Retention policy — Phase 8.
4. API and UI exposure and authentication — Phases 7–8. Recordings are sensitive; the default posture is
   LAN-only with no external exposure. Note the UI and API now share one port, so exposing one exposes
   the other — there is no arrangement where the browser reaches the UI but not `/api`.
