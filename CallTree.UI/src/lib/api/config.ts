/**
 * Mirrors the settings models exposed by CallTree.Api's ConfigController.
 *
 * Two fields are write-only: the trunk password and the outbound PIN. Neither is ever returned by the
 * API, only reported as set or not. Sending `null` (or omitting them) leaves whatever is configured
 * alone, which is what lets this form save an unrelated field without ever holding either secret.
 */

export interface TelephonySettings {
	myCellNumber: string;
	didNumber: string;
	publicHost: string;
	sipListenPort: number;
	listenOnTcp: boolean;
	rtpPortStart: number;
	rtpPortEnd: number;
	traceSip: boolean;
	screeningDigit: number;
	screeningTimeoutSeconds: number;
	jitterBufferMilliseconds: number;
	recordingToneIntervalSeconds: number;
}

export interface TrunkSettings {
	host: string;
	port: number;
	username: string;
	authUsername: string | null;
	registrationExpirySeconds: number;
}

export interface SettingsResponse {
	telephony: TelephonySettings;
	trunk: TrunkSettings;
	trunkPasswordSet: boolean;
	/** Whether the outbound path is gated by a PIN. False means caller ID alone decides. */
	outboundPinSet: boolean;
	trunkConfigured: boolean;
	/** Startup-only settings that have changed since the SIP stack started. */
	pendingRestartKeys: string[];
	/** Every startup-only key, changed or not, so fields can be labelled before a save. */
	restartOnlyKeys: string[];
	/** Keys an environment variable is supplying, which no save can override. */
	environmentOverrides: string[];
	configFilePath: string;
	configFileExists: boolean;
}

export interface SettingsUpdate {
	telephony: TelephonySettings;
	trunk: TrunkSettings;
	trunkPassword?: string | null;
	/** Null leaves the PIN alone; an empty string turns the gate off. */
	outboundPin?: string | null;
}

/** Validation errors keyed by the property path the API reports, e.g. `Trunk.Port`. */
export type FieldErrors = Record<string, string[]>;

export class SettingsSaveError extends Error {
	readonly fieldErrors: FieldErrors;

	constructor(message: string, fieldErrors: FieldErrors = {}) {
		super(message);
		this.name = 'SettingsSaveError';
		this.fieldErrors = fieldErrors;
	}
}

export async function fetchSettings(fetchFn: typeof globalThis.fetch): Promise<SettingsResponse> {
	const response = await fetchFn('/api/config');

	if (!response.ok) {
		throw new Error(`The API returned ${response.status} ${response.statusText}.`);
	}

	return response.json();
}

export async function saveSettings(update: SettingsUpdate): Promise<SettingsResponse> {
	const response = await fetch('/api/config', {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify(update)
	});

	if (response.ok) {
		return response.json();
	}

	// ProblemDetails, either the validation shape (with `errors`) or a plain problem from a failed write.
	const problem = await response.json().catch(() => null);
	throw new SettingsSaveError(
		problem?.detail ??
			problem?.title ??
			`The API returned ${response.status} ${response.statusText}.`,
		problem?.errors ?? {}
	);
}

/**
 * The API names the same setting two ways: configuration keys use a colon (`Telephony:SipListenPort`,
 * which is also how you would spell it as an environment variable) and validation errors use the C#
 * property path with a dot. Normalising lets a field carry one key rather than two, and the
 * case-insensitivity means a casing slip cannot silently hide a validation message.
 */
function normalizeKey(key: string): string {
	return key.toLowerCase().replaceAll(':', '.');
}

export function errorsFor(errors: FieldErrors, key: string): string[] {
	const wanted = normalizeKey(key);
	for (const [candidate, messages] of Object.entries(errors)) {
		if (normalizeKey(candidate) === wanted) return messages;
	}
	return [];
}

export function includesKey(keys: string[], key: string): boolean {
	const wanted = normalizeKey(key);
	return keys.some((candidate) => normalizeKey(candidate) === wanted);
}
