# CallTree

A self-hosted call recorder and bridge PBX built on a from-scratch SIP user agent. It owns a single phone
number on a SIP trunk, screens inbound callers, bridges them to your mobile, and records the conversation.

It is deliberately **not** a wrapper around Asterisk, FreeSWITCH or PJSIP. Signalling, media, DTMF and
recording are implemented directly against [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery), which
makes it a practical way to actually learn SIP and RTP rather than configure someone else's dial plan. If
you want a fully featured PBX, use a fully featured PBX.

> **Status: in development.** Phases 0–2 are complete and validated over a real trunk — registration,
> inbound signalling, prompt playback and the DTMF screening gate all work. Recording (Phase 3) and
> bridging (Phase 4) are not implemented yet. See [PROGRESS.md](PROGRESS.md) and [TODO.md](TODO.md).

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
- Node.js and pnpm, only if you want to work on the (as-yet unbuilt) web UI.

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

If registration succeeds but calls never arrive, the cause is almost always NAT — see
[Troubleshooting](#troubleshooting).

## Configuration

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
| `Telephony:ScreeningTimeoutSeconds` | `12` | How long they have to press it |
| `Telephony:PromptsRoot` | `prompts` | Prompt directory, relative to the content root |
| `Telephony:ListenOnTcp` | `true` | Also accept SIP over TCP |
| `Telephony:TraceSip` | `false` | Log complete SIP messages. Essential for bring-up, noisy after |
| `Storage:RecordingsRoot` | `data/recordings` | Where recordings are written |
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
| `greeting.wav` | On answer — the recording disclosure and the press-1 instruction |
| `accepted.wav` | The caller pressed the right digit |
| `rejected.wav` | Wrong digit, or no input before the timeout |

They are a content directory rather than embedded resources specifically so the wording can change without
a rebuild. The ones in the repository are synthesised placeholders; regenerate them, or edit the text
first, with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/generate-prompts.ps1
```

For production use, record them properly. A synthetic voice sets an unfortunate tone for a call that is
about to tell someone they are being recorded.

## Recording consent — read this

**Recording a call without the right consent is illegal in many places.** Some US states and many countries
require *all* parties to consent, not just one, and the rules differ for calls that cross jurisdictions.
CallTree does not and cannot make this decision for you.

The default `greeting.wav` includes a spoken recording notice because that errs toward disclosure, but the
wording, placement and whether a periodic tone is also needed are yours to determine. Note in particular
that in the three-way merge flow the *third* party never hears the inbound greeting — that gap is real and
deliberate to point out.

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

Also disable SIP ALG on your router if it has one; it rewrites SIP messages in flight and causes one-way
audio that is very hard to diagnose.

## Deployment

A Dockerfile, Compose file and a GitHub Actions workflow that publishes to the GitHub Container Registry
are in [`deploy/`](deploy/), along with notes on running under a Proxmox LXC. The container uses host
networking — SIP embeds addresses in the message body, so bridge NAT breaks media in ways that are
tedious to debug.

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
├── CallTree.UI/                 # Next.js frontend (not started)
├── deploy/                      # Dockerfile, Compose, firewall lists
└── tools/                       # Prompt generation
```

Dependencies point inwards: `Api → {Telephony, Infrastructure} → Application → Domain`. The SIP user agent
runs as a `BackgroundService` inside the API process, so one process serves both HTTP and SIP/RTP.

## Testing

```bash
dotnet test CallTree.Core/CallTree.Tests
```

Unit tests cover what is genuinely pure logic: the call state machine, phone-number normalisation, and the
WAV parsing and timing maths. Telephony behaviour is validated by placing real calls — a short SIPSorcery
console program makes a serviceable scripted caller for local end-to-end tests.

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

No licence has been chosen yet — until one is added, default copyright applies and others have no rights to
use this. Pick one before publishing.

## Acknowledgements

Built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) for SIP and RTP, and
[NAudio](https://github.com/naudio/NAudio) for WAV handling.
