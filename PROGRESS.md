# CallTree — Progress

What has been built so far. See [TODO.md](TODO.md) for remaining work and the full plan at
`C:\Users\Scott\.claude\plans\project-brief-self-hosted-serene-music.md` for rationale.

## Phase 0 — Foundation ✅

- Split the backend from a single template project into DDD projects under `CallTree.Core/`, all in `CallTree.Core.slnx`:
  - **CallTree.Domain** — entities, value objects, enums, domain events; zero dependencies.
  - **CallTree.Application** — ports (`ICallRepository`) and use-case services.
  - **CallTree.Infrastructure** — EF Core + SQLite persistence.
  - **CallTree.Telephony** — SIPSorcery/NAudio; hosted service that owns the SIP user agent.
  - **CallTree.Api** — ASP.NET Core host (renamed from the old `CallTree.Core` project); wires DI, hosts telephony.
  - **CallTree.Tests** — xUnit (v3).
- Domain model: `Call` aggregate root with an enforced state machine
  (`Ringing → Screening → Dialing → InProgress → Completed/ScreenedOut/Missed/Failed`), `CallLeg` (one SIP dialog
  per leg; bridged calls will have two), `Recording` entity with `FinalizedAt` crash marker, `PhoneNumber` E.164
  value object, domain events (`CallStarted/CallAnswered/CallBridged/CallEnded`).
- Persistence: `CallTreeDbContext` (enums stored as strings), `CallRepository`, `InitialCreate` migration,
  migration auto-applied at startup, db directory auto-created. DB lands in `CallTree.Api/data/` (gitignored).
- Config binding: `Trunk`, `Telephony` (MyCellNumber, SIP/RTP ports), `Storage` sections; user secrets enabled
  for sensitive values (`UserSecretsId` on the Api project).
- `/health` endpoint; Scalar/OpenAPI kept from the template.
- 21 unit tests: state-machine transitions, phone-number normalization, recording finalization, domain events.

## Phase 1 — SIP signaling ✅ (code-complete, E2E-verified locally)

- `TelephonyBackgroundService` now:
  - Binds a UDP SIP channel on `Telephony:SipListenPort`.
  - Registers with the trunk/PBX via `SIPRegistrationUserAgent`; all four registration events logged
    (success / removed / temporary failure / failed). Retry-on-timeout path verified against a dead registrar.
  - Routes SIPSorcery's internal logging into the host logging pipeline (`SIPSorcery.LogFactory.Set`).
  - Answers `OPTIONS` keepalives so Asterisk `qualify` sees the extension as reachable.
- Inbound call handling: logs every INVITE (caller ID, display name, remote endpoint, SIP Call-ID, User-Agent),
  classifies the caller against `Telephony:MyCellNumber` (`Outbound/CallerIdMatch` vs `Inbound/Default`),
  answers with a silence media session, holds 5 s, hangs up. Remote-BYE vs local-hangup races are guarded so
  exactly one terminal state is recorded. DTMF tones are logged (Phase 2 groundwork).
- `CallLifecycleService` (Application layer) drives the aggregate from telephony events —
  `StartAsync` / `AnswerAsync` / `EndAsync`, with `EndAsync` choosing the correct terminal transition for the
  current status. Telephony resolves it from a fresh DI scope per event.
- **End-to-end verified**: a scratch SIPSorcery caller placed a real call; it was answered in ~350 ms, held 5 s,
  hung up by CallTree, and the full `Call` + `CallLeg` row set appeared in SQLite with correct timestamps,
  classification, and `HangupInitiator`.
- Known Phase 1 quirk (intended): answered inbound calls end as `ScreenedOut` because they enter `Screening`
  and no IVR gate exists yet; resolves itself in Phase 2.

## Environment / tooling decisions made along the way

- Repo-local `NuGet.config` maps all packages to nuget.org, overriding the strict per-package
  source-mapping allowlist in the user-level NuGet.Config.
- `Microsoft.OpenApi` is pinned to **2.x** (2.10.0+): the `Microsoft.AspNetCore.OpenApi` source generator
  compiles against the 2.x object model and fails (CS0200) with 3.x.
- Vulnerable transitive packages pinned to patched versions (`SQLitePCLRaw.bundle_e_sqlite3`, `Microsoft.OpenApi`).
- Local `dotnet-ef` tool installed via `dotnet-tools.json` manifest.

## Validation status

- Phase 0: validated (build, tests, boot, migration, `/health`).
- Phase 1: validated E2E against a local scripted caller. **Pending**: Scott's manual validation against the
  real FreePBX/Asterisk box (register as an extension, dial it from a phone).
- Nothing committed to git yet.
