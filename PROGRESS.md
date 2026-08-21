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

## Phase 3 — Recording calls from my own number ⚠️ (built and unit-tested, not yet validated by phone)

Calls classified `Outbound` are answered, optionally gated by a PIN, and recorded to a mono 16-bit WAV.
Only received audio is captured, which is the whole design: the operator adds the other party with the
handset's own three-way merge, so by the time it matters this single leg already carries both voices.

**Not yet validated over the trunk.** The unit tests feed `CallRecorder` packets directly; the seam that
has never run is the one in between — SIPSorcery's `OnRtpPacketReceived` delivering real PCMU. Until a
real call has been placed and the file played back, this phase is not finished.

### The RTP timestamp is the clock

The original plan called for a paced 20 ms write clock. There isn't one, and the reason is worth keeping:
for PCMU the RTP timestamp *counts samples at 8 kHz*, so it is already the sender's sample clock and says
exactly where every packet belongs. Writing packets back to back as they arrive would silently compress
every pause in the conversation; pacing off a wall clock instead would drift whenever the two clocks
disagree, and would still need reconciling against the timestamps. Writing from the timestamp directly
means gaps are *measurable*, and they are filled with real silence so the recording stays in step with
what was said.

Two guards fall out of that:

- A timestamp jump larger than ten seconds is treated as a discontinuity — a clock reset, a stray packet
  from an old stream — and resynchronised rather than filled. Without the cap, one bogus timestamp asks
  for however many gigabytes of silence it implies.
- A packet that arrives after its place in the file has been written is dropped and counted. A WAV cannot
  be inserted into; dropping costs 20 ms, rewinding would corrupt everything after it.

Timestamps are compared as signed differences throughout, so the 32-bit wrap (about six days of
continuous audio) reorders nothing.

Phase 5 cannot reuse this. Two legs have two unrelated RTP clocks with nothing to align them to, which is
the case that genuinely needs the shared wall clock this phase did without.

### The rest of the pipeline

- **`RtpJitterBuffer` is a reordering buffer, not a playout buffer.** A softphone needs a playout clock
  because it has a speaker to feed; a recorder has no deadline. So the release rule is depth-based: a
  frame is handed on once a frame at least `Telephony:JitterBufferMilliseconds` newer has arrived, which
  is the point after which nothing earlier can still be expected.
- **`G711` is written out rather than taken from NAudio**, since understanding the wire format is half
  the point of the project, and the test asserts it against NAudio's decoder for all 256 codes — the
  whole input domain, so nothing is left to sample. That turned up a genuine disagreement: NAudio's table
  decodes 0x7F, the negative-zero code, to −1 where the ITU reference expansion computes 0. One LSB on a
  code that means zero, inaudible either way, but a table that cannot represent silence as silence is the
  wrong one to inherit. The test pins the difference down so it is not rediscovered as a bug.
- **Non-PCMU payloads are ignored at the door.** Payload 101 shares the RTP session; decoding the RFC
  4733 telephone-event stream as audio would write a burst of noise into the recording on every keypress.
- **The header is re-patched every five seconds.** A process killed mid-call then leaves a file that
  still plays up to the last flush, instead of one that every tool reads as empty. Asserted by a test
  that reads the file while the recorder still holds it open.
- **Finalizing cannot race the hangup.** `FinalizeRecording` touches only the `Recording` entity, so EF
  emits no `UPDATE` for the `Call` row and cannot overwrite the terminal status that the hangup handler
  is writing concurrently in its own scope.

### The PIN, and what Screening now means

`Telephony:OutboundPin` gates the recording path, blank by default. Caller ID is trivially forged, and
this is the path that answers automatically and records without asking; today that costs disk, but once
Phase 4 can place an outbound leg the same forgery costs money.

`Call.Answer` now takes `requireScreening` rather than deriving it from `Source`, so `Screening` means
"this caller is being gated" on either path. That is what lets a failed PIN land in `ScreenedOut` — a
spoofing attempt is distinguishable in the call log from a call that simply finished, which it would not
be if the gate lived outside the state machine. `Call.PassScreening` covers the Outbound case where the
caller clears the gate and there is nothing to dial.

Multi-digit entry needs a debounce the single-digit gate did not: one keypress raises `OnDtmfTone`
several times because RFC 4733 retransmits the end-of-event packet three times. Those repeats arrive
within a packet interval or two, so a repeat of the same digit inside 250 ms counts as one keypress —
which is what stops a PIN like 1112 collapsing to 12.

### Disclosure, and the gap that cannot be closed in software

`recording-notice.wav` plays before the recorder opens, so the disclosure never lands inside the file it
is disclosing. But it reaches **only the operator**: the third party is merged in by the handset, and
CallTree is never told it happened. No prompt can reach them.

The only mechanical disclosure available on this path is a periodic tone, added as
`Telephony:RecordingToneIntervalSeconds` (a generated 1400 Hz tone, sent not received, so it does not
appear in the recording). It is **off by default**, because the wording, the interval and whether
one-party consent is even sufficient are legal decisions for the operator — see TODO.md. What is built
is the mechanism, not the answer.

