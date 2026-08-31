import { MESSAGING_OFF, type MessagingCapabilities } from './api/messages';

/**
 * What the app currently believes SMS can do, shared by the navigation and the message log.
 *
 * The one piece of cross-route state in this frontend, and it earns the exception. Everything else here
 * lives in the URL or in a `load` function, but this is read by the root layout (to decide whether a
 * Messages link is worth offering) and written by the settings page (which is where it changes), and
 * those are not in a parent/child data relationship.
 *
 * Re-fetching after a save would be the obvious alternative and it is subtly wrong. The API writes
 * `Storage:ConfigFile` and the options monitor reloads it *asynchronously*, so a read issued immediately
 * after a save can still describe the configuration as it was before — the same trap documented against
 * `outboundPinSet` on the settings page. The PUT response describes what was just written, so that is
 * what gets adopted here. A page reload re-seeds from the server either way.
 */
class MessagingCapabilityState {
	enabled = $state(MESSAGING_OFF.enabled);
	canSend = $state(MESSAGING_OFF.canSend);

	set(next: MessagingCapabilities) {
		this.enabled = next.enabled;
		this.canSend = next.canSend;
	}
}

export const messagingCapability = new MessagingCapabilityState();
