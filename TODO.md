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

- [x] **Reject INVITEs not addressed to our DID** (`Telephony:DidNumber`, 404 before any Call row is
      created). `217.160.58.53` is actively sweeping dial-plan prefixes at this host — `011`, `9011`, `00`
      and bare, all aimed at `390237902590` (Milan, Italy) — which is textbook toll-fraud enumeration.
      Before the filter, CallTree answered every probe and played 12 s of greeting, which both confirms a
      live PBX to the attacker and fills the database with junk rows.
- [ ] **Restrict the router port-forward to Telnyx's addresses.** This is the right layer — it drops the
      packets before they reach the host at all. The probes do *not* arrive via Telnyx; they are sent
      straight to `47.204.201.45:5060` from the open internet, so no Telnyx account setting can stop them.
      Per <https://sip.telnyx.com/>, the US region signals from exactly **192.76.120.10** and
      **64.16.250.10** (both confirmed in our logs as the source of every real call). Media/RTP arrives from
      `36.255.198.128/25`, `50.114.136.128/25`, `50.114.144.0/21`, `64.16.226.0/24`–`64.16.230.0/24`,
      `64.16.248.0/24`, `64.16.249.0/24`, `103.115.244.128/25`, `103.115.247.128/27`, `185.246.41.128/25`,
      `185.246.42.128/28`. Scope 5060/udp to the two signalling IPs and 10000–10100/udp to the media ranges.
- [ ] Optionally mirror that allowlist in `HandleIncomingCallAsync` as defence in depth, in case the
      forward is ever widened or the host is moved.
- [ ] Consider rate-limiting or temporarily blocklisting source addresses that get repeatedly rejected —
      the probes arrive in bursts and each one still costs a transaction.

## Phase 2 — Media out + DTMF in ✅ (code-complete, pending phone validation)

- [x] Play a WAV prompt on answer (`AudioExtrasSource.SendAudioFromStream`, which takes *raw* PCM —
      `WavAudio` unwraps the RIFF container at load).
- [x] DTMF detection via RFC 4733 (`OnDtmfTone`, first tone latched to ignore per-keypress repeats).
- [x] Press-1 IVR gate with barge-in; passed → `Completed`, wrong key or timeout → `ScreenedOut`.
- [x] Prompt WAVs in `CallTree.Api/prompts/` (content directory, not embedded — the wording has to be
      changeable without a rebuild). Regenerate with `tools/generate-prompts.ps1`.
- [x] Codec restricted to PCMU.
- [ ] **Scott: validate by phone** — call the DID, hear the prompt, press 1; then call again and press
      nothing. Check `Calls.Status` is `Completed` vs `ScreenedOut`.
- [ ] **Decide the consent disclosure wording** and re-run `tools/generate-prompts.ps1`. The current
      greeting ("This call will be recorded") is a placeholder chosen to err toward disclosing; Florida is
      all-party consent and the real wording/placement is still an open decision.
- [ ] Replace the TTS placeholders with real recordings if the robotic voice grates.

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
