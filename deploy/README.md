# Deploying CallTree

The backend is a single container that serves HTTP **and** speaks SIP/RTP, so networking is the part that
needs thought. Everything else is ordinary Compose.

## Where the image comes from

[`../.github/workflows/publish-container.yml`](../.github/workflows/publish-container.yml) builds on every
push to `master` and publishes to the **GitHub Container Registry** (`ghcr.io`), which is free for public
repositories and requires no signup beyond the repo itself — the workflow authenticates with the
automatically issued `GITHUB_TOKEN`, so there is no secret to configure.

Images land at `ghcr.io/<owner>/<repo>`, tagged `latest`, the short commit SHA, and semver on `v*` tags.

One manual step the first time: after the first successful run, open the package under your GitHub profile
→ **Packages** → **Package settings** and set visibility to **Public** if you want to pull without
authenticating. Otherwise `docker login ghcr.io` with a personal access token that has `read:packages`.

To build locally instead, from the repository root:

```bash
docker build -f deploy/Dockerfile -t calltree .
```

## One image, both halves

The image contains the backend **and** the web UI. `deploy/Dockerfile` builds the SvelteKit app to static
files in a Node stage and copies them into the API's `wwwroot`; ASP.NET serves them and falls back to
`index.html` for unmatched paths, so deep links like `/calls` work on a first load.

That means one container, one port, and one origin — so there is no CORS policy to configure and no second
service to deploy. The UI is at `http://<host>:8080/`, the API under `/api`, on the same port.

The cost is that the UI is a single-page app with no server-side rendering: `@sveltejs/adapter-static`
emits one fallback document and routing happens in the browser. For a LAN-only call log that is invisible.
If you ever want SSR, that is the point at which a second container (`adapter-node`) starts to earn its
keep — and it would need CORS or a reverse proxy in front of both.

## Configuring it

Trunk and telephony settings are edited in the web UI at **`/settings`**, which writes them to
`config.json` inside the data volume. Configuration is layered, lowest first:

| Layer | Where | Use it for |
|---|---|---|
| `appsettings.json` | baked into the image | defaults that ship |
| `config.json` | `/data/config.json`, written by the UI | this instance's settings |
| environment | `.env` / Compose `environment:` | pinning a value this host must always have |

Higher layers win, so a setting in the environment cannot be changed from the UI — the settings page
says so rather than accepting an edit that would do nothing. `.env.example` and `casaos-compose.yml`
therefore ship with the trunk and telephony blocks **commented out**: uncomment a line only when you
want it pinned.

Two consequences worth knowing:

- **The volume is the whole instance.** Database, recordings and configuration all live under `/data`,
  so moving that directory to another host moves the trunk with it. It contains the trunk password in
  plain text; treat it like the recordings.
- **Most settings need a restart.** Sockets are bound and the trunk registration is established once, at
  startup. The settings page lists exactly which saved keys are waiting on a restart rather than
  pretending they took effect. The numbers, the screening digit and timeout, and SIP tracing apply
  immediately.

Until a trunk is configured the container starts normally, serves the UI, and logs `telephony is idle`.

## Host networking, and why

`docker-compose.yml` sets `network_mode: host`. This is not laziness:

- SIP puts IP addresses and ports **inside the message body** (the SDP). A bridge network rewrites packet
  headers but not payloads, so the container advertises an address the trunk cannot reach. The classic
  symptom is a call that connects and then has no audio in one or both directions.
- RTP uses a range of UDP ports. Publishing them individually is tedious and slow.

The trade-off is that the container shares the host's network stack, so keep it on a dedicated LXC or VM.

Because SIP binds port 5060 — privileged — the process runs as root inside the container. If you would
rather not, raise `Telephony__SipListenPort` above 1024 and translate the port at your router; the
registration `Contact` automatically advertises whatever port is actually in use.

## On a Proxmox LXC

An LXC needs a couple of things before it can run Docker properly.

1. Create the container. A **privileged** LXC is the path of least resistance for Docker; unprivileged
   works but needs extra idmap and cgroup fiddling. 2 vCPU / 2 GB RAM is ample.

2. Enable nesting and keyctl, either in the Proxmox UI (**Options → Features**) or on the host:

   ```bash
   pct set <vmid> --features nesting=1,keyctl=1
   ```

