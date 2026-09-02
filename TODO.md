# CallTree — Remaining Work

Completed work is in [PROGRESS.md](PROGRESS.md); the project overview is in [README.md](README.md).

This is a flat list, not phase-ordered. The build phases (registration, screening, recording, bridging,
SMS, the API, the frontend) are all done and validated by real phone calls and texts — see PROGRESS.md for
that history. What's left is grouped by topic instead.

## Security

- [ ] Optionally mirror the router level allowlist in [`deploy/firewall/`](deploy/firewall/) to `HandleIncomingCallAsync` as defence in depth, for whenever the
      forward gets widened or the host moves.
- [ ] Decide the API's auth posture: LAN-only, or authenticated remote access. There is currently no auth
      and no CORS policy. `GET /api/config` discloses the DID, mobile number, public host and trunk
      username, and `PUT` can repoint the trunk or clear the DID filter that keeps toll-fraud probes out —
      this is the strongest argument for auth, and it should land before any remote exposure. It's currently recommended to use Cloudflare Zero Trust for public access.

## Trunk and calls

- [ ] Refactor per-call handling out of `TelephonyBackgroundService` into a `CallSession` runtime class
      plus an `ActiveCallRegistry`. Deliberately deferred so far — see PROGRESS.md's scope note. Worth
      doing once something actually needs to reason about more than one active call at a time.
- [ ] Replace the single long-lived listener `SIPUserAgent` with a per-call agent. One agent holds one
      dialogue, so a second concurrent *inbound* call — or a dialogue left behind by an abnormal teardown —
      is silently dropped with no log line. Pre-existing since Phase 1; the bridge's outbound leg already
      uses its own separate per-call agent, so it neither worsens nor fixes this.
- [ ] Re-check the trunk account's concurrency and per-call duration caps. A 10-minute ceiling would
      truncate recordings; some providers apply one on lower tiers.
- [ ] No audible feedback on an unanswered outbound proxy dial (`*{NUMBER}#`) beyond a log line. A
      purpose-built prompt is a reasonable small follow-up, not required.
- [ ] Registration resilience: backoff tuning and network-blip recovery. Registration state is tracked in
      `TelephonyStatus` and exposed at `GET /api/telephony/status`; surfacing it on `/health` too would let
      a container orchestrator act on it, which the status page alone can't.
- [ ] Wire up `Trunk:AuthUsername` — currently warned about but not honoured. Needs the long
      `SIPRegistrationUserAgent` overload (AOR, realm, contact URI, custom headers). Only needed for
      providers that split the SIP and auth usernames.
- [ ] Startup sweep to repair unfinalized WAVs — recompute RIFF sizes from the file length and close the
      `FinalizedAt` gap left by a crash mid-write. Less urgent than it was: `CallRecorder` now re-patches
      the header every five seconds, so a killed process leaves a file that plays up to the last flush. What
      the sweep still fixes is the `Recording` row with a null `FinalizedAt`, and the last few seconds.
- [ ] `Telephony:PublicHost` is a static value. Residential IPs change — switch to a DDNS hostname, or
      discover the public address at startup via SIPSorcery's `STUNClient` and re-check it periodically.

## Messaging (SMS)

- [ ] 10DLC registration, to unblock outbound SMS. Brand registration plus a campaign registration through
      the provider, with a one-off fee and a monthly campaign charge — a sole proprietor can register
      without an EIN at a lower throughput tier. No code work needed: the send paths are written and
      tested, so setting `Messaging:ApiKey` once the number is approved is all that's left.
- [ ] Forward MMS media rather than only counting it. Would mean re-sending the provider's media URLs at
      MMS rates, with its own failure modes — deliberately out of scope for the first cut.
- [ ] Consider a sticky reply target, so a reply to a forwarded message doesn't need the number retyped.
      Currently rejected on purpose: it's invisible state deciding who a message goes to, and getting it
      wrong sends a text to the wrong person. Revisit only if the retyping becomes genuinely annoying.
- [ ] Message detail view / conversation threading in the UI. The list shows the received body and what
      was relayed; there's no per-number thread.

## API

- [ ] Remaining filters on `GET /api/calls`: date range, number, duration.
- [ ] Call detail endpoint (`GET /api/calls/{id}`) exposing the legs, which the list summary flattens away.

## Frontend

- [ ] Restart the service from the UI, so a trunk change doesn't need shell access. Needs care: the
      process only supervises itself under Compose's `restart: unless-stopped`, and there is no auth yet.
- [ ] Live call state on the status page — needs the `CallSession`/`ActiveCallRegistry` work above first,
      since there's no runtime call registry yet to answer "is a call up right now".
- [ ] Call detail view, once the call detail endpoint above exists.

## Retention

- [ ] Decide a retention policy for recordings and message bodies: keep forever, delete after N days, or
      cap by size? Optionally transcode older recordings to Opus or MP3 — stereo PCM WAV runs about
      1.9 MB per minute. Message bodies are the most sensitive thing in the database after recordings, and
      the same open question applies to them.

## Future

- [ ] Text-to-speech prompts generated on the fly instead of static
      `.wav` files under `CallTree.Api/prompts/`. Would let prompt wording change without regenerating and
      redeploying audio files — see [Prompts](README.md#prompts) for how that works today.
- [ ] Responses API / Ollama integration for LLMs to handle spam prevention.
- [ ] A softphone in the web UI: a dialer with live audio, so a call can be placed and heard from the
      browser instead of only from a phone on the trunk. `PlaceOutboundLegAsync` (see PROGRESS.md) was
      already extracted as trigger-agnostic dial primitive with this in mind.
- [ ] SMS sending from the web UI, once 10DLC registration (above) clears — compose and send a text from
      `/messages` instead of only via the `{RECIPIENT-NUMBER} Body of text` command from the mobile.
