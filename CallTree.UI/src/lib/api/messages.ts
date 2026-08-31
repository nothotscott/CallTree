/**
 * Mirrors the message read models exposed by CallTree.Api. Enums travel as names, not numbers.
 *
 * Note `source` is the *business* direction, the same convention the call log uses: both kinds arrive
 * at CallTree as an inbound message to the DID. `Inbound` is a stranger texting in (forwarded to your
 * mobile); `Outbound` is you texting the DID a `{number} body` command for it to send on.
 */

import type { PagedResult } from './calls';

export const MESSAGE_SOURCES = ['Outbound', 'Inbound'] as const;
export type MessageSource = (typeof MESSAGE_SOURCES)[number];

export const MESSAGE_STATUSES = [
	'Received',
	'Recorded',
	'Relaying',
	'Relayed',
	'Rejected',
	'Failed'
] as const;
export type MessageStatus = (typeof MESSAGE_STATUSES)[number];

/**
 * The statuses a line can actually reach, given whether it can send.
 *
 * A receive-only line — messaging on, no API key — never relays anything, so offering Relaying, Relayed
 * or Failed in the filter would be offering three filters that always come back empty.
 */
export function statusesFor(canSend: boolean): readonly MessageStatus[] {
	return canSend ? MESSAGE_STATUSES : ['Received', 'Recorded', 'Rejected'];
}

/** The carrier's last word on what was sent on. Delivery is a fact about the relay, not a status. */
export type RelayDelivery = 'Queued' | 'Sent' | 'Delivered' | 'Unconfirmed' | 'Failed';

export interface MessageSummary {
	id: string;
	source: MessageSource;
	status: MessageStatus;
	/** E.164 sender. */
	from: string;
	/** E.164 destination — our DID. */
	to: string;
	/** The body as received, before any forwarding prefix. */
	body: string;
	/** Attachments on the received message. Recorded, never forwarded. */
	mediaCount: number;
	receivedAt: string;
	completedAt: string | null;
	failureReason: string | null;
	relayRecipient: string | null;
	relayBody: string | null;
	relaySentAt: string | null;
	relayDelivery: RelayDelivery | null;
	relayError: string | null;
}

export const DEFAULT_PAGE_SIZE = 25;

export interface MessageListParams {
	page?: number;
	pageSize?: number;
	source?: MessageSource | null;
	status?: MessageStatus | null;
	search?: string | null;
}

export function buildMessageListQuery(params: MessageListParams): URLSearchParams {
	const search = new URLSearchParams();
	if (params.page && params.page > 1) search.set('page', String(params.page));
	if (params.pageSize && params.pageSize !== DEFAULT_PAGE_SIZE) {
		search.set('pageSize', String(params.pageSize));
	}
	if (params.source) search.set('source', params.source);
	if (params.status) search.set('status', params.status);
	if (params.search) search.set('search', params.search);
	return search;
}

/**
 * The API is same-origin: in development Vite proxies /api to the backend (see vite.config.ts).
 * `fetch` is passed in so SvelteKit's load functions can supply their own instrumented version.
 */
export async function fetchMessages(
	fetchFn: typeof globalThis.fetch,
	params: MessageListParams
): Promise<PagedResult<MessageSummary>> {
	const search = buildMessageListQuery(params);
	const response = await fetchFn(`/api/messages?${search}`);

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

/** Reads list parameters out of a URL, ignoring anything that is not a value the API accepts. */
export function parseMessageListParams(url: URL): MessageListParams {
	const page = Number(url.searchParams.get('page'));
	const source = url.searchParams.get('source');
	const status = url.searchParams.get('status');
	const search = url.searchParams.get('search');

	return {
		page: Number.isFinite(page) && page > 1 ? Math.floor(page) : 1,
		source: MESSAGE_SOURCES.includes(source as MessageSource) ? (source as MessageSource) : null,
		status: MESSAGE_STATUSES.includes(status as MessageStatus) ? (status as MessageStatus) : null,
		search: search && search.trim().length > 0 ? search.trim() : null
	};
}

/**
 * What this instance can do with SMS, mirroring `MessagingCapabilities` on the API.
 *
 * Two separate questions, because messaging can be switched on with no API key at all. That is a
 * supported way to run: a receive-only DID whose texts are read here rather than forwarded, which is
 * also the only way a US long code can be used until it is 10DLC-registered.
 */
export interface MessagingCapabilities {
	/** Whether the webhook is accepted. False means the whole feature is off. */
	enabled: boolean;
	/** Whether an API key is set, which is exactly whether anything can be sent onward. */
	canSend: boolean;
}

/** What to assume when the API cannot be reached: show nothing rather than a link that 404s. */
export const MESSAGING_OFF: MessagingCapabilities = { enabled: false, canSend: false };

export async function fetchMessagingCapabilities(
	fetchFn: typeof globalThis.fetch
): Promise<MessagingCapabilities> {
	const response = await fetchFn('/api/messages/capabilities');

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}