### Elsewhere

- `StorageOptions` moved from Infrastructure to Application: Infrastructure owns the database and
  Telephony writes the recordings, neither may reference the other, and binding one section into two
  option types would put the same setting in two places.
- `RecordingStore` groups files by month and names them `<utc-timestamp>-<call-id>.wav`, storing a
  forward-slashed relative path so a database written on Windows still resolves in the container. It also
  owns the traversal check for Phase 7's streaming endpoint, next to the path construction rather than
  wherever gets there first.
- `PromptPlayer` was extracted from `ScreeningGate` before `PinGate` copied it a second time.
- The settings page grew a Recording section. The PIN is write-only like the trunk password, with a
  switch beside it, because "blank means unchanged" leaves no way to express "turn the gate off".

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

## Call log — API and UI (2026-08-05, out of phase order)

The first vertical slice through the whole stack: a paginated call log, read-only.

- **`GET /api/calls`** returns `PagedResult<CallSummary>`, most recent first, filterable by `source` and
  `status`. Paging clamps rather than rejects — page 0 means page 1, and page size is capped at 200 so one
  request cannot read the table into memory — while an unknown enum name is still a 400 from model binding.
- **Read and write paths are separate ports.** `ICallQueries` returns untracked `CallSummary` read models;
  `ICallRepository` keeps loading whole aggregates for mutation. The API never serializes the `Call`
  aggregate, so the domain's transition surface stays out of the HTTP contract.
- **Enums serialize as names.** `JsonStringEnumConverter` is registered globally: numeric enums would
  silently change meaning the first time a member is inserted mid-enum.
- **UI** at `/calls` — table with source/status filters and a pager. Filter and page state live in the URL,
  so views are linkable and the back button behaves; the filter form is a plain GET form and the pager is
  ordinary links, so it works before hydration. The load function returns fetch failures instead of
  throwing, because "the backend isn't running" is the overwhelmingly likely cause in development and an
  error page cannot say so.
- **Same-origin in development.** Vite proxies `/api` to the API on 5146, so there is no CORS policy in the
  backend and none is needed. Whether production is same-origin is still open — see TODO.md.

### Shipping as one container

The UI is `@sveltejs/adapter-static` in SPA mode, built in a Node stage of `deploy/Dockerfile` and copied
into the API's `wwwroot`. ASP.NET serves it and falls back to `index.html` for unmatched paths, so deep
links work on a cold load. One container, one port, one origin — no CORS policy exists anywhere in the
codebase, and none is needed. The cost is no server-side rendering, which for a LAN-only call log behind a
fetch is invisible.

Two things that had to be got right, both verified by running the built image:

- **Unknown `/api/*` paths must 404**, not fall through to the SPA shell. Left alone, `MapFallbackToFile`
  answered `/api/nope` with `200 text/html`, so a mistyped endpoint looked like success and failed later
  at `JSON.parse`.
- **A locally staged `wwwroot` must not reach the build.** It did on the first attempt: `dotnet publish`
  precompressed it to `.br`/`.gz`, and those survived the later `COPY` of the real UI, because `COPY`
  overwrites files without removing them. The image would have served a stale compressed page to any
  browser sending `Accept-Encoding` while `curl` saw the right one. Now excluded in `.dockerignore`.

Verified end to end in the container: `/`, `/calls`, `/api/calls`, `/health`, a 404 on an unknown API path,
`curl` present for the Compose healthcheck, and telephony correctly idle without trunk configuration.

## Configuration from the UI (2026-08-07)

Trunk and telephony settings are now editable at `/settings` instead of only through environment
variables, which is what makes a container's data volume the whole instance rather than half of it.

- **Three layers**: `appsettings.json` (what ships) < `Storage:ConfigFile` (`/data/config.json`, written
  by the UI, `reloadOnChange`) < environment and user secrets (what this host demands). The file is
  inserted as a configuration source in `Program.AddWritableConfiguration`, and it goes into the volume
  beside the database and the recordings, so moving the volume moves the trunk with it.
- **Ordering is the whole trick, and getting it wrong fails quietly.** The source has to be inserted
  before the *unprefixed* environment source: the host adds `ASPNETCORE_` and `DOTNET_` sources of the
  same type ahead of the appsettings files, so matching on type alone puts the config file *underneath*
  `appsettings.json`. The first run did exactly that, and the symptom was partial — `DidNumber` and
  `PublicHost` (absent from appsettings) saved correctly while `SipListenPort`, `TraceSip` and the whole
  `Trunk` section were silently ignored. It reads as a flaky writer, not an ordering bug.
- **The password is write-only.** It is never returned; a `PUT` that omits it leaves the configured value
  alone. Writing an empty string instead would override the same key coming from user secrets or the
  environment, so saving any unrelated field would blank a working credential.
- **Honest reporting beats silent acceptance.** Sockets are bound and the trunk registered once, at
  startup, and rebinding mid-flight would drop calls and hand the provider's binding to nobody. So those
  settings are deliberately restart-only, and `TelephonySettingsWatcher` — one list, used by both the
  hosted service's log line and the API response — says which saved keys the running stack has not
  picked up. The UI also marks fields an environment variable is overriding, because an edit there
  saves fine and changes nothing.
