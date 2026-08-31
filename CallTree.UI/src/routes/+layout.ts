import { fetchMessagingCapabilities, MESSAGING_OFF } from '$lib/api/messages';
import { messagingCapability } from '$lib/messaging.svelte';
import type { LayoutLoad } from './$types';

// The UI ships as static files served by the ASP.NET host, so there is no Node server to render on.
// Turning SSR off is what lets adapter-static emit a single fallback document for every route; the
// load functions then run in the browser and fetch the API on the same origin.
//
// The trade-off is deliberate: this is a LAN-only call log behind a fetch either way, so server
// rendering buys nothing, and avoiding it means one container instead of two.
export const ssr = false;
export const prerender = false;

/**
 * Seeds what the app knows about SMS, once, for every route.
 *
 * Writing to a module-level store from a load function is only safe because `ssr = false`: this runs in
 * the browser, in one session, so there is no request whose state could leak into another's. It is
 * seeded here rather than returned as layout data because the settings page updates it after a save,
 * and layout data cannot be written from a child route.
 *
 * A failed fetch is not fatal. Messaging is simply treated as off, which is what an instance that has
 * never configured it would show anyway — losing a navigation link is a far better failure than every
 * page in the app failing to load because one optional feature could not be asked about.
 */
export const load: LayoutLoad = async ({ fetch }) => {
	const capability = await fetchMessagingCapabilities(fetch).catch(() => MESSAGING_OFF);
	messagingCapability.set(capability);
};
