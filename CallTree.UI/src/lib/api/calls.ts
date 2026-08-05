/** Mirrors the enums and read models exposed by CallTree.Api. Enums travel as names, not numbers. */

export const CALL_SOURCES = ['Outbound', 'Inbound'] as const;
export type CallSource = (typeof CALL_SOURCES)[number];

export const CALL_STATUSES = [
	'Ringing',
	'Screening',
	'Dialing',
	'InProgress',
	'Completed',
	'ScreenedOut',
	'Missed',
	'Failed'
] as const;
export type CallStatus = (typeof CALL_STATUSES)[number];

export type SourceClassification = 'Default' | 'CallerIdMatch' | 'PinVerified';

export interface CallSummary {
	id: string;
	source: CallSource;
	sourceClassification: SourceClassification;
	status: CallStatus;
	/** E.164, or null when the caller ID would not parse. */
	remoteNumber: string | null;
	rawCallerId: string;
	startedAt: string;
	answeredAt: string | null;
	endedAt: string | null;
	terminationReason: string | null;
	durationSeconds: number | null;
	talkTimeSeconds: number | null;
	hasRecording: boolean;
	recordingDurationSeconds: number | null;
}

export interface PagedResult<T> {
	items: T[];
	page: number;
	pageSize: number;
	totalCount: number;
	totalPages: number;
	hasPreviousPage: boolean;
	hasNextPage: boolean;
}

export const DEFAULT_PAGE_SIZE = 25;

export interface CallListParams {
	page?: number;
	pageSize?: number;
	source?: CallSource | null;
	status?: CallStatus | null;
}

export function buildCallListQuery(params: CallListParams): URLSearchParams {
	const search = new URLSearchParams();
	if (params.page && params.page > 1) search.set('page', String(params.page));
	if (params.pageSize && params.pageSize !== DEFAULT_PAGE_SIZE) {
		search.set('pageSize', String(params.pageSize));
	}
	if (params.source) search.set('source', params.source);
	if (params.status) search.set('status', params.status);
	return search;
}

/**
 * The API is same-origin: in development Vite proxies /api to the backend (see vite.config.ts).
 * `fetch` is passed in so SvelteKit's load functions can supply their own instrumented version.
 */
export async function fetchCalls(
	fetchFn: typeof globalThis.fetch,
	params: CallListParams
): Promise<PagedResult<CallSummary>> {
	const search = buildCallListQuery(params);
	const response = await fetchFn(`/api/calls?${search}`);

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

/** Reads list parameters out of a URL, ignoring anything that is not a value the API accepts. */
export function parseCallListParams(url: URL): CallListParams {
	const page = Number(url.searchParams.get('page'));
	const source = url.searchParams.get('source');
	const status = url.searchParams.get('status');

	return {
		page: Number.isFinite(page) && page > 1 ? Math.floor(page) : 1,
		source: CALL_SOURCES.includes(source as CallSource) ? (source as CallSource) : null,
		status: CALL_STATUSES.includes(status as CallStatus) ? (status as CallStatus) : null
	};
}
