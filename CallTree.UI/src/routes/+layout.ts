// The UI ships as static files served by the ASP.NET host, so there is no Node server to render on.
// Turning SSR off is what lets adapter-static emit a single fallback document for every route; the
// load functions then run in the browser and fetch the API on the same origin.
//
// The trade-off is deliberate: this is a LAN-only call log behind a fetch either way, so server
// rendering buys nothing, and avoiding it means one container instead of two.
export const ssr = false;
export const prerender = false;
