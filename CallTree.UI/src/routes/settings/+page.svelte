<script lang="ts">
	import {
		errorsFor,
		includesKey,
		saveSettings,
		SettingsSaveError,
		type FieldErrors,
		type SettingsResponse,
		type TelephonySettings,
		type TrunkSettings
	} from '$lib/api/config';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	/** The last thing the server said, and the baseline the form was populated from. */
	let settings = $state<SettingsResponse | null>(null);

	// Editable copies, so an abandoned edit never misrepresents what the backend is running.
	let telephony = $state<TelephonySettings | null>(null);
	let trunk = $state<TrunkSettings | null>(null);

	/** Empty means "leave the configured password alone"; it is never sent in that case. */
	let password = $state('');

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
		password = '';
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
		if (!telephony || !trunk) return;

		saving = true;
		saved = false;
		errors = {};
		saveError = null;

		try {
			const saveResult = await saveSettings({
				telephony,
				trunk,
				// Omitted unless one was typed. The API treats null as "unchanged", which is what keeps
				// a password set from user secrets or the environment from being blanked by a save.
				trunkPassword: password.length > 0 ? password : null
			});

			settings = saveResult;
			telephony = { ...saveResult.telephony };
			trunk = { ...saveResult.trunk };
			password = '';
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
			Telephony and trunk configuration. Saved to a file on the server, layered over the settings
			the image ships with and beneath anything set in the environment.
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
	{:else if settings && telephony && trunk}
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

					<label class="flex items-center gap-2 pt-6 text-sm">
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
