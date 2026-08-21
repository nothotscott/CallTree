<script lang="ts">
	import { recordingAudioUrl, type ChannelLayout } from '$lib/api/recordings';
	import { formatBytes, formatDuration, formatPhoneNumber, formatTimestamp } from '$lib/format';
	import { resolve } from '$app/paths';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	const recording = $derived(data.recording);

	const channelLayoutLabels: Record<ChannelLayout, string> = {
		Mono: 'Mono',
		StereoPerLeg: 'Stereo (one channel per leg)'
	};

	const callerLabel = $derived(
		recording
			? (formatPhoneNumber(recording.remoteNumber) ?? recording.rawCallerId ?? 'unknown')
			: ''
	);
</script>

<svelte:head><title>Recording · CallTree</title></svelte:head>

<section class="space-y-6">
	<a href={resolve('/recordings')} class="text-sm text-slate-500 hover:text-slate-900">
		← Back to recordings
	</a>

	{#if data.error}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The recording could not be loaded.</p>
			<p class="mt-1">{data.error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if !recording}
		<div
			class="rounded-lg border border-slate-200 bg-white p-8 text-center text-slate-500 shadow-sm"
		>
			No recording has this id.
		</div>
	{:else}
		<header>
			<h1 class="text-2xl font-semibold text-slate-900">{callerLabel}</h1>
			<p class="mt-1 text-sm text-slate-500">
				<span class="text-slate-700">{recording.callSource}</span> call started
				{formatTimestamp(recording.callStartedAt)}
				{#if !recording.remoteNumber}
					<span class="ml-1 text-xs text-slate-400">(caller ID unparsed)</span>
				{/if}
			</p>
		</header>

		<div class="rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
			{#if recording.finalizedAt}
				<audio controls preload="none" class="w-full" src={recordingAudioUrl(recording.id)}>
					Your browser cannot play this recording. The file is at {recordingAudioUrl(recording.id)}.
				</audio>
			{:else}
				<div
					class="flex items-center gap-2 rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800 ring-1 ring-amber-200 ring-inset"
				>
					Recording still in progress — playback will be available once the call ends.
				</div>
			{/if}
		</div>

		<dl
			class="grid grid-cols-1 gap-x-6 gap-y-4 rounded-lg border border-slate-200 bg-white p-6 text-sm shadow-sm sm:grid-cols-2 lg:grid-cols-3"
		>
			<div>
				<dt class="text-xs tracking-wide text-slate-500 uppercase">Recorded</dt>
				<dd class="mt-1 text-slate-900">{formatTimestamp(recording.createdAt)}</dd>
			</div>
			<div>
				<dt class="text-xs tracking-wide text-slate-500 uppercase">Finalized</dt>
				<dd class="mt-1 text-slate-900">
					{recording.finalizedAt ? formatTimestamp(recording.finalizedAt) : 'Not yet'}
				</dd>
			</div>
			<div>
				<dt class="text-xs tracking-wide text-slate-500 uppercase">Duration</dt>
				<dd class="mt-1 text-slate-900 tabular-nums">
					{formatDuration(recording.durationSeconds)}
				</dd>
			</div>
			<div>
				<dt class="text-xs tracking-wide text-slate-500 uppercase">Size</dt>
				<dd class="mt-1 text-slate-900 tabular-nums">{formatBytes(recording.sizeBytes)}</dd>
			</div>
			<div>
				<dt class="text-xs tracking-wide text-slate-500 uppercase">Channels</dt>
				<dd class="mt-1 text-slate-900">{channelLayoutLabels[recording.channelLayout]}</dd>
			</div>
		</dl>
	{/if}
</section>
