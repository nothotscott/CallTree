/** Mirrors the telephony status exposed by CallTree.Api's TelephonyController. */

export const REGISTRATION_STATES = [
	'NotConfigured',
	'Registering',
	'Registered',
	'TemporaryFailure',
	'Failed',
	'Removed'
] as const;
export type TrunkRegistrationState = (typeof REGISTRATION_STATES)[number];

export interface TelephonyStatus {
	registrationState: TrunkRegistrationState;
	registrationMessage: string | null;
	registeredUri: string | null;
	/** The binding the registrar echoed back — the address it will actually dial. */
	registrarContact: string | null;
	registrarServer: string | null;
	registrationChangedAt: string | null;
	lastRegisteredAt: string | null;
	registrationCount: number;
	expirySeconds: number;
	startedAt: string | null;
	listeningEndpoints: string[];
	/** Null means the LAN address is being advertised, which breaks inbound calls behind NAT. */
	contactHost: string | null;
	/** Null means the LAN address is going into SDP, which breaks audio behind NAT. */
	sdpAddress: string | null;
	rtpPortRange: string | null;
	didFilterActive: boolean;
	cellNumberConfigured: boolean;
	traceSipEnabled: boolean;
	promptsRoot: string;
	promptsLoaded: string[];
	promptsMissing: string[];
	pendingRestartKeys: string[];
}

export async function fetchTelephonyStatus(
	fetchFn: typeof globalThis.fetch
): Promise<TelephonyStatus> {
	const response = await fetchFn('/api/telephony/status');

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

/** How a state should read to someone deciding whether to go and fix something. */
export const REGISTRATION_LABELS: Record<TrunkRegistrationState, string> = {
	NotConfigured: 'Not configured',
	Registering: 'Registering…',
	Registered: 'Registered',
	TemporaryFailure: 'Retrying',
	Failed: 'Failed',
	Removed: 'Removed'
};
