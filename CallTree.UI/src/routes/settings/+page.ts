import { fetchSettings, type SettingsResponse } from '$lib/api/config';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ fetch }) => {
	// As with the call log, a failed fetch is returned rather than thrown: "the backend is not
	// running" is the likeliest cause in development and an error page cannot say so.
	try {
		const settings: SettingsResponse = await fetchSettings(fetch);
		return { settings, error: null };
	} catch (cause) {
		return {
			settings: null,
			error: cause instanceof Error ? cause.message : 'The settings could not be loaded.'
		};
	}
};