- **Live settings** are the numbers, the screening digit and timeout, and SIP tracing. The hosted service
  moved from `IOptions` to `IOptionsMonitor` and re-reads them per call.

`.env.example` and `casaos-compose.yml` now ship with the trunk and telephony blocks commented out. For
CasaOS in particular that removes the step where credentials are pasted into a Compose file that ends up
root-readable at `/var/lib/casaos/apps/calltree/`.

### Telephony status page

`GET /api/telephony/status` and a page at `/status` that polls it every 5 seconds. Registration state
used to exist only as four log lines, so "is the trunk up?" meant finding and reading the log.

`TelephonyStatus` holds an immutable snapshot that the registration events replace wholesale — the
events arrive on SIPSorcery's threads while the API reads on request threads, and a page showing a
registered state next to a failure message would be worse than no page.

What it shows is chosen from the bring-up faults in Phase 1, all four of which presented identically as
a caller hearing a busy tone with nothing whatsoever in the log:

- **The Contact the registrar echoed back in its 200 OK** — the address it will actually dial. This is
  the fault that hides best: registration looks perfectly healthy locally while the binding points at a
  LAN address or has no user part at all.
- What we advertise in `Contact` and in SDP, separately, because they fail differently — the first
  stops calls arriving, the second connects the call and then sends the audio nowhere.
- The bound SIP endpoints, the RTP range, whether the DID filter is active, and which prompts loaded.
- Settings saved but waiting on a restart, from the same `TelephonySettingsWatcher` the settings page
  uses.

The last known binding is deliberately kept across a later failure: "registered at 09:12 as this
contact, failing since" reads better than a blank field.

### Telephony:TraceSip is now the only SIP-trace switch

It used to take two settings that had to agree — `Telephony:TraceSip` to attach the handlers and a
`Logging:LogLevel` entry to let Trace through. Setting one without the other produced no output at all,
which looks exactly like a packet that never reached the process: the one conclusion SIP tracing exists
to rule out.

`SipTraceLogLevel`, an `IConfigureOptions<LoggerFilterOptions>` registered after the rules built from the
`Logging` section, raises that category to Trace whenever `TraceSip` is on. Registering it as configured
options rather than a one-off `AddFilter` is what makes it reload: filter options are recomputed on the
`Logging` section's change token and the logger factory refreshes its filters, so the setting can be
flipped from the UI during a misbehaving call rather than needing a restart that drops the registration
and the call being investigated. The trace handlers are attached unconditionally and check
`IsEnabled(Trace)` first, which is what allows that.

### wwwroot has to exist before the builder does

A clean clone would not start in Development: `wwwroot` holds the built UI, only the container build
produces it, so it is gitignored — but the build still emits a static web assets manifest naming it, and
the host reads that manifest *inside* `WebApplication.CreateBuilder`. The result is an unhandled
`DirectoryNotFoundException` before a line of `Main` runs, which no runtime guard can catch. Fixed with
an MSBuild target that creates the directory.

### SQLite cannot sort a DateTimeOffset

Ordering the log by `StartedAt` failed outright: *"SQLite does not support expressions of type
'DateTimeOffset' in ORDER BY clauses."* SQLite has no date type, and EF refuses because rows written with
different UTC offsets would not sort by instant. Range filters have the same flaw but fail silently, which
would have hit Phase 7's date filters later and much less obviously.

Fixed at the mapping rather than in the query: `UtcDateTimeOffsetConverter` normalizes to UTC and stores
text in the exact format EF already used, applied to every `DateTimeOffset` in the model through
`ConfigureConventions`. All 5,376 existing rows read back unchanged and `has-pending-model-changes`
reports nothing — the column was always TEXT, so there is no migration. Ordering by text is now ordering
by instant, because the offset is invariant and a trimmed fraction compares as the zeros it stands for
(`+` sorts before every digit).

Verified against the real database: 5,376 calls, correct ordering across page boundaries, filters, clamping,
and a 400 on a bad enum. 29 new unit tests cover the paging arithmetic and the converter's round-trip and
sort order (63 total).

## Frontend — SvelteKit (scaffolded 2026-08-05)

The UI stack changed from **Next.js to SvelteKit** (Svelte 5 + TypeScript + Tailwind 4, scaffolded with
`sv create`). Nothing had been built on the Next.js scaffold — it was still the unmodified
`create-next-app` output — so this cost nothing and there was no migration to perform.

The frontend is being worked ahead of Phase 3 at the maintainer's request, so the phases are no longer
running in numeric order. Conventions specific to this scaffold that contradict most published Svelte
material are recorded in [`CallTree.UI/AGENTS.md`](CallTree.UI/AGENTS.md).

Currently: the default template page, type-checking clean (`pnpm check`, 0 errors). No CallTree features
yet — there is no API to read from until Phase 7.

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
- Configuration layering, hot reload, the password merge rules and the restart-required reporting:
  verified against a running instance, not just unit-tested.
- Unit tests: 81 passing.
