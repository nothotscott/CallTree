import { fetchRecording, type RecordingSummary } from '$lib/api/recordings';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ fetch, params }) => {
	// A failed fetch is returned rather than thrown, same reasoning as the calls list: "the backend is
	// not running" is the most likely cause during development, and an error page cannot tell you that.
	// A 404 is distinct from that - it means the id itself doesn't exist - so it's tracked separately.
	try {
		const recording: RecordingSummary | null = await fetchRecording(fetch, params.id);
		return { recording, error: null };
	} catch (cause) {
		return {
			recording: null,
			error: cause instanceof Error ? cause.message : 'The recording could not be loaded.'
		};
	}
};
