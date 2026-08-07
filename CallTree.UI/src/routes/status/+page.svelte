<script lang="ts">
	import {
		fetchTelephonyStatus,
		REGISTRATION_LABELS,
		type TelephonyStatus,
		type TrunkRegistrationState
	} from '$lib/api/telephony';
	import { formatRelative, formatTimestamp } from '$lib/format';
	import { resolve } from '$app/paths';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	/** How often to re-poll. Registration changes are slow; this is about noticing, not measuring. */
	const REFRESH_MS = 5000;

	let status = $state<TelephonyStatus | null>(null);
	let error = $state<string | null>(null);
	/** Ticks with each poll so the "x ago" labels stay honest between fetches. */
	let now = $state(Date.now());

	$effect(() => {
		status = data.status;
		error = data.error;
	});

	$effect(() => {
		const timer = setInterval(async () => {
			try {
				status = await fetchTelephonyStatus(fetch);
				error = null;
			} catch (cause) {
				error =
					cause instanceof Error ? cause.message : 'The telephony status could not be loaded.';
			}
			now = Date.now();
		}, REFRESH_MS);

		return () => clearInterval(timer);
	});

	const stateStyles: Record<TrunkRegistrationState, string> = {
		Registered: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
		Registering: 'bg-sky-50 text-sky-800 ring-sky-200',
		TemporaryFailure: 'bg-amber-50 text-amber-800 ring-amber-200',
		Failed: 'bg-rose-50 text-rose-800 ring-rose-200',
		Removed: 'bg-orange-50 text-orange-800 ring-orange-200',
		NotConfigured: 'bg-slate-100 text-slate-700 ring-slate-200'
	};

	/**
	 * A registration can be perfectly healthy from our side while the trunk holds a LAN address it
	 * cannot route to — the failure that produces a busy tone and no log line at all. Worth its own
	 * warning rather than a field the reader has to interpret.
	 */
	const natWarning = $derived.by(() => {
		if (!status || status.registrationState === 'NotConfigured') return null;
		if (!status.contactHost) {
			return 'No public host is set, so the trunk was told to reach this instance at a LAN address. Inbound calls will not arrive, and nothing will be logged because they never get here.';
		}
		if (!status.sdpAddress) {
			return 'The public host did not resolve to an IPv4 address, so SDP advertises the LAN address. Calls will connect and then have no audio.';
		}
		return null;
	});
</script>

<svelte:head><title>Status · CallTree</title></svelte:head>

