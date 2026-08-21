# CallTree.UI — agent notes

SvelteKit frontend for [CallTree](../README.md). Read the root [CLAUDE.md](../CLAUDE.md) for what the
project is; this file covers only what is specific to the frontend.

Stack as installed: **Svelte 5.56**, **SvelteKit 2.70**, **Vite 8**, **Tailwind 4.3**, TypeScript 6, pnpm.

## Commands

```bash
pnpm dev        # vite dev
pnpm build      # vite build
pnpm check      # svelte-kit sync && svelte-check  <- the real type-check; run this
pnpm lint       # prettier --check . && eslint .
pnpm format     # prettier --write .
```

`pnpm lint` only checks formatting and lint rules. Type errors surface in `pnpm check`, so a change is not
verified until that passes. Currently clean: 0 errors, 0 warnings.

## Conventions that differ from most published Svelte material

These are the ones that will bite if you write from memory. Each is verified against what is actually
installed here, not inferred from the version numbers.

- **There is no `svelte.config.js`.** Configuration lives inside `vite.config.ts`, passed as options to the
  `sveltekit()` plugin. Nearly every tutorial and answer online tells you to edit `svelte.config.js`;
  creating one here is how you end up with config that is silently ignored. `svelte-kit sync` works fine
  without it.
- **Runes mode is forced project-wide**, via `compilerOptions.runes` in `vite.config.ts` (node_modules is
  excluded so libraries still compile). So the Svelte 4 idioms are not merely discouraged, they are
  compile errors: use `$props()` not `export let`, `$state()` not a plain reassigned variable, `$derived()`
  and `$effect()` not `$:`. Children come through `{@render children()}`, not `<slot />`.
- **Tailwind 4 has no `tailwind.config.js` and no PostCSS config.** It is wired as a Vite plugin
  (`@tailwindcss/vite`) and configured _in CSS_: `src/routes/layout.css` holds `@import 'tailwindcss'` and
  `@plugin '@tailwindcss/forms'`. Theme customisation goes in `@theme` blocks in that file. Adding a
  `tailwind.config.js` does nothing.
- **`.npmrc` sets `engine-strict=true`**, so an out-of-range Node version fails the install rather than
  warning.
- **Internal links must go through `resolve()`** from `$app/paths` — the `svelte/no-navigation-without-resolve`
  ESLint rule fails the build otherwise. The rule is syntactic: it looks for `resolve()` at the `href`
  itself, so wrapping the whole URL in a helper function does not satisfy it. Resolve the path and append
  the query separately: `href="{resolve('/calls')}{queryFor(page)}"`. Note `resolve()` returns a
  _relative_ URL (`./calls`), which is deliberate — it keeps the app working under any base path.

## Talking to the API

The backend is same-origin **in development and in production**. In development `vite.config.ts` proxies
`/api` to `http://localhost:5146`; in production this app is built to static files that the ASP.NET host
serves from its own `wwwroot` on the same port. Either way there is no CORS anywhere, and no absolute API
origin should ever be hard-coded.

Types for the API live in `src/lib/api/` (`calls.ts`, `config.ts`) and are hand-mirrored from the C#
models. Enums travel as **names**, not numbers. Data is fetched in `load` functions, and list state
(page, filters) lives in the URL rather than component state, so views are linkable and the back button
works.

The status page (`/status`) polls `GET /api/telephony/status` on an interval from an `$effect` that
returns its `clearInterval` — without that cleanup the timer survives navigation and keeps fetching. It
keeps the last good reading when a poll fails rather than blanking the page, since a status page that
goes empty on one dropped request is worse than one that says "last refresh failed".

The settings page (`/settings`) is the one place that writes:

- **Two secrets are write-only**, the trunk password and the outbound PIN. The API never sends either,
  only `trunkPasswordSet` / `outboundPinSet`. Send `null` when the field is blank — an empty string would
  be written to the config file and would then override a value coming from user secrets or the
  environment.
- **The PIN needs the switch as well as the field.** Because blank has to keep meaning "unchanged", an
  empty string is the only way to say "remove the gate", and there is no way to type that. Hence the
  "Require a PIN" checkbox: unchecked sends `''`. And after a save the switch is set from what was
  _sent_, never from the response — when the PIN was not part of the save, the response can still
  describe the pre-save configuration, because the file the API just wrote reloads asynchronously.
  Adopting that would flip the switch off on its own, and the next save would then genuinely clear the
  PIN on the path that answers automatically and records.
- **Two spellings of the same key.** Configuration keys use a colon (`Telephony:SipListenPort`, which is
  also how you would spell it as an environment variable); validation errors use the C# property path
  with a dot. `errorsFor`/`includesKey` normalise, so a field carries one key rather than two.
- **The form is seeded by an `$effect`, not by initialising `$state` from `data`.** Initialising once
  captures only the first value, so returning to the page later would show a form the server had moved
  on from — and `svelte-check` warns about exactly that (`state_referenced_locally`).

## Structure

```
src/
├── app.html              # shell; %sveltekit.head% / %sveltekit.body%
├── app.d.ts              # App.Locals / App.PageData ambient types
├── lib/                  # importable as $lib/*
└── routes/               # filesystem routing: +page.svelte, +layout.svelte, +page.ts, +server.ts
static/                   # served verbatim from the site root
```

## How this ships

`@sveltejs/adapter-static` in **SPA mode**: `fallback: 'index.html'` plus `ssr = false` in
`src/routes/+layout.ts`. `pnpm build` emits static files to `build/`, which `deploy/Dockerfile` copies into
the API's `wwwroot`. One container, one port, one origin.

Two consequences to keep in mind when writing code here:

- **There is no server to run anything on.** No `+page.server.ts`, no form actions, no server-only
  `$env` — load functions run in the browser. `ssr = false` is what makes the static build legal; don't
  turn it back on without also changing the adapter and the deployment.
- **Deep links depend on the host's fallback.** ASP.NET maps unmatched paths to `index.html`, which is why
  `/calls` works on a cold load. Unknown `/api/*` paths are excluded from that fallback and return 404, so
  a mistyped endpoint fails loudly instead of resolving to the shell document.

## Still open

- The backend has no auth in front of it and recordings are sensitive; the assumed posture is LAN-only.
  Don't add anything that assumes public exposure.
