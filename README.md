# CallTree

A self-hosted call recorder and bridge PBX built on a from-scratch SIP user agent. It owns a single phone
number on a SIP trunk, screens inbound callers, bridges them to your mobile, and records the conversation.

It is deliberately **not** a wrapper around Asterisk, FreeSWITCH or PJSIP. Signalling, media, DTMF and
recording are implemented directly against [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery), which
makes it a practical way to actually learn SIP and RTP rather than configure someone else's dial plan. If
you want a fully featured PBX, use a fully featured PBX.

> **Status: in development.** Phases 0–2 are complete and validated over a real trunk — registration,
> inbound signalling, prompt playback and the DTMF screening gate all work. Phase 3, recording calls from
> your own number, is written and unit-tested but has not yet been validated over the trunk. Bridging
> (Phase 4) is not implemented. The SvelteKit web UI has a call log, a status page and a settings page.
> See [PROGRESS.md](PROGRESS.md) and [TODO.md](TODO.md).

## How it works

One number; every call to or from it passes through CallTree, which classifies each one by caller ID:

- **`CallSource.Outbound`** — the caller ID matches your own mobile (`Telephony:MyCellNumber`). The call is
  auto-answered and recorded immediately. You then use your phone's native three-way merge to add the other
  party, so a single mono leg captures both sides of the conversation.
- **`CallSource.Inbound`** — anyone else. They hear a prompt and must press a digit to get through, which
  turns away most automated spam. Once past the gate the call is bridged to your mobile and recorded in
  stereo, one leg per channel.

Both start life as inbound SIP INVITEs — `Outbound`/`Inbound` describe the *business* meaning, while
`LegDirection` describes the SIP-level direction of an individual leg.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A SIP trunk with a DID. Any provider supporting credential registration and PCMU should work; Telnyx is
  the one this has been exercised against.
- A publicly reachable IP or DDNS hostname, with UDP 5060 and an RTP port range forwarded.
- Node.js and pnpm, only if you want to work on the web UI (SvelteKit; scaffolded, no features yet).

## Quick start

```bash
cd CallTree.Core
dotnet build CallTree.Core.slnx
dotnet test CallTree.Tests
```

Configure the trunk. In development these belong in user secrets, never in `appsettings.json`:

```bash
cd CallTree.Api
dotnet user-secrets set "Trunk:Host"              "sip.example.com"
dotnet user-secrets set "Trunk:Username"          "your-sip-user"
dotnet user-secrets set "Trunk:Password"          "your-sip-password"
dotnet user-secrets set "Telephony:DidNumber"     "+15555550100"
dotnet user-secrets set "Telephony:MyCellNumber"  "+15555550199"
dotnet user-secrets set "Telephony:PublicHost"    "your.public.ip.or.hostname"
```

Then run it. The SQLite database is created and migrated automatically on first boot:

```bash
dotnet run --project CallTree.Api
```

Look for `SIP registration successful` in the log, then call your DID.

You can also set all of these from the web UI's settings page instead, which writes them to
`data/config.json` — that is how a container is meant to be configured. User secrets are still the right
place in development, because they sit *above* that file and stay out of the working tree.