{#snippet row(label: string, value: string | null | undefined, hint?: string)}
	<div class="grid gap-1 py-2.5 sm:grid-cols-3 sm:gap-4">
		<dt class="text-sm font-medium text-slate-600">{label}</dt>
		<dd class="sm:col-span-2">
			<span class="text-sm break-all text-slate-900">{value ? value : '—'}</span>
			{#if hint}
				<span class="mt-0.5 block text-xs text-slate-500">{hint}</span>
			{/if}
		</dd>
	</div>
{/snippet}

<section class="max-w-3xl space-y-6">
	<header class="flex flex-wrap items-end justify-between gap-4">
		<div>
			<h1 class="text-2xl font-semibold text-slate-900">Telephony status</h1>
			<p class="mt-1 text-sm text-slate-500">
				The live state of the SIP stack. Refreshes every {REFRESH_MS / 1000} seconds.
			</p>
		</div>

		{#if status}
			<span
				class="inline-flex rounded-full px-3 py-1 text-sm font-medium ring-1 ring-inset {stateStyles[
					status.registrationState
				]}"
			>
				{REGISTRATION_LABELS[status.registrationState]}
			</span>
		{/if}
	</header>

	{#if error && !status}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The telephony status could not be loaded.</p>
			<p class="mt-1">{error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if status}
		{#if error}
			<!-- Keep showing the last good reading rather than blanking the page on one failed poll. -->
			<div class="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
				Last refresh failed ({error}) — showing the previous reading.
			</div>
		{/if}

		{#if status.registrationState === 'NotConfigured'}
			<div class="rounded-lg border border-slate-200 bg-white p-4 text-sm text-slate-700 shadow-sm">
				<p class="font-medium text-slate-900">Telephony is idle.</p>
				<p class="mt-1">
					No trunk host or username is set, so the SIP stack never started and no calls can arrive.
					Configure it on the
					<a href={resolve('/settings')} class="font-medium text-slate-900 underline"
						>settings page</a
					>, then restart the service.
				</p>
			</div>
		{/if}

		{#if status.registrationMessage}
			<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
				<p class="font-medium">The registrar said:</p>
				<p class="mt-1 font-mono text-xs break-all">{status.registrationMessage}</p>
			</div>
		{/if}

		{#if natWarning}
			<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
				<p class="font-medium">NAT is not configured.</p>
				<p class="mt-1">{natWarning}</p>
			</div>
		{/if}

		{#if status.pendingRestartKeys.length > 0}
			<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
				<p class="font-medium">Saved settings are waiting on a restart.</p>
				<p class="mt-1">
					The running stack is still using the old values for: {status.pendingRestartKeys.join(
						', '
					)}.
				</p>
			</div>
		{/if}

		{#if !status.didFilterActive && status.registrationState !== 'NotConfigured'}
			<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
				<p class="font-medium">No DID is set, so every INVITE is answered.</p>
				<p class="mt-1">
					An open SIP port is swept continuously for a PBX that will place international calls on
					someone else's behalf. Setting the DID rejects those before a call record exists.
				</p>
			</div>
		{/if}

		{#if status.promptsMissing.length > 0}
			<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
				<p class="font-medium">Prompts are missing: {status.promptsMissing.join(', ')}.</p>
				<p class="mt-1">
					Those IVR steps will play silence. The call still connects, so this looks like success
					right up until nobody hears the instruction.
				</p>
			</div>
		{/if}

		<div class="rounded-lg border border-slate-200 bg-white p-5 shadow-sm">
			<h2 class="text-sm font-semibold text-slate-900">Registration</h2>
			<dl class="mt-2 divide-y divide-slate-100">
				{@render row('Registrar', status.registrarServer)}
				{@render row('Address of record', status.registeredUri)}
				{@render row(
					'Binding held by registrar',
					status.registrarContact,
					'Echoed back in the 200 OK. This is where the trunk will send calls.'
				)}
				{@render row(
					'Last registered',
					status.lastRegisteredAt
						? `${formatTimestamp(status.lastRegisteredAt)} (${formatRelative(status.lastRegisteredAt, now)})`
						: null
				)}
				{@render row(
					'Last state change',
					status.registrationChangedAt
						? `${formatTimestamp(status.registrationChangedAt)} (${formatRelative(status.registrationChangedAt, now)})`
						: null
				)}
				{@render row(
					'Successful registrations',
					String(status.registrationCount),
					'Counts re-registrations too, so it climbs steadily on a healthy trunk.'
				)}
				{@render row('Expiry', status.expirySeconds > 0 ? `${status.expirySeconds}s` : null)}
			</dl>
		</div>

		<div class="rounded-lg border border-slate-200 bg-white p-5 shadow-sm">
			<h2 class="text-sm font-semibold text-slate-900">Transport</h2>
			<dl class="mt-2 divide-y divide-slate-100">
				{@render row('Started', status.startedAt ? formatTimestamp(status.startedAt) : null)}
				{@render row(
					'Listening on',
					status.listeningEndpoints.length > 0 ? status.listeningEndpoints.join(', ') : null
				)}
				{@render row(
					'Advertised in Contact',
					status.contactHost,
					'Blank means the LAN address, which the trunk cannot reach.'
				)}
				{@render row(
					'Advertised in SDP',
					status.sdpAddress,
					'Blank means the LAN address, which sends the audio nowhere.'
				)}
				{@render row('RTP ports', status.rtpPortRange)}
			</dl>
		</div>

		<div class="rounded-lg border border-slate-200 bg-white p-5 shadow-sm">
			<h2 class="text-sm font-semibold text-slate-900">Call handling</h2>
			<dl class="mt-2 divide-y divide-slate-100">
				{@render row(
					'DID filter',
					status.didFilterActive ? 'Active' : 'Off — every INVITE is answered'
				)}
				{@render row(
					'Own-number match',
					status.cellNumberConfigured ? 'Configured' : 'Not set — all calls classified Inbound'
				)}
				{@render row('SIP tracing', status.traceSipEnabled ? 'On' : 'Off')}
				{@render row('Prompts', status.promptsLoaded.join(', '), status.promptsRoot)}
			</dl>
		</div>
	{:else}
		<p class="text-sm text-slate-500">Loading status…</p>
	{/if}
</section>
