import {
	fetchCalls,
	parseCallListParams,
	type CallSummary,
	type PagedResult
} from '$lib/api/calls';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ fetch, url }) => {
	const params = parseCallListParams(url);

	// A failed fetch is returned rather than thrown: "the backend is not running" is the most likely
	// reason by far during development, and an error page cannot tell you that.
	try {
		const result: PagedResult<CallSummary> = await fetchCalls(fetch, params);
		return { params, result, error: null };
	} catch (cause) {
		return {
			params,
			result: null,
			error: cause instanceof Error ? cause.message : 'The call log could not be loaded.'
		};
	}
};
