<script lang="ts">
	import {
		errorsFor,
		includesKey,
		saveSettings,
		SettingsSaveError,
		type FieldErrors,
		type MessagingSettings,
		type SettingsResponse,
		type TelephonySettings,
		type TrunkSettings
	} from '$lib/api/config';
	import { messagingCapability } from '$lib/messaging.svelte';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	/** The last thing the server said, and the baseline the form was populated from. */
	let settings = $state<SettingsResponse | null>(null);

	// Editable copies, so an abandoned edit never misrepresents what the backend is running.
	let telephony = $state<TelephonySettings | null>(null);
	let trunk = $state<TrunkSettings | null>(null);
	let messaging = $state<MessagingSettings | null>(null);

	/** Empty means "leave the configured password alone"; it is never sent in that case. */
	let password = $state('');

	// The messaging API key needs the switch as well as the box, for the PIN's reason rather than the
	// trunk password's: blank has to keep meaning "unchanged", so an empty string is the only way to say
	// "remove the key", and there is no way to type that. Unchecking is not the same as unchecking
	// "Enable SMS" - that turns the webhook off and stops messages arriving at all, where this leaves a
	// receive-only line that still records everything texted to the DID. That is the only way a US long
	// code can be run before it is 10DLC-registered, so it has to be reachable from here.
	let sendingEnabled = $state(false);
	let apiKey = $state('');
	let apiKeyError = $state<string | null>(null);

	// The PIN needs a switch as well as a box, because blank has to keep meaning "unchanged" — without
	// the switch there would be no way to express "turn the gate off" at all.
	let pinRequired = $state(false);
	let pin = $state('');
	let pinError = $state<string | null>(null);

	let saving = $state(false);
	let saved = $state(false);
	let errors = $state<FieldErrors>({});
	let saveError = $state<string | null>(null);

	// Adopt whatever the load function produced, including on a later visit: initialising the form
	// once would leave it showing values the server had moved on from. In-progress edits are
	// deliberately discarded at that point, since a fresh load means fresh truth.
	$effect(() => {
		adopt(data.settings);
	});

	function adopt(next: SettingsResponse | null) {
		settings = next;
		telephony = next ? { ...next.telephony } : null;
		trunk = next ? { ...next.trunk } : null;
		messaging = next ? { ...next.messaging } : null;
		password = '';
		sendingEnabled = next?.messagingApiKeySet ?? false;
		apiKey = '';
		apiKeyError = null;
		pinRequired = next?.outboundPinSet ?? false;
		pin = '';
		pinError = null;
		errors = {};
		saveError = null;
		saved = false;
	}

	const restartOnly = $derived(settings?.restartOnlyKeys ?? []);
	const overrides = $derived(settings?.environmentOverrides ?? []);
	const pending = $derived(settings?.pendingRestartKeys ?? []);

	const inputClass =
		'mt-1 w-full rounded-md border border-slate-300 bg-white px-2.5 py-1.5 text-sm text-slate-900 shadow-sm disabled:bg-slate-100 disabled:text-slate-500';

	async function save(event: SubmitEvent) {
		event.preventDefault();
		if (!telephony || !trunk || !messaging) return;

		// Asking for a PIN without ever supplying one would save nothing and leave the switch claiming
		// a gate that is not there - worse than refusing, because the operator would believe it.
		pinError =
			pinRequired && !settings?.outboundPinSet && pin.length === 0
				? 'Enter the PIN you want to require.'
				: null;

		// Same trap on the API key: asking to send with no key to send with would save nothing and leave
		// the switch claiming an ability the line does not have.
		apiKeyError =
			sendingEnabled && !settings?.messagingApiKeySet && apiKey.length === 0
				? 'Enter the API key to send with, or turn sending off to run receive-only.'
				: null;

		if (pinError || apiKeyError) return;

		saving = true;
		saved = false;
		errors = {};
		saveError = null;

		// Null leaves the PIN alone; an empty string is the only way to say "remove it".
		const sentPin = pinRequired ? (pin.length > 0 ? pin : null) : '';

		// And the same for the API key, which is what makes the line receive-only.
		const sentApiKey = sendingEnabled ? (apiKey.length > 0 ? apiKey : null) : '';

		try {
			const saveResult = await saveSettings({
				telephony,
				trunk,
				messaging,
				// Omitted unless one was typed. The API treats null as "unchanged", which is what keeps
				// a password set from user secrets or the environment from being blanked by a save.
				trunkPassword: password.length > 0 ? password : null,
				outboundPin: sentPin,
				messagingApiKey: sentApiKey
			});

			settings = saveResult;
			telephony = { ...saveResult.telephony };
			trunk = { ...saveResult.trunk };
			messaging = { ...saveResult.messaging };
			password = '';
			pin = '';
			apiKey = '';

			// Set from what was sent, not from the response. When the PIN was not part of this save the
			// response can still describe the configuration as it was before it — the file the API just
			// wrote is reloaded asynchronously — and adopting that would flip the switch off on its own.
			// The next save would then send an empty PIN and genuinely remove the gate.
			if (sentPin !== null) pinRequired = sentPin.length > 0;
			if (sentApiKey !== null) sendingEnabled = sentApiKey.length > 0;

			// Tell the rest of the app, from the response rather than by re-reading the API, for the same
			// asynchronous-reload reason. This is what makes the Messages link appear the moment SMS is
			// switched on, and the relay columns appear the moment a key is added.
			messagingCapability.set({
				enabled: saveResult.messaging.enabled,
				canSend: saveResult.messaging.enabled && saveResult.messagingApiKeySet
			});

			saved = true;
		} catch (cause) {
			if (cause instanceof SettingsSaveError) {
				errors = cause.fieldErrors;
				saveError = Object.keys(cause.fieldErrors).length > 0 ? null : cause.message;
			} else {
				saveError = cause instanceof Error ? cause.message : 'The settings could not be saved.';
			}
		} finally {
			saving = false;
		}
	}
