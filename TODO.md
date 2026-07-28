# CallTree — Remaining Work

Ordered per the approved phase plan (each phase gets manual phone validation before the next starts).
Full plan with rationale: `C:\Users\Scott\.claude\plans\project-brief-self-hosted-serene-music.md`.
Completed work: [PROGRESS.md](PROGRESS.md).

## Immediate

- [x] Manual Phase 1 validation — real call from the cell through Telnyx, answered and persisted.
- [ ] **Upgrade off the Telnyx trial tier so anyone can call the DID.** Trial restricts voice in *both*
      directions to a single verified number ("Inbound limited to receiving from the verified phone
      number"), one verified number at a time, 10 changes per account lifetime. Adding the cell as the
      verified number unblocked testing, but every other caller still gets refused before an INVITE is sent
      (`486`/`D61` inbound, `603`/`D38` outbound, blank Connection Id in the CDR either way). The Paid tier
      documents no inbound restriction. Trial also caps 2 concurrent outbound calls and 10 minutes per
      call — the 10-minute ceiling will truncate recordings, so this is a hard blocker by Phase 3/5.
- [ ] Constrain the codec offer to PCMU. The answer currently echoes the trunk's full list and Telnyx put
      G722 first, so a real call negotiates G722 — the plan assumes PCMU-only, and Phase 3's decode and
      Phase 4's payload-relay bridging both depend on that.
- [ ] First git commit (Phases 0–1).

## Security — before Phase 4

- [ ] **Restrict inbound SIP to Telnyx's signalling ranges.** Within ~30 minutes of exposing UDP 5060 the
      log picked up four unsolicited probes, including `OPTIONS sip:100@47.204.201.45` — an extension sweep
      looking for an open PBX. Today the risk is low (Phase 1 answers, holds 5 s, hangs up, and cannot dial
      out), but Phase 4 adds outbound legs, at which point an unauthenticated INVITE from the internet
      becomes a toll-fraud vector. Allowlist at the router *and* reject in `HandleIncomingCallAsync` on
      unknown source addresses.

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

- [x] Choose trunk provider — **Telnyx**, DID (941) 304-0304, credential registration as `voipserver@sip.telnyx.com`.
- [x] Constrain SIPSorcery RTP ports to `Telephony:RtpPortStart/End` to match router forwards.
- [ ] Registration resilience: backoff tuning, network-blip recovery, registration state on `/health`.
- [ ] `Trunk:AuthUsername` is currently only warned about, not honoured — wiring it up means moving to the
      long `SIPRegistrationUserAgent` overload (AOR, realm, contact URI, custom headers). Telnyx doesn't
      need it; do it if a provider that splits SIP and auth usernames is ever used.
- [ ] Startup sweep to repair unfinalized WAVs (recompute RIFF sizes from file length; clear `FinalizedAt` gap).
- [ ] `Telephony:PublicHost` is currently a hard-coded WAN IP in user secrets. Residential IPs change —
      switch to a DDNS hostname, or discover the public address at startup via SIPSorcery's `STUNClient`
      and re-check it periodically.
- [ ] Replace the single long-lived `SIPUserAgent` with a per-call agent. One agent holds one dialogue, so a
      second concurrent call (or a dialogue left behind by an abnormal teardown) is *silently dropped* with no
      log line — indistinguishable from a network fault. Folds into the Phase 4 `CallSession` refactor.

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
