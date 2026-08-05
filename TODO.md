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

## Phase 7 — REST API (started, out of order)

- [x] `GET /api/calls` — paginated list, most recent first, filterable by source and status.
- [ ] Remaining filters: date range, number, duration.
- [ ] Call detail endpoint (`GET /api/calls/{id}`) exposing the legs, which the list summary flattens away.
- [ ] Stream recordings with HTTP range support so playback can seek.
- [ ] Decide the auth posture: LAN-only, or authenticated remote access. **There is currently no auth and
      no CORS policy**; the API is reachable by anything that can reach the port.

## Phase 8 — Frontend (in progress, out of order)

The stack is **SvelteKit** (Svelte 5, TypeScript, Tailwind 4). It replaced the original Next.js scaffold on
2026-08-05; nothing had been built on that scaffold, so there was no migration to do. See
[`CallTree.UI/AGENTS.md`](CallTree.UI/AGENTS.md) for the conventions that differ from what most tutorials
show.

- [x] Scaffold the project (`sv create`, minimal template, prettier + eslint + tailwind).
- [x] Call log at `/calls`: paginated table, source and status filters, filter state in the URL. Reads
      `GET /api/calls` through the Vite dev proxy, so it is same-origin with no CORS.
- [ ] Call detail view, once the detail endpoint exists.
- [ ] Recording player, once Phase 3 produces recordings.
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

1. Consent disclosure approach and wording — needed by Phase 3.
2. Outbound-path authentication: caller-ID match only, or caller ID plus a PIN. A PIN is the safer default
   given how easily caller ID is spoofed.
3. Retention policy — Phase 8.
4. API and UI exposure and authentication — Phases 7–8. Recordings are sensitive; the default posture is
   LAN-only with no external exposure. Note the UI and API now share one port, so exposing one exposes
   the other — there is no arrangement where the browser reaches the UI but not `/api`.
