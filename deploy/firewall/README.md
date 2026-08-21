# Trusted SIP provider addresses

Two plain lists, one address or CIDR per line and nothing else, so they can be pasted or imported straight
into a firewall address group (UniFi: **Settings → Security → Firewall Rules → Groups → IPv4 Address/Subnet**).

| File | Use it for |
|---|---|
| [`sip-signalling-ips.txt`](sip-signalling-ips.txt) | The SIP port rule — `5060/udp` (and `5060/tcp`, `5061/tcp` if you enable them) |
| [`sip-media-ips.txt`](sip-media-ips.txt) | The RTP port rule — whatever you set `Telephony:RtpPortStart`–`RtpPortEnd` to |

Signalling and media come from **different** address ranges, which is why these are separate files. Putting
media ranges on the 5060 rule (or vice versa) is a common way to end up with a call that connects but has
no audio.

## Why bother

An open SIP port is scanned continuously. A host running this project logged **276 rejected INVITEs in
about 40 minutes** from four independent sources, all sweeping international dial prefixes (`011…`,
`9011…`, `00…`) against premium-rate destinations — the standard hunt for a PBX that will place calls on
someone else's behalf. `Telephony:DidNumber` rejects those in-process, but dropping them at the router is
cheaper and stops them reaching the host at all.

This matters more once outbound calling exists (Phase 4): until then a probe is noise, afterwards it is a
potential phone bill.

## What is in the lists

Only providers whose published ranges could be verified directly. **Trim these to the regions you actually
use** — there is no reason to accept SIP from Sydney if your trunk terminates in Virginia.

### Telnyx — verified 2026-08-21 from <https://sip.telnyx.com/>

Signalling, by region: US `192.76.120.10`, `64.16.250.10` · Canada `192.76.120.31`, `64.16.250.13` ·
Europe `185.246.41.140`, `185.246.41.141` · Middle East `185.246.42.128`, `185.246.42.129` ·
Australia `103.115.244.145`, `103.115.244.146` · Asia `103.115.244.158`, `103.115.244.159`.

Media subnets are the `36.255.198.128/25` … `185.246.42.128/28` entries. Telnyx uses RTP ports
16384–32768 outbound; that only constrains their side, not the range you listen on.

Telnyx emailed on 2026-08-21 announcing a new media range, `103.115.247.0/24`, superseding the
narrower `103.115.247.128/27` this list previously carried — that /27 is a subset of the new /24, so
it was replaced rather than kept alongside it.

### Twilio Elastic SIP Trunking — verified 2026-07-31 from <https://www.twilio.com/docs/sip-trunking/ip-addresses>

Signalling is one `/30` per region: Virginia `54.172.60.0/30` · Oregon `54.244.51.0/30` ·
Ireland `54.171.127.192/30` · Frankfurt `35.156.191.128/30` · Tokyo `54.65.63.192/30` ·
Singapore `54.169.127.128/30` · Sydney `54.252.254.64/30` · São Paulo `177.71.206.192/30`.

Media is a single global range, `168.86.128.0/18`.

## Adding a provider

Not every provider publishes a stable address list. **VoIP.ms** is the notable case: it runs roughly thirty
POPs, load-balances behind hostnames, and its own guidance is to use the FQDN rather than an address. Its
server list is at <https://wiki.voip.ms/article/Servers> (that page blocks automated fetching, so the
addresses here were deliberately not guessed at). If you use them, take the addresses for the specific POP
your account is registered to and add them by hand.

For any provider, resolving the hostname gives you a starting point:

```bash
dig +short sip.example.com          # or: nslookup sip.example.com
```

Treat that as a hint, not a list — anycast and load balancing mean one lookup rarely shows every address.
Always prefer the provider's published allowlist page.

## Keeping them current

Providers do change these. If inbound calls stop arriving and nothing appears in the SIP trace even with
`Telephony:TraceSip` on, a stale allowlist is a prime suspect: re-check the source pages above before
digging into the application. Both lists carry a verification date for exactly that reason.