If registration succeeds but calls never arrive, the cause is almost always NAT — see
[Troubleshooting](#troubleshooting).

## Configuration

Settings come from three layers, each overriding the one above it:

1. **`appsettings.json`** — defaults, baked into the build.
2. **`config.json`** — written by the settings page in the web UI, at `Storage:ConfigFile`
   (`data/config.json` by default, `/data/config.json` in the container). It lives on the data volume
   alongside the database and the recordings, so the volume carries the whole instance. Changes are
   picked up without a restart; whether the *SIP stack* can act on them is a separate question, below.
3. **Environment variables** (and user secrets in development) — for values a particular host must
   always have. These win, so the settings page reports them as overridden rather than accepting an
   edit that would silently do nothing.

Most settings are read once, when the sockets are bound and the trunk registration is established:
changing them saves fine but needs a restart, and the settings page lists exactly which ones are
waiting. Everything read per call applies immediately — `MyCellNumber`, `DidNumber`, `ScreeningDigit`,
`ScreeningTimeoutSeconds`, `OutboundPin`, `JitterBufferMilliseconds`, `RecordingToneIntervalSeconds` and
`TraceSip`.

`config.json` holds the trunk password and the outbound PIN in plain text — necessary if the UI is to set
them. It is written owner-only where the platform supports it; treat it like the recordings.

| Setting | Default | What it does |
|---|---|---|
| `Trunk:Host` | — | Registrar hostname |
| `Trunk:Port` | `5060` | Registrar port |
| `Trunk:Username` / `Trunk:Password` | — | Credentials for digest authentication |
| `Trunk:RegistrationExpirySeconds` | `120` | Registration lifetime. Short values keep NAT bindings alive |
| `Telephony:DidNumber` | — | The number this instance owns. INVITEs for anything else get a 404. **Set this** |
| `Telephony:MyCellNumber` | — | Calls from here are classified `Outbound` |
| `Telephony:PublicHost` | — | Public IP or hostname. **Required behind NAT** |
| `Telephony:SipListenPort` | `5060` | Local SIP port |
| `Telephony:RtpPortStart` / `RtpPortEnd` | `10000` / `10100` | RTP range; must match your port forward |
| `Telephony:ScreeningDigit` | `1` | Digit an inbound caller must press |
| `Telephony:ScreeningTimeoutSeconds` | `12` | How long they have to press it. Also the PIN deadline |
| `Telephony:OutboundPin` | — | PIN required before a call from your own number is answered and recorded. Blank means caller ID alone decides, and caller ID is spoofable |
| `Telephony:JitterBufferMilliseconds` | `60` | How long received audio is held so out-of-order RTP can be reordered before it is written |
| `Telephony:RecordingToneIntervalSeconds` | `0` | Seconds between recording-notice tones; `0` for none. The only notice a merged-in third party hears — see [Recording consent](#recording-consent--read-this) |
| `Telephony:PromptsRoot` | `prompts` | Prompt directory, relative to the content root |
| `Telephony:ListenOnTcp` | `true` | Also accept SIP over TCP |
| `Telephony:TraceSip` | `false` | Log complete SIP messages. Essential for bring-up, noisy after. Raises the `CallTree.Telephony.SipTrace` log category to `Trace` by itself — there is no second logging setting to keep in step — and applies without a restart |
| `Storage:RecordingsRoot` | `data/recordings` | Where recordings are written |
| `Storage:ConfigFile` | `data/config.json` | The file the settings page writes |
| `ConnectionStrings:CallTree` | `Data Source=data/calltree.db` | SQLite database |

In containers, use environment variables with double underscores: `Telephony__DidNumber`.

## Audio codecs

CallTree offers exactly one codec. G.711 µ-law is universally supported, converts to and from 16-bit PCM
with a symmetric 2:1 mapping, and needs no transcoding when relaying between two legs — which keeps the
recording and bridging code simple enough to read.

| Codec | Payload type | Rate | Status |
|---|---|---|---|
| **PCMU** (G.711 µ-law) | 0 | 8 kHz | **Offered and negotiated.** The only media codec advertised |
| **telephone-event** (RFC 4733) | 101 | 8 kHz | **Always negotiated.** Carries DTMF; added independently of the codec list |
| PCMA (G.711 A-law) | 8 | 8 kHz | Supported by the underlying stack, not currently offered |
| G.722 | 9 | 16 kHz | Not offered. Trunks will pick it if you let them — see below |
| G.729 | 18 | 8 kHz | Not offered |

The restriction is applied in `TelephonyBackgroundService.CreateMediaSession` via
`AudioExtrasSource.RestrictFormats`. It matters: left unrestricted, the answer echoes the trunk's full
offer, and at least one provider ranks G.722 first — which would silently change the sample rate that the
recording and bridging paths depend on.

Restricting the codec list does **not** disable DTMF. The telephone-event payload is added separately.

Prompt files must be **8 or 16 kHz, 16-bit, mono PCM WAV**. Anything else is rejected at startup with a
clear error rather than played as noise.

## Prompts

Prompts live in `CallTree.Api/prompts/` as ordinary `.wav` files, loaded once at startup:

| File | When it plays |
|---|---|
| `greeting.wav` | On answer to an inbound caller — the recording disclosure and the press-1 instruction |
| `accepted.wav` | The caller pressed the right digit |
| `rejected.wav` | Wrong digit, no input before the timeout, or a failed PIN |
| `recording-notice.wav` | To *you*, on a call from your own number, just before recording starts |
| `pin-request.wav` | Asks for `Telephony:OutboundPin`. Only used when one is configured |
| `recording-tone.wav` | The periodic tone, when `Telephony:RecordingToneIntervalSeconds` is non-zero |

They are a content directory rather than embedded resources specifically so the wording can change without
a rebuild. The ones in the repository are synthesised placeholders; regenerate them, or edit the text
first, with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/generate-prompts.ps1
```

Every run of the synthesiser produces slightly different bytes, so to add or change one prompt without
rewriting the others, name it:

```powershell
powershell -ExecutionPolicy Bypass -Command "& tools/generate-prompts.ps1 -Only greeting"
```

For production use, record them properly. A synthetic voice sets an unfortunate tone for a call that is
about to tell someone they are being recorded.

## How recording works

A call from your own number is answered, optionally gated by `Telephony:OutboundPin`, and then recorded to
a mono 16-bit WAV under `Storage:RecordingsRoot`, grouped by month and named for the call.

Only *received* audio is captured — which is the whole point of the design. You add the other party with
your phone's own three-way merge, so by the time it matters this single leg already carries both voices
mixed together, and CallTree needs no second leg and no mixing to record a two-party conversation.

The **RTP timestamp drives the file**, not a wall clock. For PCMU that timestamp counts samples at 8 kHz,
so it says exactly where each packet belongs; writing packets back to back as they arrive would quietly
compress every pause in the conversation and drift against the sender over a long call. Gaps are therefore
measurable, and are filled with real silence so the recording stays in step with what was actually said. A
timestamp jump too large to be a pause is treated as a discontinuity and resynchronised instead, because
one bogus value would otherwise ask for however many gigabytes of silence it implies.

Received packets are held briefly — `Telephony:JitterBufferMilliseconds`, 60 ms by default — so packets
that overtook each other in the network can be put back in order before anything is written. This is a
reordering buffer, not a playout buffer: nothing is being played, so it costs latency in the file rather
than in the call. The header is re-patched every few seconds, so a process killed mid-call leaves a file
that still plays up to the last flush rather than one every tool reads as empty.

## Recording consent — read this

**Recording a call without the right consent is illegal in many places.** Some US states and many countries
require *all* parties to consent, not just one, and the rules differ for calls that cross jurisdictions.
CallTree does not and cannot make this decision for you.

There is a structural gap you need to understand before using the recording path:

- Inbound callers hear `greeting.wav`, which carries a spoken notice.
- On a call from your own number, `recording-notice.wav` is played **to you** — and only to you. The party
  you merge in afterwards joins through your handset, which CallTree is never told about. They will not
  hear any spoken notice, ever.

The only disclosure CallTree can make mechanically to that party is the periodic tone, enabled by setting
`Telephony:RecordingToneIntervalSeconds`. It is **off by default**, which means that out of the box,
disclosing to the merged-in party is entirely your job and has to be done out loud. Turning the tone on
does not by itself make the recording lawful either — the interval, the wording and whether a tone is
even accepted as consent all vary.

This is not legal advice. If you deploy this, work out what your jurisdiction requires first.

## Security

An open SIP port is scanned continuously. A live deployment logged **276 rejected INVITEs in roughly 40
minutes** from four independent sources, all sweeping international dial prefixes looking for a PBX that
would place calls on their behalf.

Two mitigations, and you want both:

1. **`Telephony:DidNumber`** — CallTree rejects any INVITE not addressed to your number with a 404, before
   any database row is created. This is on by default once the setting is populated.
2. **Restrict your router's port forwards by source address.** Importable address lists for several trunk
   providers are in [`deploy/firewall/`](deploy/firewall/).

Separately, **`Telephony:OutboundPin`** guards the recording path. Without it, the only thing between a
stranger and a call that is answered automatically and recorded is a caller ID match, and caller ID is
trivially forged. Today the worst that buys an attacker is a junk recording and some disk; once Phase 4
can place an outbound leg, the same forgery is what turns a probe into a phone bill. It is blank by
default so that bringing the recording path up does not require deciding this first — but decide it.

Also disable SIP ALG on your router if it has one; it rewrites SIP messages in flight and causes one-way
audio that is very hard to diagnose.

## Deployment

A Dockerfile, Compose files and a GitHub Actions workflow that publishes to the GitHub Container Registry
are in [`deploy/`](deploy/), along with notes on running under a Proxmox LXC. The container uses host
networking — SIP embeds addresses in the message body, so bridge NAT breaks media in ways that are
tedious to debug.

**One image contains both halves.** The web UI is built to static files and served by the ASP.NET host, so
there is no separate frontend container, no second port, and no CORS to configure — browse it at
`http://<host>:8080/`.

**CasaOS** is supported directly: [`deploy/casaos-compose.yml`](deploy/casaos-compose.yml) is written for
its **Custom Install → Import** dialog. Paste the file, install, then configure the trunk from the app's
settings page — nothing has to be edited in the YAML first, which also keeps the trunk password out of a
Compose file on that host. See [`deploy/README.md`](deploy/README.md#on-casaos).

## Web UI and API

The backend exposes a read-only call log, a settings endpoint and a telephony status endpoint; the
SvelteKit frontend in [`CallTree.UI/`](CallTree.UI/) is the browser for all three. Run the two together:

```bash
dotnet run --project CallTree.Api    # from CallTree.Core/, serves the API on :5146
pnpm dev                             # from CallTree.UI/, serves the UI on :5173
```

Vite proxies `/api` to the backend, so the browser sees a single origin and there is no CORS to configure.
The UI is at <http://localhost:5173/calls>, with `/status` and `/settings` alongside it.

| Endpoint | Purpose |
|---|---|
| `GET /api/calls` | Paginated call log, most recent first |
| `GET /api/config` | Effective Telephony and Trunk settings |
| `PUT /api/config` | Save them to `Storage:ConfigFile` |
| `GET /api/telephony/status` | Trunk registration state and the SIP stack's live view |
| `GET /health` | Liveness |

`GET /api/calls` accepts `page` (1-based), `pageSize` (default 25, capped at 200), `source`
(`Inbound`/`Outbound`) and `status`. Out-of-range paging is clamped rather than rejected; an unrecognised
enum name is a 400. Enums are serialized as names. The response carries `items` plus `page`, `pageSize`,
`totalCount`, `totalPages`, `hasPreviousPage` and `hasNextPage`.

The config endpoints never return the trunk password or the outbound PIN — only `trunkPasswordSet` and
`outboundPinSet`. On `PUT`, omitting either leaves the configured value alone, so the UI can save an
unrelated field without ever holding a secret; sending an empty string is how you deliberately clear one.
The response also reports `pendingRestartKeys` (saved, but the running SIP stack is still on the old
value), `restartOnlyKeys` and `environmentOverrides`.

`GET /api/telephony/status` answers "is the trunk up, and if not, why not" without reading the log. It
carries the registration state and the registrar's own failure message, plus the things that otherwise
fail identically — as a caller hearing a busy tone with nothing logged at all: the Contact the registrar
echoed back (the address it will actually dial), what we advertise in `Contact` and SDP, the bound SIP
endpoints, whether the DID filter is active, and which prompts loaded. The page at `/status` polls it
and raises each of those as a warning in its own right.

> **There is no authentication on the API.** The assumed posture is LAN-only. This matters more now than
> it did for the call log alone: `/api/config` discloses the DID, the mobile number, the public host and
> the trunk username, and `PUT` can repoint the trunk or clear the DID filter that keeps toll-fraud
> probes out. Decide the auth story before exposing it — see [TODO.md](TODO.md).

To work on the UI without disturbing a live deployment, leave `Trunk:Host` unset: telephony logs
`telephony is idle` and never registers, so whichever instance owns the trunk keeps it.

## Project layout

```
CallTree/
├── CallTree.Core/               # Backend (.NET 10)
│   ├── CallTree.Domain/           # Aggregates, value objects, domain events. No dependencies
│   ├── CallTree.Application/      # Ports and use cases; call commands
│   ├── CallTree.Infrastructure/   # EF Core + SQLite
│   ├── CallTree.Telephony/        # SIPSorcery + NAudio; owns the SIP user agent
│   ├── CallTree.Api/              # ASP.NET Core host, DI wiring, /health
│   └── CallTree.Tests/            # xUnit; pure logic only
├── CallTree.UI/                 # SvelteKit frontend (call log)
├── deploy/                      # Dockerfile, Compose, firewall lists
└── tools/                       # Prompt generation
```

Dependencies point inwards: `Api → {Telephony, Infrastructure} → Application → Domain`. The SIP user agent
runs as a `BackgroundService` inside the API process, so one process serves both HTTP and SIP/RTP.

## Testing

```bash
dotnet test CallTree.Core/CallTree.Tests
```

Unit tests cover what is genuinely pure logic: the call state machine, phone-number normalisation, the WAV
parsing and timing maths, the G.711 decode (checked against NAudio's decoder for all 256 codes), the RTP
reordering buffer, and the recorder's silence-fill and discontinuity handling. Telephony behaviour itself
is validated by placing real calls — a short SIPSorcery console program makes a serviceable scripted
caller for local end-to-end tests.

## Troubleshooting

**Registration succeeds but inbound calls never arrive, and nothing is logged.** Almost always NAT. Set
`Telephony:PublicHost`; without it the trunk is told to reach you at a LAN address. Turn on
`Telephony:TraceSip` and check what the registrar echoes back in its `200 OK` `Contact` header — that is
the address it will actually dial.

**A `200 OK` on REGISTER does not mean you are registered.** If your provider's portal shows the connection
as unregistered while SIP says success, check that the `Contact` header includes a user part.

**Calls connect but there is no audio.** The SDP is advertising the wrong address, or RTP is not getting
through. Confirm the `c=` line in the answer carries your public address, and that your RTP port range is
forwarded and matches `Telephony:RtpPortStart`/`End`.

**Nothing reaches the application at all.** Send a SIP `OPTIONS` to your public IP from inside the LAN;
many routers hairpin it back through the port forward, so a `200 Ok` proves the forward and host firewall
are working without needing a second phone.

## Licence

[MIT](LICENSE.md).

## Acknowledgements

Built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) for SIP and RTP, and
[NAudio](https://github.com/naudio/NAudio) for WAV handling.
