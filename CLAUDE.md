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

Phases 0–5 complete and validated by real phone calls — registration, inbound signalling, prompt playback,
the DTMF screening gate, recording calls from your own number, and the inbound bridge (a screened-in
caller placed on a second SIP leg to your mobile, relayed both directions, and recorded to a stereo WAV)
all confirmed working end to end, including hangup from either side and an unanswered-ring `Missed`
outcome with the apology prompt. See PROGRESS.md for what was deliberately left out of the bridge (no
`CallSession`/`ActiveCallRegistry` refactor, no DTMF passthrough to the mobile leg) and for the caller-ID
bring-up fault (`403 Caller Origination Number is Invalid`) worth knowing about before touching
`BridgeToMobileAsync` again.

**Consent disclosure: decided.** The spoken notice on each path (`greeting.wav` Inbound,
`recording-notice.wav` Outbound) is the operator's chosen disclosure, confirmed audible on real calls; the
periodic recording tone (`Telephony:RecordingToneIntervalSeconds`) stays off. See the consent note below —
the structural gap it describes (the Outbound path's merged-in party never hears anything) is unchanged by
that decision, it is a property of the design.

A recordings browser (`/recordings`, `/recordings/{id}` with playback) is built on top of Phase 7's REST
API and already ships in the frontend.

**Workflow rule: one phase at a time.** The maintainer validates each telephony phase by phone before the
next begins.

## Solution layout

```
CallTree/
├── NuGet.config             # repo-local; maps all packages to nuget.org
├── dotnet-tools.json        # local dotnet-ef
├── deploy/                  # Dockerfile, compose (+ CasaOS variant), Proxmox notes, firewall allowlists
├── tools/                   # generate-prompts.ps1
├── CallTree.Core/           # backend (.NET 10, CallTree.Core.slnx)
│   ├── CallTree.Domain/         # aggregates, VOs, enums, domain events — no dependencies
│   ├── CallTree.Application/    # ports (ICallRepository), CallLifecycleService, call commands,
│   │                            #   StorageOptions (shared by Infrastructure and Telephony)
│   ├── CallTree.Infrastructure/ # EF Core + SQLite (CallTreeDbContext, migrations, CallRepository)
│   ├── CallTree.Telephony/      # SIPSorcery + NAudio; TelephonyBackgroundService owns the SIP UA
│   │                            #   Audio/ holds prompts, the G.711 decode and the recording pipeline
│   ├── CallTree.Api/            # ASP.NET Core host; DI wiring; /health; Scalar at /scalar (dev)
│   │                            #   Settings/ owns the writable config file the UI edits
│   └── CallTree.Tests/          # xUnit v3 — pure-logic tests only (state machine, PhoneNumber, WAV logic)
└── CallTree.UI/             # SvelteKit frontend (has its own CLAUDE.md/AGENTS.md — read them)
```

Dependency direction: `Api → {Telephony, Infrastructure} → Application → Domain`.

## Architecture rules

- The SIP UA lives **inside the API process** as a `BackgroundService`; one process serves HTTP and SIP/RTP.
- Domain `Call` records history and enforces transition legality
  (`Ringing → Screening → Dialing → InProgress → Completed/ScreenedOut/Missed/Failed`); live SIPSorcery
  objects never enter the Domain. Runtime per-call state belongs in Telephony. The inbound bridge
  (`TelephonyBackgroundService.BridgeToMobileAsync`/`RunBridgeAsync`) deliberately stayed a call-local
  method rather than introducing the `CallSession`/`ActiveCallRegistry` refactor TODO.md once described for
  Phase 4 — see PROGRESS.md's scope note. That refactor is still the planned shape once something actually
  needs to reason about more than one active call at a time; don't add it speculatively before then.
- **Telephony never opens DI scopes itself.** It describes what happened as a `CallCommand` and hands it to
  `ICallCommands`, which resolves `CallLifecycleService` in a fresh scope per command. `DbContext` is
  scoped and telephony callbacks are long-lived, so there is no ambient scope to join — but that plumbing
  belongs in one place, not at every call site.
- "Recording" is a fact (`Recording` entity), never a `CallStatus`.
- **`CallStatus.Screening` means "this caller is being gated"**, on either path — the inbound press-1
  spam gate, or the Outbound path's optional PIN. `Call.Answer` takes `requireScreening` from the caller
  rather than deriving it from `Source`, which is what lets a failed PIN land in `ScreenedOut` instead of
  looking like a call that simply finished. Don't push the gate back outside the state machine.
- **Reads and writes use separate ports.** `ICallRepository` loads whole aggregates to mutate and save;
  `ICallQueries` returns flat, untracked read models (`CallSummary`) for display. API responses are read
  models, never the aggregate — exposing `Call` would freeze the transition surface into the HTTP contract.
- Bridging (RTP payload relay) and recording (decoded PCM tap) are separate concerns — don't conflate.
- **`Storage` is bound in `AddApplication`**, and `StorageOptions` lives in Application, because
  Infrastructure resolves the database directory from it while Telephony resolves the recordings root and
  neither layer may reference the other. Don't "tidy" it back into Infrastructure and bind the same
  section into a second options type — one setting in two places is exactly the trap `Telephony:TraceSip`
  used to be in.
- Enums persist as strings; timestamps are `DateTimeOffset` passed explicitly into domain methods (testability).
- Trunk credentials/config live in options bound from configuration, never in the DB or the domain.
- **Configuration is three layers**: `appsettings.json` < `Storage:ConfigFile` (`data/config.json`, what
  the settings UI writes, `reloadOnChange`) < environment/user secrets. `Program.AddWritableConfiguration`
  inserts the file source; the API never writes anywhere else. Settings the SIP stack only reads at
  startup are enumerated in `TelephonySettingsWatcher`, which is the single source of truth for "did
  that change actually take effect" — the hosted service logs it and `/api/config` reports it.
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

**Running the two halves together:** `dotnet run --project CallTree.Api` (http profile, port 5146) and
`pnpm dev` in `CallTree.UI/` (port 5173). Vite proxies `/api` to 5146, so the browser sees one origin and
there is no CORS to configure. To work on the UI without touching the phone line, leave `Trunk:Host` unset
— `TelephonyBackgroundService` logs "telephony is idle" and never registers, so the deployed instance keeps
the trunk binding.

Frontend (`CallTree.UI/`): pnpm (`pnpm dev`, `pnpm build`, `pnpm check`, `pnpm lint`, `pnpm format`).
`pnpm check` is the type-check (`svelte-kit sync && svelte-check`) — run it, not just `lint`.
Prompts: `powershell -ExecutionPolicy Bypass -File tools/generate-prompts.ps1` (from the repo root).
Container: `docker build -f deploy/Dockerfile -t calltree .` (build context is the repo root).
Deployment targets: plain Compose (`deploy/docker-compose.yml`) and CasaOS (`deploy/casaos-compose.yml`).
**One image contains both halves** — the Dockerfile builds the SvelteKit UI to static files in a Node
stage and copies them into the API's `wwwroot`. One container, one port, one origin, no CORS anywhere.

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
- **Placing an outbound call needs its own `From`, explicitly.** `SIPUserAgent.Call(dst, username,
  password, mediaSession, ringTimeout)` — the simple string-destination overload — builds its own
  `SIPCallDescriptor` without setting `From`, which leaves the trunk to infer a caller ID from the SIP
  registration username. That is not a phone number, and Telnyx rejects it outright with
  `403 Caller Origination Number is Invalid` before the call ever rings - a clean, immediate failure, not
  a NAT-style silent one. Build the `SIPCallDescriptor` yourself and set `From` to `Telephony:DidNumber`
  (see `TelephonyBackgroundService.BridgeToMobileAsync`) for any outbound leg. This one was found by
  reflecting the installed package's actual field list rather than trusting the XML docs' summaries -
  another instance of "reading the source beats guessing" above.
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
  `TraceSip` is the *only* switch: `SipTraceLogLevel` (an `IConfigureOptions<LoggerFilterOptions>`
  registered by `AddTelephony`) raises the `CallTree.Telephony.SipTrace` category to Trace on its own.
  Don't reintroduce a `Logging:LogLevel` entry for that category — the two settings used to have to
  agree, and setting one without the other produced silence, which reads exactly like the packet never
  arriving. The trace handlers are attached unconditionally and guard on `IsEnabled(Trace)`, which is
  what lets tracing be turned on mid-call from the settings UI instead of via a restart that drops the
  registration and the call being investigated.
- Registrars echo the stored binding in their `200 OK` `Contact` — the fastest way to confirm what address
  the trunk will actually dial. `TelephonyStatus` captures it from `RegistrationSuccessful`, so
  `/api/telephony/status` and the `/status` page show it without going near the log.
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
  first tone or you will process one keypress as several. **For multi-digit entry latching is not enough**
  — `PinGate` debounces instead: a repeat of the *same* digit within 250 ms is the same keypress. The
  repeats are RFC 4733's three end-of-event retransmissions, so they arrive a packet interval apart while
  a human pressing a digit twice cannot come close. Without it a PIN of 1112 collapses to 12.
- **The RTP timestamp is the recording's clock, not a wall clock.** For PCMU it counts samples at 8 kHz,
  so it states where each packet belongs; writing packets back-to-back compresses every pause, and pacing
  off a wall clock drifts against the sender. `CallRecorder` writes from the timestamp and fills gaps with
  real silence. Two consequences that must not be "simplified away": gaps over 10 s are treated as a
  discontinuity and resynchronised (one bogus timestamp would otherwise allocate gigabytes of silence),
  and timestamp comparisons are signed so the 32-bit wrap doesn't reorder anything. Phase 5's stereo
  recording is the case that genuinely needs a shared wall clock — two legs, two unrelated RTP clocks.
- **Filter the RTP payload type before decoding.** Payload 101 (RFC 4733 telephone-event) shares the
  session with PCMU; decoding it as audio writes a burst of noise into the recording on every keypress.
- **G.711 code 0x7F decodes to 0 here, not −1.** NAudio's `MuLawDecoder` table says −1 for that
  "negative zero" code; the ITU reference expansion computes 0, and so does `G711`. Inaudible either way,
  but `G711Tests` asserts the whole 256-code table against NAudio *except* this one so the disagreement
  reads as intentional rather than as a bug found later.
- **`FinalizeRecording` must keep touching only the `Recording` entity.** It runs concurrently with the
  hangup handler's `EndCall` in a separate DI scope; because the `Call` row has no changed columns, EF
  emits no `UPDATE` for it and cannot overwrite the terminal status with a stale one. Adding a `Call`
  mutation to that path reintroduces the race silently.
- **Never write `Telephony:OutboundPin` unless one was supplied** — same rule and same reasoning as the
  trunk password. The settings UI also needs the switch beside the field: "blank means unchanged" leaves
  no way to express "remove the gate", and the PUT response's `outboundPinSet` can briefly describe the
  pre-save configuration (the file it just wrote reloads asynchronously), so the UI sets the switch from
  what it *sent*, never from the response.
- Keep log message strings ASCII. Em-dashes render as mojibake in consoles that aren't UTF-8.
- **SQLite cannot ORDER BY a `DateTimeOffset`**, and every timestamp in this project is one. EF throws
  outright on ordering ("SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY
  clauses") because rows written with different UTC offsets would not sort by instant; range filters have
  the same flaw but fail *silently*. `UtcDateTimeOffsetConverter` (applied to every `DateTimeOffset` via
  `ConfigureConventions`) normalizes to UTC and stores text in EF's own format, so ordering and
  comparison work and existing rows are byte-identical — no migration. Don't remove it to "simplify" the
  mapping, and don't add a second timestamp column to sort by instead.
- SQLite won't create missing parent directories — `Program.EnsureDatabase` handles it; keep that when
  touching startup.
- Runtime data (`CallTree.Api/data/` — db + recordings + `config.json`) is gitignored; recordings root and
  config file path are themselves config (`Storage:RecordingsRoot`, `Storage:ConfigFile`).
- **Never write `Trunk:Password` into `config.json` unless one was actually supplied.** A key present with
  an empty value overrides the same key from user secrets or the environment, so a save of any unrelated
  field would blank a working credential. `SettingsDocument.Apply` treats a null password as "unchanged";
  the UI sends null when the box is empty and the API never returns the current value.
- **Never let a locally staged `wwwroot` reach the Docker build.** `.dockerignore` excludes
  `CallTree.Core/CallTree.Api/wwwroot/` for a reason: if it arrives in the build stage, `dotnet publish`
  precompresses it to `.br`/`.gz`, and those survive the later `COPY` of the freshly built UI — `COPY`
  overwrites files but never removes them. The container then serves a stale compressed copy to any
  browser sending `Accept-Encoding`, while an uncompressed `curl` sees the correct page. Observed once
  already; that is why the exclusion is there.
- **`wwwroot` must exist at build time or the app will not start in Development.** It holds the built UI,
  which only the container build produces, so it is gitignored and absent from a clean clone — but the
  build still emits a static web assets manifest naming it, and in Development the host reads that
  manifest *inside* `WebApplication.CreateBuilder`. A missing directory is an unhandled
  `DirectoryNotFoundException` before a line of `Program.Main` runs, so no runtime guard can help. The
  `EnsureWebRoot` target in `CallTree.Api.csproj` creates it; don't remove it.
- **The unprefixed environment source is the one to insert the config file before.** The host adds
  `ASPNETCORE_` and `DOTNET_` `EnvironmentVariablesConfigurationSource`s *before* the appsettings files,
  so matching on type alone puts `config.json` underneath `appsettings.json`. The failure is quiet and
  partial — keys absent from appsettings appear to save correctly while keys present there are ignored,
  which looks like a flaky writer rather than an ordering bug. Match on `{ Prefix: null or "" }`.
- **The SPA fallback must not swallow `/api`.** `MapFallbackToFile` catches everything, so an unknown API
  path would answer `200 text/html` and callers would fail later at `JSON.parse` instead of seeing a 404.
  `app.Map("/api/{**path}", ...)` returns a proper 404 ahead of the fallback; controllers are more
  specific and still match first.
- **Bind-mounting an empty directory over `/app/prompts` hides the prompts baked into the image** and the
  IVR answers calls in silence — `PromptLibrary` logs a warning and carries on. Signalling still succeeds,
  so it reads as working until nobody hears the press-1 instruction. Both compose files ship with that
  mount present-but-commented for this reason; don't "tidy" it back on.
- **The `aspnet:10.0` base image is Debian slim and has no HTTP client** — no curl, no wget. The Dockerfile
  installs curl solely so the compose healthcheck has something to run; a healthcheck whose binary is
  missing doesn't error, it just leaves the container permanently `unhealthy`.
- **Only one instance may hold the trunk registration.** The provider keeps the most recent binding, so a
  second instance on the same credential silently steals inbound calls. Stop the old one before deploying.
- **Legal: recording consent varies by jurisdiction**, and several require *all* parties to consent rather
  than one. The disclosure approach was an open decision for the operator; it is now decided (spoken
  notice on each path, tone off — see Status) but the rule stands regardless of what gets decided: never
  silently drop or "simplify away" disclosure prompts or tones once they exist, and never assume one
  jurisdiction's rules apply universally. Specific to the Outbound path: `recording-notice.wav` reaches
  **only the operator**. The third party is merged in by the handset and CallTree is never told, so no
  prompt can ever reach them — `Telephony:RecordingToneIntervalSeconds` is the only disclosure available
  to that party, and it has been left off by operator decision, not just its default. Say so plainly
  whenever this path is discussed; it is a property of the design, not a bug to be quietly fixed, and an
  operator who assumes the notice is heard by everyone is exposed.

## Testing philosophy

Unit-test what's pure logic (domain transitions, normalization, WAV/timing math). Telephony behavior is
validated per-phase by real phone calls; a scratch SIPSorcery console caller works for local E2E and can
send real RFC 4733 DTMF.
