import { fetchMessages, parseMessageListParams, type MessageSummary } from '$lib/api/messages';
import type { PagedResult } from '$lib/api/calls';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ fetch, url }) => {
	const params = parseMessageListParams(url);

	// A failed fetch is returned rather than thrown, same as the call log: "the backend is not running"
	// is the likeliest cause in development and an error page cannot say so.
	try {
		const result: PagedResult<MessageSummary> = await fetchMessages(fetch, params);
		return { params, result, error: null };
	} catch (cause) {
		return {
			params,
			result: null,
			error: cause instanceof Error ? cause.message : 'The message log could not be loaded.'
		};
	}
};