</script>

<svelte:head><title>Settings · CallTree</title></svelte:head>

{#snippet notes(key: string, hint: string)}
	<span class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-500">
		<span>{hint}</span>
		{#if includesKey(restartOnly, key)}
			<span class="rounded bg-slate-100 px-1.5 py-0.5 font-medium text-slate-600">
				restart to apply
			</span>
		{/if}
		{#if includesKey(overrides, key)}
			<span class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800">
				set by {key.replaceAll(':', '__')}
			</span>
		{/if}
	</span>
	{#each errorsFor(errors, key) as message (message)}
		<span class="mt-1 block text-xs font-medium text-rose-600">{message}</span>
	{/each}
{/snippet}

<section class="max-w-3xl space-y-6">
	<header>
		<h1 class="text-2xl font-semibold text-slate-900">Settings</h1>
		<p class="mt-1 text-sm text-slate-500">
			Telephony, trunk and messaging configuration. Saved to a file on the server, layered over the
			settings the image ships with and beneath anything set in the environment.
		</p>
	</header>

	{#if data.error}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The settings could not be loaded.</p>
			<p class="mt-1">{data.error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if settings && telephony && trunk && messaging}
		{#if pending.length > 0}
			<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
				<p class="font-medium">Restart the service to apply these.</p>
				<p class="mt-1">
					They have been saved, but the running SIP stack bound its sockets and registered with the
					trunk using the old values: {pending.join(', ')}.
				</p>
			</div>
		{/if}

		{#if overrides.length > 0}
			<div class="rounded-lg border border-slate-200 bg-slate-50 p-4 text-sm text-slate-700">
				<p class="font-medium">Some settings come from the environment.</p>
				<p class="mt-1">
					The environment sits above this file, so saving these changes nothing until the variable
					is removed: {overrides.join(', ')}.
				</p>
			</div>
		{/if}

		{#if !settings.trunkConfigured}
			<div class="rounded-lg border border-slate-200 bg-white p-4 text-sm text-slate-700 shadow-sm">
				<p class="font-medium text-slate-900">Telephony is idle.</p>
				<p class="mt-1">
					Without a trunk host and username the SIP stack does not register, and no calls arrive.
				</p>
			</div>
		{/if}

		<form onsubmit={save} class="space-y-6">
			<fieldset
				disabled={saving}
				class="space-y-4 rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
			>
				<legend class="px-1 text-sm font-semibold text-slate-900">Telephony</legend>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">My cell number</span>
					<input
						bind:value={telephony.myCellNumber}
						class={inputClass}
						placeholder="+15550001111"
					/>
					{@render notes(
						'Telephony:MyCellNumber',
						'Calls whose caller ID matches this are treated as your own. Blank disables the match.'
					)}
				</label>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">DID number</span>
					<input bind:value={telephony.didNumber} class={inputClass} placeholder="+15550002222" />
					{@render notes(
						'Telephony:DidNumber',
						'The number this instance owns. Calls to anything else are rejected before a record is created; leaving it blank answers every dial-plan probe that reaches the port.'
					)}
				</label>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">Public host</span>
					<input
						bind:value={telephony.publicHost}
						class={inputClass}
						placeholder="pbx.example.com"
					/>
					{@render notes(
						'Telephony:PublicHost',
						'Public IP or hostname as seen from the internet. Required behind NAT: without it the trunk is told to reach a LAN address and inbound calls never arrive.'
					)}
				</label>

				<div class="grid gap-4 sm:grid-cols-2">
					<label class="block text-sm">
						<span class="font-medium text-slate-700">SIP port</span>
						<input type="number" bind:value={telephony.sipListenPort} class={inputClass} />
						{@render notes('Telephony:SipListenPort', 'Must match the router port forward.')}
					</label>

					<label class="flex gap-2 pt-6 text-sm">
						<input type="checkbox" bind:checked={telephony.listenOnTcp} class="rounded" />
						<span class="font-medium text-slate-700">Also listen on TCP</span>
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">RTP port range start</span>
						<input type="number" bind:value={telephony.rtpPortStart} class={inputClass} />
						{@render notes('Telephony:RtpPortStart', 'Keep narrow and matched to the forward.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">RTP port range end</span>
						<input type="number" bind:value={telephony.rtpPortEnd} class={inputClass} />
						{@render notes('Telephony:RtpPortEnd', 'Must not be below the start.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Screening digit</span>
						<input type="number" bind:value={telephony.screeningDigit} class={inputClass} />
						{@render notes(
							'Telephony:ScreeningDigit',
							'The key an unknown caller must press to get through.'
						)}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Screening timeout (seconds)</span>
						<input
							type="number"
							bind:value={telephony.screeningTimeoutSeconds}
							class={inputClass}
						/>
						{@render notes('Telephony:ScreeningTimeoutSeconds', 'How long to wait for that key.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Dial timeout (seconds)</span>
						<input type="number" bind:value={telephony.dialTimeoutSeconds} class={inputClass} />
						{@render notes(
							'Telephony:DialTimeoutSeconds',
							'How long to let your mobile ring before giving up and telling the caller nobody answered.'
						)}
					</label>
				</div>

				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={telephony.traceSip} class="mt-0.5 rounded" />
					<span>
						<span class="font-medium text-slate-700">Log every SIP message</span>
						{@render notes(
							'Telephony:TraceSip',
							'Very noisy, and it takes effect immediately - it can be turned on while a call is misbehaving without restarting and losing the registration.'
						)}
					</span>
				</label>
			</fieldset>

			<fieldset
				disabled={saving}
				class="space-y-4 rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
			>
				<legend class="px-1 text-sm font-semibold text-slate-900">Recording</legend>
				<p class="text-xs text-slate-500">
					Applies to calls from your own number, which are answered automatically and recorded. You
					add the other party with your phone’s three-way merge, so they never hear the spoken
					notice — telling them is on you, unless you turn on the tone below.
				</p>

				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={pinRequired} class="mt-0.5 rounded" />
					<span>
						<span class="font-medium text-slate-700">Require a PIN</span>
						{@render notes(
							'Telephony:OutboundPin',
							'Caller ID alone is trivially spoofable, and this path answers and records without asking. Turning this off saves an empty PIN, which removes the gate.'
						)}
					</span>
				</label>

				{#if pinRequired}
					<label class="block text-sm">
						<span class="font-medium text-slate-700">PIN</span>
						<input
							type="password"
							inputmode="numeric"
							bind:value={pin}
							class={inputClass}
							autocomplete="new-password"
							placeholder={settings.outboundPinSet ? 'unchanged' : 'digits only'}
						/>
						<span class="mt-1 block text-xs text-slate-500">
							Write-only, like the trunk password: the current value is never sent to this page.
							Keyed in on the phone, so digits only; end with # if it is shorter than expected.
						</span>
						{#if pinError}
							<span class="mt-1 block text-xs font-medium text-rose-600">{pinError}</span>
						{/if}
					</label>
				{/if}

				<div class="grid gap-4 sm:grid-cols-2">
					<label class="block text-sm">
						<span class="font-medium text-slate-700">Recording tone interval (seconds)</span>
						<input
							type="number"
							bind:value={telephony.recordingToneIntervalSeconds}
							class={inputClass}
						/>
						{@render notes(
							'Telephony:RecordingToneIntervalSeconds',
							'0 for none. The only notice a merged-in party hears; consent law varies and several places need every party to agree.'
						)}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Jitter buffer (ms)</span>
						<input
							type="number"
							bind:value={telephony.jitterBufferMilliseconds}
							class={inputClass}
						/>
						{@render notes(
							'Telephony:JitterBufferMilliseconds',
							'How long received audio is held so out-of-order packets can be put right. Delays the file, not the call.'
						)}
					</label>
				</div>
			</fieldset>

			<fieldset
				disabled={saving}
				class="space-y-4 rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
			>
				<legend class="px-1 text-sm font-semibold text-slate-900">Trunk</legend>
				<p class="text-xs text-slate-500">
					Every trunk setting is read once, when the service registers. Changes here always need a
					restart — and only one instance may hold the registration, so stop the old one first.
				</p>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">Host</span>
					<input bind:value={trunk.host} class={inputClass} placeholder="sip.provider.example" />
					{@render notes('Trunk:Host', 'The provider’s SIP hostname.')}
				</label>

				<div class="grid gap-4 sm:grid-cols-2">
					<label class="block text-sm">
						<span class="font-medium text-slate-700">Port</span>
						<input type="number" bind:value={trunk.port} class={inputClass} />
						{@render notes('Trunk:Port', 'Usually 5060.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Registration expiry (seconds)</span>
						<input type="number" bind:value={trunk.registrationExpirySeconds} class={inputClass} />
						{@render notes('Trunk:RegistrationExpirySeconds', 'How often to re-register.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Username</span>
						<input bind:value={trunk.username} class={inputClass} autocomplete="username" />
						{@render notes('Trunk:Username', 'The SIP username for the trunk.')}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">Auth username</span>
						<input
							bind:value={trunk.authUsername}
							class={inputClass}
							placeholder="(same as above)"
						/>
						{@render notes(
							'Trunk:AuthUsername',
							'Only for providers that split these. Not honoured yet - it warns and registers as the username above.'
						)}
					</label>
				</div>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">Password</span>
					<input
						type="password"
						bind:value={password}
						class={inputClass}
						autocomplete="new-password"
						placeholder={settings.trunkPasswordSet ? 'unchanged' : 'not set'}
					/>
					{@render notes(
						'Trunk:Password',
						'Write-only: the current value is never sent to this page. Leave blank to keep it.'
					)}
				</label>
			</fieldset>

			<fieldset
				disabled={saving}
				class="space-y-4 rounded-lg border border-slate-200 bg-white p-5 shadow-sm"
			>
				<legend class="px-1 text-sm font-semibold text-slate-900">Messaging (SMS)</legend>
				<p class="text-xs text-slate-500">
					{#if sendingEnabled}
						Texts to your DID are forwarded to your mobile. To send one, text the DID from your
						mobile as
						<code class="rounded bg-slate-100 px-1 py-0.5">3055551234 Your message here</code> — the number
						is read off the front and the rest is sent from the DID.
					{:else}
						Texts to your DID are recorded and readable on the Messages page. Nothing is sent on.
					{/if}
					Everything here applies immediately; none of it needs a restart.
				</p>

				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={messaging.enabled} class="mt-0.5 rounded" />
					<span>
						<span class="font-medium text-slate-700">Enable SMS</span>
						{@render notes(
							'Messaging:Enabled',
							'Off means the webhook answers 404 and nothing is ever sent, whatever else is set here.'
						)}
					</span>
				</label>

				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={sendingEnabled} class="mt-0.5 rounded" />
					<span>
						<span class="font-medium text-slate-700">Send as well as receive</span>
						<span class="mt-0.5 block text-xs text-slate-500">
							Off makes this a receive-only line: texts to the DID are recorded here and nothing is
							ever sent — no forward to your mobile, no
							<code class="rounded bg-slate-100 px-1 py-0.5">{'{number} body'}</code> commands, no failure
							notices. That is the only way to run a US long code that is not 10DLC-registered, since
							the carrier refuses everything it sends.
						</span>
						<!-- The badge that normally says this lives on the API key field, which is hidden the
						     moment this is unchecked - exactly when the operator needs to be told that
						     unchecking will not take effect. The environment sits above the config file, so
						     saving a cleared key here changes nothing while that variable is set. -->
						{#if !sendingEnabled && includesKey(overrides, 'Messaging:ApiKey')}
							<span
								class="mt-1 block rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-800"
							>
								Messaging__ApiKey is set in the environment, which overrides the config file.
								Turning sending off here will not take effect until that variable is removed.
							</span>
						{/if}
					</span>
				</label>

				{#if sendingEnabled}
					<label class="block text-sm">
						<span class="font-medium text-slate-700">API key</span>
						<input
							type="password"
							bind:value={apiKey}
							class={inputClass}
							autocomplete="new-password"
							placeholder={settings.messagingApiKeySet ? 'unchanged' : 'not set'}
						/>
						{@render notes(
							'Messaging:ApiKey',
							'Write-only, like the trunk password: the current value is never sent to this page.'
						)}
						{#if apiKeyError}
							<span class="mt-1 block text-xs text-rose-700">{apiKeyError}</span>
						{/if}
					</label>
				{/if}

				<label class="block text-sm">
					<span class="font-medium text-slate-700">Webhook public key</span>
					<input
						bind:value={messaging.publicKey}
						class={inputClass}
						placeholder="base64, 32 bytes"
					/>
					{@render notes(
						'Messaging:PublicKey',
						'The Ed25519 key from the provider portal. This is a public key, so it is shown in full - it is what proves a webhook really came from the provider.'
					)}
				</label>

				<label class="block text-sm">
					<span class="font-medium text-slate-700">Messaging profile ID</span>
					<input
						bind:value={messaging.messagingProfileId}
						class={inputClass}
						placeholder="(optional)"
					/>
					{@render notes(
						'Messaging:MessagingProfileId',
						'Only needed when the DID belongs to more than one profile; the number alone routes a send.'
					)}
				</label>

				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={messaging.requireSignature} class="mt-0.5 rounded" />
					<span>
						<span class="font-medium text-slate-700">Require a signed webhook</span>
						{@render notes(
							'Messaging:RequireSignature',
							'Leave this on. The webhook URL is public, nothing else authenticates it, and reaching it is enough to make this instance send a text at your expense.'
						)}
					</span>
				</label>

				<!-- Nothing to notify about on a receive-only line, and the notice would need the very key
				     that is missing to go anywhere. -->
				{#if sendingEnabled}
					<label class="flex items-start gap-2 text-sm">
						<input
							type="checkbox"
							bind:checked={messaging.notifyOnFailure}
							class="mt-0.5 rounded"
						/>
						<span>
							<span class="font-medium text-slate-700">Text me when a send fails</span>
							{@render notes(
								'Messaging:NotifyOnFailure',
								'The phone has no other channel: without this, a mistyped number fails silently and only this site says otherwise. Successful sends are never acknowledged.'
							)}
						</span>
					</label>
				{/if}

				<div class="grid gap-4 sm:grid-cols-2">
					<label class="block text-sm">
						<span class="font-medium text-slate-700">Signature tolerance (seconds)</span>
						<input
							type="number"
							bind:value={messaging.signatureToleranceSeconds}
							class={inputClass}
						/>
						{@render notes(
							'Messaging:SignatureToleranceSeconds',
							'How out of date a signed webhook may be, which bounds how long a captured one stays replayable.'
						)}
					</label>

					<label class="block text-sm">
						<span class="font-medium text-slate-700">API timeout (seconds)</span>
						<input type="number" bind:value={messaging.apiTimeoutSeconds} class={inputClass} />
						{@render notes(
							'Messaging:ApiTimeoutSeconds',
							'The send happens inside the webhook request, so keep this short - a provider that stops answering must not hold that request open.'
						)}
					</label>
				</div>

				{#if messaging.enabled && !messaging.requireSignature}
					<p
						class="rounded-md bg-rose-50 p-3 text-xs text-rose-900 ring-1 ring-rose-200 ring-inset"
					>
						Signature checking is off. Anyone who finds the webhook URL can make this instance send
						a text. Turn it back on as soon as the public key is in place.
					</p>
				{/if}
			</fieldset>

			<div class="flex flex-wrap items-center gap-4">
				<button
					type="submit"
					disabled={saving}
					class="rounded-md bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-700 disabled:opacity-50"
				>
					{saving ? 'Saving…' : 'Save settings'}
				</button>

				{#if saved}
					<span class="text-sm font-medium text-emerald-700">Saved.</span>
				{/if}
				{#if saveError}
					<span class="text-sm font-medium text-rose-700">{saveError}</span>
				{/if}
				{#if Object.keys(errors).length > 0}
					<span class="text-sm font-medium text-rose-700">
						Nothing was saved — see the messages above.
					</span>
				{/if}
			</div>

			<p class="text-xs text-slate-500">
				Written to <code class="rounded bg-slate-100 px-1 py-0.5">{settings.configFilePath}</code>
				on the server. It holds the trunk password in plain text; keep it with the recordings.
			</p>
		</form>
	{:else}
		<!-- The form is populated by an effect, so there is one tick before it exists. -->
		<p class="text-sm text-slate-500">Loading settings…</p>
	{/if}
</section>
