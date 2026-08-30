<script lang="ts">
	import {
		MAX_RECORDING_NAME_LENGTH,
		recordingAudioUrl,
		renameRecording,
		type ChannelLayout
	} from '$lib/api/recordings';
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

	/** What is in the box, and what the server last confirmed. The gap between them is "unsaved". */
	let draft = $state('');
	let savedName = $state('');
	let saving = $state(false);
	let saveError = $state<string | null>(null);

	// Seeded by an $effect rather than by initialising $state from `data`: initialising captures only
	// the first value, so coming back to a different recording would show the previous one's name.
	// Same reasoning as the settings page, and svelte-check warns about it (state_referenced_locally).
	$effect(() => {
		const current = recording?.name ?? '';
		savedName = current;
		draft = current;
		saveError = null;
	});

	const trimmed = $derived(draft.trim());
	const dirty = $derived(trimmed !== savedName);
	const valid = $derived(trimmed.length > 0 && trimmed.length <= MAX_RECORDING_NAME_LENGTH);

	async function save(event: SubmitEvent) {
		event.preventDefault();
		if (!recording || !dirty || !valid || saving) return;

		saving = true;
		saveError = null;
		try {
			const updated = await renameRecording(recording.id, trimmed);
			// Taken from the response rather than from `trimmed`: the server trims and is the authority
			// on what was actually stored.
			savedName = updated.name;
			draft = updated.name;
		} catch (cause) {
			saveError = cause instanceof Error ? cause.message : 'The name could not be saved.';
		} finally {
			saving = false;
		}
	}

	function revert() {
		draft = savedName;
		saveError = null;
	}

	function onKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape') {
			event.preventDefault();
			revert();
			(event.currentTarget as HTMLInputElement).blur();
		}
	}
</script>

<svelte:head><title>{savedName || 'Recording'} · CallTree</title></svelte:head>

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
			<!-- The visible title is an editable box, so the page still needs a heading with actual text
			     in it - an <h1> wrapping an input has none, and svelte-check says so. The box carries its
			     own label. -->
			<h1 class="sr-only">{savedName || 'Recording'}</h1>

			<!-- The name reads as a heading until you put the cursor in it, then it is a plain text box.
			     Enter saves, Escape puts back what was last saved. -->
			<form onsubmit={save} class="flex flex-wrap items-center gap-2">
				<div class="min-w-0 flex-1">
					<input
						bind:value={draft}
						onkeydown={onKeydown}
						aria-label="Recording name"
						maxlength={MAX_RECORDING_NAME_LENGTH}
						disabled={saving}
						class="-ml-2 w-full rounded-md border border-transparent bg-transparent px-2 py-1 text-2xl font-semibold text-slate-900 hover:border-slate-300 focus:border-slate-400 focus:bg-white focus:outline-none disabled:opacity-60"
					/>
				</div>

				{#if dirty}
					<button
						type="submit"
						disabled={!valid || saving}
						class="rounded-md bg-slate-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-slate-700 disabled:pointer-events-none disabled:opacity-40"
					>
						{saving ? 'Saving…' : 'Save'}
					</button>
					<button
						type="button"
						onclick={revert}
						disabled={saving}
						class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:pointer-events-none disabled:opacity-40"
					>
						Cancel
					</button>
				{/if}
			</form>

			{#if dirty && !valid}
				<p class="mt-1 text-sm text-amber-700">A recording needs a name.</p>
			{:else if saveError}
				<p class="mt-1 text-sm text-rose-700">{saveError}</p>
			{/if}

			<p class="mt-1 text-sm text-slate-500">
				<span class="text-slate-700">{recording.callSource}</span>
				call from <span class="text-slate-700">{callerLabel}</span>, started
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
