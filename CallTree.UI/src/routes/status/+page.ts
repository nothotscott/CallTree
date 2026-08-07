import { fetchTelephonyStatus, type TelephonyStatus } from '$lib/api/telephony';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ fetch }) => {
	try {
		const status: TelephonyStatus = await fetchTelephonyStatus(fetch);
		return { status, error: null };
	} catch (cause) {
		return {
			status: null,
			error: cause instanceof Error ? cause.message : 'The telephony status could not be loaded.'
		};
	}
};
