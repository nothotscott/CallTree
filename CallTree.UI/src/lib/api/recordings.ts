/** Mirrors the read models exposed by CallTree.Api's RecordingsController. */

import type { CallSource, PagedResult } from './calls';

export const CHANNEL_LAYOUTS = ['Mono', 'StereoPerLeg'] as const;
export type ChannelLayout = (typeof CHANNEL_LAYOUTS)[number];

export interface RecordingSummary {
	id: string;
	callId: string;
	callSource: CallSource;
	/** E.164, or null when the caller ID would not parse. */
	remoteNumber: string | null;
	rawCallerId: string;
	callStartedAt: string;
	channelLayout: ChannelLayout;
	createdAt: string;
	/** Null means the writer never finished (crash mid-call). */
	finalizedAt: string | null;
	durationSeconds: number | null;
	sizeBytes: number | null;
}

export const DEFAULT_PAGE_SIZE = 25;

export interface RecordingListParams {
	page?: number;
	pageSize?: number;
}

export function buildRecordingListQuery(params: RecordingListParams): URLSearchParams {
	const search = new URLSearchParams();
	if (params.page && params.page > 1) search.set('page', String(params.page));
	if (params.pageSize && params.pageSize !== DEFAULT_PAGE_SIZE) {
		search.set('pageSize', String(params.pageSize));
	}
	return search;
}

/**
 * The API is same-origin: in development Vite proxies /api to the backend (see vite.config.ts).
 * `fetch` is passed in so SvelteKit's load functions can supply their own instrumented version.
 */
export async function fetchRecordings(
	fetchFn: typeof globalThis.fetch,
	params: RecordingListParams
): Promise<PagedResult<RecordingSummary>> {
	const search = buildRecordingListQuery(params);
	const response = await fetchFn(`/api/recordings?${search}`);

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

/** Null when no recording has this id — the detail page tells that apart from a network failure. */
export async function fetchRecording(
	fetchFn: typeof globalThis.fetch,
	id: string
): Promise<RecordingSummary | null> {
	const response = await fetchFn(`/api/recordings/${id}`);

	if (response.status === 404) {
		return null;
	}
	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

/**
 * URL for the WAV itself, suitable as an `<audio>` element's `src`. Range-enabled on the server, so
 * seeking works. The API only serves this once `finalizedAt` is set — check that before rendering a
 * player, since a request against a still-recording call gets a 409.
 */
export function recordingAudioUrl(id: string): string {
	return `/api/recordings/${id}/audio`;
}

/** Reads list parameters out of a URL, ignoring anything that is not a value the API accepts. */
export function parseRecordingListParams(url: URL): RecordingListParams {
	const page = Number(url.searchParams.get('page'));

	return {
		page: Number.isFinite(page) && page > 1 ? Math.floor(page) : 1
	};
}
