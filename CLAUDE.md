# CallTree

Self-hosted call recorder / bridge PBX. A from-scratch SIP user agent (SIPSorcery — no Asterisk/PJSIP)
embedded in an ASP.NET Core backend, owning one DID via a SIP trunk. Also a deliberate SIP/RTP learning
project: prefer understandable from-scratch implementations over drop-in PBX features.

## What it does

One DID; all calls to/from it pass through CallTree. Calls are classified by caller ID:

- **CallSource.Outbound** — caller ID matches `Telephony:MyCellNumber` (Scott's cell). Auto-answer and
  record immediately; Scott then uses his cell's native 3-way merge to add the real party, so a single
  mono leg captures both sides.
- **CallSource.Inbound** — anyone else. IVR spam gate ("press 1"), then bridge to the cell and record
  (stereo, one leg per channel).

Naming note: `Outbound`/`Inbound` are **business** classifications; both start as inbound SIP INVITEs.
`LegDirection` is the SIP-level direction.

## Status & plan

- Approved 8-phase plan: `C:\Users\Scott\.claude\plans\project-brief-self-hosted-serene-music.md`.
- Done vs remaining: [PROGRESS.md](PROGRESS.md) / [TODO.md](TODO.md). Phases 0–1 complete; next is
  Phase 2 (WAV prompt + press-1 gate).
- **Workflow rule: one phase at a time — Scott manually validates each phase by phone before the next begins.**
- Phases 1–5 are developed against Scott's existing FreePBX/Asterisk as a local extension; the paid SIP
  trunk (provider TBD) arrives in Phase 6.

## Solution layout

```
CallTree/
├── NuGet.config             # repo-local; overrides Scott's strict user-level packageSourceMapping
├── dotnet-tools.json        # local dotnet-ef
├── CallTree.Core/           # backend (.NET 10, CallTree.Core.slnx)
│   ├── CallTree.Domain/         # aggregates, VOs, enums, domain events — no dependencies
│   ├── CallTree.Application/    # ports (ICallRepository) + CallLifecycleService
│   ├── CallTree.Infrastructure/ # EF Core + SQLite (CallTreeDbContext, migrations, CallRepository)
│   ├── CallTree.Telephony/      # SIPSorcery + NAudio; TelephonyBackgroundService owns the SIP UA
│   ├── CallTree.Api/            # ASP.NET Core host; DI wiring; /health; Scalar at /scalar (dev)
│   └── CallTree.Tests/          # xUnit v3 — pure-logic tests only (state machine, PhoneNumber, WAV logic)
└── CallTree.UI/             # Next.js 16.2 frontend (has its own CLAUDE.md/AGENTS.md — read them)
```

Dependency direction: `Api → {Telephony, Infrastructure} → Application → Domain`.

## Architecture rules

- The SIP UA lives **inside the API process** as a `BackgroundService`; one process serves HTTP and SIP/RTP.
- Domain `Call` records history and enforces transition legality
  (`Ringing → Screening → Dialing → InProgress → Completed/ScreenedOut/Missed/Failed`); live SIPSorcery
  objects never enter the Domain. Runtime per-call state belongs in Telephony (a `CallSession` class is the
  planned shape once Phase 4 needs it).
- Telephony persists via `CallLifecycleService`, resolved from a **fresh DI scope per telephony event**
  (`DbContext` is scoped; telephony callbacks are long-lived).
- "Recording" is a fact (`Recording` entity), never a `CallStatus`.
- Bridging (RTP payload relay) and recording (decoded PCM tap) are separate concerns — don't conflate.
- Enums persist as strings; timestamps are `DateTimeOffset` passed explicitly into domain methods (testability).
- Trunk credentials/config live in options bound from configuration, never in the DB or the domain.

## Commands

Run from `CallTree.Core/` unless noted:

```bash
dotnet build CallTree.Core.slnx
dotnet test CallTree.Tests
dotnet run --project CallTree.Api                  # migrates SQLite automatically on boot
dotnet ef migrations add <Name> --project CallTree.Infrastructure --startup-project CallTree.Api --output-dir Persistence/Migrations
```

Secrets (Development) go in user secrets, not appsettings:

```bash
dotnet user-secrets set "Trunk:Host" "..." --project CallTree.Api     # also Trunk:Username, Trunk:Password
dotnet user-secrets set "Telephony:MyCellNumber" "+1..." --project CallTree.Api
```

Frontend (`CallTree.UI/`): pnpm (`pnpm dev`, `pnpm build`, `pnpm lint`).

## Gotchas (hard-won — don't rediscover these)

- **NuGet**: Scott's user-level NuGet.Config allowlists packages per-source; the repo-local `NuGet.config`
  (pattern `*` → nuget.org) overrides it. Without it, new package adds fail with NU1100/"incompatible".
- **Microsoft.OpenApi must stay 2.x** (currently 2.10.0). 3.x breaks the `Microsoft.AspNetCore.OpenApi`
  source generator (CS0200 in generated code). Already tried twice.
- **SIPSorcery is v10** — APIs differ from older docs/samples. Verify signatures against the actual package
  (reflection dump or `~/.nuget/packages/sipsorcery/10.0.12/lib/net10.0/SIPSorcery.xml`) before coding.
  Confirmed working patterns: `VoIPMediaSession(new MediaEndPoints { AudioSource = new AudioExtrasSource(...) })`,
  `SIPUserAgent.OnIncomingCall` + `AcceptCall`/`Answer(uas, media, null)`,
  `SIPRegistrationUserAgent(transport, user, pass, server, expiry, exitOnUnequivocalFailure: false)`.
- **Next.js 16.2** has breaking changes vs. training-data conventions — read the guides in
  `CallTree.UI/node_modules/next/dist/docs/` before writing frontend code.
- SQLite won't create missing parent directories — `Program.EnsureDatabase` handles it; keep that when touching startup.
- Runtime data (`CallTree.Api/data/` — db + recordings) is gitignored; recordings root is config (`Storage:RecordingsRoot`).
- Answer OPTIONS keepalives (already done) or Asterisk marks the extension unreachable.
- Legal: Florida is **all-party consent** for recording. The consent-disclosure approach is an open decision —
  never silently drop or "simplify away" disclosure prompts/tones once they exist.

## Testing philosophy

Unit-test what's pure logic (domain transitions, normalization, WAV/timing math). Telephony behavior is
validated per-phase by real phone calls (see plan §Verification); a scratch SIPSorcery console caller works
for local E2E when Asterisk isn't handy.
