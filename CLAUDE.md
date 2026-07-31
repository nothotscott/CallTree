# CallTree

Self-hosted call recorder / bridge PBX. A from-scratch SIP user agent (SIPSorcery — no Asterisk/PJSIP)
embedded in an ASP.NET Core backend, owning one DID via a SIP trunk. Also a deliberate SIP/RTP learning
project: prefer understandable from-scratch implementations over drop-in PBX features.

User-facing overview: [README.md](README.md). Progress: [PROGRESS.md](PROGRESS.md) / [TODO.md](TODO.md).

## What it does

One DID; all calls to/from it pass through CallTree. Calls are classified by caller ID:

- **CallSource.Outbound** — caller ID matches `Telephony:MyCellNumber` (the operator's own mobile).
  Auto-answer and record immediately; the operator then uses the phone's native 3-way merge to add the real
  party, so a single mono leg captures both sides.
- **CallSource.Inbound** — anyone else. IVR spam gate ("press 1"), then bridge to the mobile and record
  (stereo, one leg per channel).

Naming note: `Outbound`/`Inbound` are **business** classifications; both start as inbound SIP INVITEs.
`LegDirection` is the SIP-level direction.

## Status

Phases 0–2 complete and validated by real phone calls. Next is Phase 3 (Outbound-source path + mono
recording), which is blocked on the consent-disclosure decision — see the legal note below.

**Workflow rule: one phase at a time.** The maintainer validates each phase by phone before the next begins.

## Solution layout

```
CallTree/
├── NuGet.config             # repo-local; maps all packages to nuget.org
├── dotnet-tools.json        # local dotnet-ef
├── deploy/                  # Dockerfile, compose, Proxmox notes, firewall allowlists
├── tools/                   # generate-prompts.ps1
├── CallTree.Core/           # backend (.NET 10, CallTree.Core.slnx)
│   ├── CallTree.Domain/         # aggregates, VOs, enums, domain events — no dependencies
│   ├── CallTree.Application/    # ports (ICallRepository), CallLifecycleService, call commands
│   ├── CallTree.Infrastructure/ # EF Core + SQLite (CallTreeDbContext, migrations, CallRepository)
│   ├── CallTree.Telephony/      # SIPSorcery + NAudio; TelephonyBackgroundService owns the SIP UA
│   ├── CallTree.Api/            # ASP.NET Core host; DI wiring; /health; Scalar at /scalar (dev)
│   └── CallTree.Tests/          # xUnit v3 — pure-logic tests only (state machine, PhoneNumber, WAV logic)
└── CallTree.UI/             # Next.js frontend (has its own CLAUDE.md/AGENTS.md — read them)
```

Dependency direction: `Api → {Telephony, Infrastructure} → Application → Domain`.

## Architecture rules

- The SIP UA lives **inside the API process** as a `BackgroundService`; one process serves HTTP and SIP/RTP.
- Domain `Call` records history and enforces transition legality
  (`Ringing → Screening → Dialing → InProgress → Completed/ScreenedOut/Missed/Failed`); live SIPSorcery
  objects never enter the Domain. Runtime per-call state belongs in Telephony (a `CallSession` class is the
  planned shape once Phase 4 needs it).
- **Telephony never opens DI scopes itself.** It describes what happened as a `CallCommand` and hands it to
  `ICallCommands`, which resolves `CallLifecycleService` in a fresh scope per command. `DbContext` is
  scoped and telephony callbacks are long-lived, so there is no ambient scope to join — but that plumbing
  belongs in one place, not at every call site.
- "Recording" is a fact (`Recording` entity), never a `CallStatus`.
- Bridging (RTP payload relay) and recording (decoded PCM tap) are separate concerns — don't conflate.
- Enums persist as strings; timestamps are `DateTimeOffset` passed explicitly into domain methods (testability).
- Trunk credentials/config live in options bound from configuration, never in the DB or the domain.
- **Only PCMU is offered.** See the codec table in README.md before changing this — Phase 3's decode and
  Phase 4's payload relay both assume it.

## Commands

Run from `CallTree.Core/` unless noted:

```bash
dotnet build CallTree.Core.slnx
dotnet test CallTree.Tests
dotnet run --project CallTree.Api                  # migrates SQLite automatically on boot
dotnet ef migrations add <Name> --project CallTree.Infrastructure --startup-project CallTree.Api --output-dir Persistence/Migrations
```

Secrets (Development) go in user secrets, not appsettings — see README.md for the full list.
Frontend (`CallTree.UI/`): pnpm (`pnpm dev`, `pnpm build`, `pnpm lint`).
Prompts: `powershell -ExecutionPolicy Bypass -File tools/generate-prompts.ps1` (from the repo root).
Container: `docker build -f deploy/Dockerfile -t calltree .` (build context is the repo root).

## Gotchas (hard-won — don't rediscover these)

- **NuGet**: a restrictive user-level NuGet.Config that allowlists packages per-source will break restore
  with NU1100/"incompatible". The repo-local `NuGet.config` (pattern `*` → nuget.org) overrides it.
- **Microsoft.OpenApi must stay 2.x** (currently 2.10.0). 3.x breaks the `Microsoft.AspNetCore.OpenApi`
  source generator (CS0200 in generated code). Already tried twice.
- **SIPSorcery is v10** — APIs differ from older docs/samples. Verify signatures against the actual package
  (`~/.nuget/packages/sipsorcery/10.0.12/lib/net10.0/SIPSorcery.xml`, or a local clone of the source) before
  coding. Reading the source beats reflection dumps and beats guessing.
- **`sendUsernameInContactHeader: true` is mandatory on `SIPRegistrationUserAgent`.** SIPSorcery defaults it
  to *false*, which sends `Contact: <sip:host:port>` with no user part. A registrar will still answer
  `200 OK` (digest auth is valid), so registration looks perfectly healthy from our side — but the provider
  cannot tie the binding to a connection, its portal reads `Unregistered` with every field `null`, and
  inbound calls have no destination, so the caller hears a busy tone. A `sip_username: null` in a provider's
  registration status is the tell: it is reporting the *Contact* user, which we weren't sending.
- **NAT: `Telephony:PublicHost` is mandatory when running behind a router.** SIPSorcery substitutes the
  *local* address into the REGISTER `Contact` (see `SIPTransport.ContactHost`), so without it the trunk is
  told to reach us at a LAN address and inbound INVITEs never arrive — the caller hears a busy/failure tone
  and the process logs nothing at all.
- **`SIPUserAgent.Answer(publicIpAddress:)` does not do what its name suggests.** It is only a *fallback*:
  `RTPSession.GetSdpConnectionAddress` prefers the local address that routes to the offer's connection
  address and uses the supplied one only when the offer carries none. A trunk always sends one, so the
  argument never wins and the SDP goes out advertising the LAN address — signalling succeeds, then the trunk
  streams RTP into the void. `NatAwareVoIPMediaSession` overrides `CreateAnswer`/`CreateOffer` and rewrites
  the address afterwards; keep using it for every media session, including Phase 4's outbound legs.
- **Set `Telephony:TraceSip` to see whole SIP messages** on the wire (`SIPRequestInTraceEvent` and friends).
  It is fired immediately after parse, before any dispatch or filtering — so if a call fails and *no* trace
  line appears, the packet never reached the process, which rules out the whole application at once.
- Registrars echo the stored binding in their `200 OK` `Contact` — the fastest way to confirm what address
  the trunk will actually dial.
- Useful reachability check that needs no second phone: send a SIP `OPTIONS` to the *public* IP from the LAN.
  Many routers hairpin it back through the port-forward, so a `200 Ok` proves the forward + host firewall
  path. Note this enters on the LAN interface, so it does not exercise the WAN firewall policy.
- Windows firewall rules are per-executable and per-profile: the rule must target `CallTree.Api.exe`, and a
  Wi-Fi connection is often classified **Public**, so the rule has to cover that profile.
- **The public SIP port is under continuous attack.** `Telephony:DidNumber` makes CallTree reject any INVITE
  whose request URI isn't our DID (404, before a Call row exists). Scanners sweep dial-plan prefixes
  (`011…`, `9011…`, `00…`) hunting for a PBX that will place an international call for them — one 40-minute
  window logged 276 such probes from four sources. Don't remove this, and don't widen it: Phase 4's outbound
  leg is what turns a probe into a phone bill. Router-level allowlists are in `deploy/firewall/`.
- **Close the audio source before hanging up.** `AudioExtrasSource` runs a 20 ms timer; if the RTP session
  closes first it logs "SendRtpRaw was called for a audio packet on a closed RTP session". `CloseAudio()`
  disposes the timer and the warning goes away. Cosmetic, but noise makes real problems harder to spot.
- **`AudioExtrasSource.SendAudioFromStream` takes raw 16-bit PCM, not a WAV file** — pass a `.wav` straight
  through and the 44-byte RIFF header is played as a burst of noise. `WavAudio.ReadPcm` unwraps it; prompts
  must be 8 or 16 kHz, 16-bit, mono.
- Restricting the audio formats to PCMU does **not** disable DTMF: `MediaStream` adds the RFC 4733
  telephone-event payload (101) separately from the negotiated codec list.
- A single DTMF keypress raises `OnDtmfTone` more than once (the tone spans several RTP packets). Latch the
  first tone or you will process one keypress as several.
- Keep log message strings ASCII. Em-dashes render as mojibake in consoles that aren't UTF-8.
- SQLite won't create missing parent directories — `Program.EnsureDatabase` handles it; keep that when
  touching startup.
- Runtime data (`CallTree.Api/data/` — db + recordings) is gitignored; recordings root is config
  (`Storage:RecordingsRoot`).
- **Legal: recording consent varies by jurisdiction**, and several require *all* parties to consent rather
  than one. The disclosure approach is an open decision for the operator — never silently drop or
  "simplify away" disclosure prompts or tones once they exist, and never assume one jurisdiction's rules.

## Testing philosophy

Unit-test what's pure logic (domain transitions, normalization, WAV/timing math). Telephony behavior is
validated per-phase by real phone calls; a scratch SIPSorcery console caller works for local E2E and can
send real RFC 4733 DTMF.
