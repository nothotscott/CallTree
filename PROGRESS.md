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
- Unit tests: 34 passing.