3. Inside the container, install Docker and create the data directories:

   ```bash
   curl -fsSL https://get.docker.com | sh
   mkdir -p /srv/calltree/data /srv/calltree/prompts
   ```

4. Copy the prompt WAVs in (or drop the `prompts` bind mount to use the ones baked into the image):

   ```bash
   scp CallTree.Core/CallTree.Api/prompts/*.wav root@<lxc>:/srv/calltree/prompts/
   ```

5. Configure and start:

   ```bash
   cp .env.example .env      # nothing to fill in for a first start
   docker compose up -d
   docker compose logs -f
   ```

6. Open `http://<lxc>:8080/settings` and fill in the trunk credentials, the DID, and `PublicHost`, then
   restart: `docker compose restart`. Trunk settings are read once, when the service registers.

Confirm it came up with `curl localhost:8080/health` and look for `SIP registration successful` in the
logs. Registration failing here almost always means the public host is wrong or unset.

## On CasaOS

[`casaos-compose.yml`](casaos-compose.yml) is a variant for CasaOS's **Custom Install → Import**, which
takes a pasted Compose file. Paste the whole file, install, then configure the trunk from the app's
`/settings` page — there is nothing to edit in the YAML first.

It is a separate file rather than the same one because CasaOS's workflow rules out two things
`docker-compose.yml` relies on:

- **`env_file` cannot work.** Pasted YAML has no `.env` beside it on disk, so anything set there is
  inline — and would then live in the app's Compose file under `/var/lib/casaos/apps/calltree/`,
  root-readable on that host. Configuring from the UI instead keeps the credentials in the data volume,
  which is the main reason the trunk block in that file is commented out.
- **`${VAR:-default}` has nothing to substitute from**, so the image tag is literal.

The file also carries `x-casaos` metadata. CasaOS uses the top-level `name` and `x-casaos.main` to work out
which service is the app; without them the import is rejected or produces a tile whose web link goes
nowhere. `architectures` claims **amd64 only**, because
[`../.github/workflows/publish-container.yml`](../.github/workflows/publish-container.yml) passes no
`platforms` list and therefore builds for the GitHub runner alone. Add `linux/arm64` there before widening
it here.

Everything in [Host networking](#host-networking-and-why) and [Router configuration](#router-configuration)
still applies — CasaOS is a management layer over the same Docker daemon, and `network_mode: host` behaves
identically underneath it.

Two things to watch:

- **The prompts mount is commented out on purpose.** Bind-mounting an empty directory over `/app/prompts`
  hides the WAVs baked into the image, and the IVR then answers calls in silence. That failure looks like
  success — signalling works, the call connects — right up until nobody hears the press-a-digit
  instruction. Copy the WAVs into `/DATA/AppData/calltree/prompts` first, then uncomment.
- **Only one instance may register at a time.** If you were previously running CallTree elsewhere against
  the same trunk credential, stop it. The provider keeps the most recent binding, so two instances mean
  calls arriving at whichever re-registered last.

## Router configuration

Forward to the LXC's address:

| Port | Protocol | Purpose |
|---|---|---|
| 5060 | UDP (and TCP if enabled) | SIP signalling |
| 10000–10100 | UDP | RTP media — must match `Telephony__RtpPortStart`/`End` |

**Restrict both forwards by source address.** An open SIP port is scanned continuously by people looking
for a PBX that will place international calls on their behalf. Importable lists are in
[`firewall/`](firewall/), with the reasoning written up there.

Also disable **SIP ALG** on the router if it offers one. It rewrites SIP messages in transit and is a
long-standing source of one-way audio and calls that fail for no visible reason.

## Backups and upgrades

Everything that matters is under `/srv/calltree`: the SQLite database, the recordings, and `config.json`.
Copy that directory and you have the lot — including, in plain text, the trunk password. Upgrading is
`docker compose pull && docker compose up -d`; migrations are applied automatically at startup and
`config.json` is untouched by an image upgrade, which is the point of it living on the volume.

Recordings are sensitive. The default posture is LAN-only with no authentication in front of the API —
think carefully before exposing it, and see the open questions in [`../TODO.md`](../TODO.md).
