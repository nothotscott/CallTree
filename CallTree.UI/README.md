# CallTree.UI

The web frontend for [CallTree](../README.md) — a browser for recorded calls. Built with SvelteKit
(Svelte 5, TypeScript, Tailwind 4).

> **Status: early.** Three pages work: `/calls` (paginated, filterable call log), `/status` (trunk
> registration and the SIP stack's live state), and `/settings` (trunk and telephony configuration). A
> recording player needs recordings, which the backend does not produce yet (Phase 3); a call detail view
> needs a detail endpoint. See [`../TODO.md`](../TODO.md).
>
> This is a single-page app with no server-side rendering, so there is no `+page.server.ts` and no form
> actions — see [AGENTS.md](AGENTS.md#how-this-ships).

## Developing

```sh
pnpm install
pnpm dev          # or: pnpm dev --open
```

The backend runs separately, from `../CallTree.Core`:

```sh
dotnet run --project CallTree.Api
```

Vite proxies `/api` to it on port 5146, so there is no CORS to configure. The UI is at
<http://localhost:5173/calls>.

If you are working against a machine that owns a live SIP trunk, leave `Trunk:Host` unset so the backend
does not register and steal inbound calls from the real deployment.

## Checks

```sh
pnpm check        # type-check (svelte-kit sync && svelte-check)
pnpm lint         # prettier + eslint
pnpm format       # apply prettier
```

`pnpm lint` does not type-check — run `pnpm check` before considering a change done.

## Building

```sh
pnpm build
pnpm preview
```

`pnpm build` writes static files to `build/` via `@sveltejs/adapter-static` in SPA mode. In deployment
those files are copied into the ASP.NET host's `wwwroot` by `deploy/Dockerfile`, so the UI and the API ship
as one container on one port — there is no separate frontend image and no CORS to configure.

## Notes for contributors and agents

Conventions in this scaffold that differ from most published Svelte material — config living in
`vite.config.ts` rather than `svelte.config.js`, forced runes mode, CSS-configured Tailwind — are written
up in [AGENTS.md](AGENTS.md).

## Regenerating the scaffold

For reference, the project was created with:

```sh
pnpm dlx sv@0.17.0 create --template minimal --types ts --add prettier eslint tailwindcss="plugins:forms" --install pnpm CallTree.UI
```
