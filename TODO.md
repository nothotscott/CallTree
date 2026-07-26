# CallTree — Remaining Work

Ordered per the approved phase plan (each phase gets manual phone validation before the next starts).
Full plan with rationale: `C:\Users\Scott\.claude\plans\project-brief-self-hosted-serene-music.md`.
Completed work: [PROGRESS.md](PROGRESS.md).

## Immediate

- [ ] Manual Phase 1 validation against FreePBX/Asterisk (register as extension, dial from a phone, check
      logs + `data/calltree.db`). Secrets go in user secrets: `Trunk:Host`, `Trunk:Username`, `Trunk:Password`,
      `Telephony:MyCellNumber`.
- [ ] First git commit (Phases 0–1).

## Phase 2 — Media out + DTMF in

- [ ] Play a WAV prompt on answer (PCM → G.711 via SIPSorcery `AudioExtrasSource.SendAudioFromStream`).
- [ ] DTMF detection via RFC 4733 (`OnDtmfTone` — logging already wired).
- [ ] Press-1 IVR gate: on "1" play confirmation and hang up (no bridge yet); timeout → `ScreenedOut`.
- [ ] Prompt WAV assets + where they live (embedded resource vs content directory).

## Phase 3 — Outbound-source path + mono recording

- [ ] Auto-answer calls classified `Outbound` (caller ID = my cell) and record: G.711 → PCM decode,
      jitter buffer (~60 ms), paced 20 ms write clock (silence-fill for gaps), NAudio `WaveFileWriter`.
- [ ] Finalize WAV on hangup; persist `Recording` (path, duration, size, `FinalizedAt`).
- [ ] Decide: CLI-only vs CLI+PIN before recording starts (open question — caller ID is spoofable).
- [ ] Consent disclosure decision needed by here (FL all-party consent): tone vs spoken notice, on which paths.

## Phase 4 — Inbound bridge

- [ ] On "1": place outbound leg to my cell (second `SIPUserAgent`), bridge RTP both directions
      (payload relay, no transcode while both legs are G.711/PCMU).
- [ ] Failure handling: cell no-answer timeout → apology prompt → `Missed`; either side hangs up → clean teardown.
- [ ] Refactor per-call handling out of `TelephonyBackgroundService` into a `CallSession` runtime class +
      `ActiveCallRegistry` (planned shape; Phase 1 deliberately kept it inline).

## Phase 5 — Bridged-call recording

- [ ] Tap decoded PCM of each leg into one stereo WAV (left = caller, right = my cell) on a shared 20 ms clock.

## Phase 6 — Real trunk cutover + resilience

- [ ] Choose trunk provider (open question: VoIP.ms lean vs Telnyx vs Flowroute) and point CallTree at it.
- [ ] Registration resilience: backoff tuning, network-blip recovery, registration state on `/health`.
- [ ] Startup sweep to repair unfinalized WAVs (recompute RIFF sizes from file length; clear `FinalizedAt` gap).
- [ ] Constrain SIPSorcery RTP ports to `Telephony:RtpPortStart/End` (currently defaults) to match router forwards.

## Phase 7 — REST API

- [ ] List/filter calls (source, date range, number, duration) + call detail endpoints.
- [ ] Stream recordings with HTTP range support (seekable playback).
- [ ] Auth posture decision (open question: LAN-only vs authenticated remote access).

## Phase 8 — Frontend + deployment

- [ ] Next.js recordings browser/player (Next 16.2 — read `CallTree.UI/node_modules/next/dist/docs/` first;
      breaking changes vs older Next).
- [ ] Dockerfiles (backend multi-project publish; frontend Next standalone) + `docker-compose.yml`;
      backend on `network_mode: host` for SIP/RTP; bind mounts `/srv/calltree/{data,recordings}`.
- [ ] Deploy to `control-server` LXC on Proxmox; router port-forward 5060/udp + RTP range.
- [ ] Retention job (open question: keep-forever vs N days vs size cap; optional Opus/MP3 transcode of old files).
- [ ] Final consent-disclosure wording baked into prompts.

## Open decisions (Scott)

1. Trunk provider (needed by Phase 6) — lean: VoIP.ms.
2. Consent disclosure approach + wording (ideally by Phase 3). Florida is all-party consent.
3. Outbound-path auth: CLI match only vs CLI + PIN (recommended: CLI + PIN).
4. IVR prompt wording (Phase 2; placeholder fine).
5. Retention policy (Phase 8).
6. API/UI exposure & auth (Phase 7–8).
