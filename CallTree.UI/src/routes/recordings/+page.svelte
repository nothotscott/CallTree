<script lang="ts">
	import { buildRecordingListQuery, type RecordingSummary } from '$lib/api/recordings';
	import { formatBytes, formatDuration, formatPhoneNumber, formatTimestamp } from '$lib/format';
	import { resolve } from '$app/paths';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	const result = $derived(data.result);
	const params = $derived(data.params);

	/** Query string only. The path is resolved at the link itself, which is what lets
	 *  svelte/no-navigation-without-resolve see it. */
	function pageQuery(page: number): string {
		const search = buildRecordingListQuery({ ...params, page });
		return search.size > 0 ? `?${search}` : '';
	}

	function callerLabel(recording: RecordingSummary): string {
		return formatPhoneNumber(recording.remoteNumber) ?? recording.rawCallerId ?? 'unknown';
	}

	const rangeStart = $derived(
		result && result.totalCount > 0 ? result.pageSize * (result.page - 1) + 1 : 0
	);
	const rangeEnd = $derived(result ? result.pageSize * (result.page - 1) + result.items.length : 0);
</script>

<svelte:head><title>Recordings · CallTree</title></svelte:head>

<section class="space-y-6">
	<header>
		<h1 class="text-2xl font-semibold text-slate-900">Recordings</h1>
		<p class="mt-1 text-sm text-slate-500">
			{#if result}
				{#if result.totalCount === 0}
					No recordings yet.
				{:else}
					Showing {rangeStart}–{rangeEnd} of {result.totalCount.toLocaleString()} recordings
				{/if}
			{:else}
				Could not reach the API.
			{/if}
		</p>
	</header>

	{#if data.error}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The recordings list could not be loaded.</p>
			<p class="mt-1">{data.error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if result}
		<div class="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
			<table class="w-full min-w-[48rem] border-collapse text-sm">
				<thead class="bg-slate-50 text-left text-xs tracking-wide text-slate-500 uppercase">
					<tr>
						<th scope="col" class="px-4 py-3 font-medium">Recorded</th>
						<th scope="col" class="px-4 py-3 font-medium">Caller</th>
						<th scope="col" class="px-4 py-3 font-medium">Source</th>
						<th scope="col" class="px-4 py-3 text-right font-medium">Duration</th>
						<th scope="col" class="px-4 py-3 text-right font-medium">Size</th>
						<th scope="col" class="px-4 py-3 font-medium">Status</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-slate-100">
					{#each result.items as recording (recording.id)}
						<tr class="relative hover:bg-slate-50">
							<td class="px-4 py-3 whitespace-nowrap text-slate-700">
								<!-- Stretched to the row via the <tr>'s `relative`, so the whole row is one
								     click target while staying a real link (keyboard, middle-click, etc). -->
								<a
									href={resolve('/recordings/[id]', { id: recording.id })}
									class="absolute inset-0"
									aria-label="View recording details"
								></a>
								{formatTimestamp(recording.createdAt)}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span class="font-medium text-slate-900">{callerLabel(recording)}</span>
								{#if !recording.remoteNumber}
									<!-- The raw header did not parse as a number; scanners send junk here. -->
									<span class="ml-1 text-xs text-slate-400">(unparsed)</span>
								{/if}
							</td>
							<td class="px-4 py-3 whitespace-nowrap text-slate-700">{recording.callSource}</td>
							<td class="px-4 py-3 text-right whitespace-nowrap text-slate-700 tabular-nums">
								{formatDuration(recording.durationSeconds)}
							</td>
							<td class="px-4 py-3 text-right whitespace-nowrap text-slate-700 tabular-nums">
								{formatBytes(recording.sizeBytes)}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								{#if recording.finalizedAt}
									<span
										class="inline-flex rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-800 ring-1 ring-emerald-200 ring-inset"
									>
										Finalized
									</span>
								{:else}
									<span
										class="inline-flex rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800 ring-1 ring-amber-200 ring-inset"
									>
										Incomplete
									</span>
								{/if}
							</td>
						</tr>
					{:else}
						<tr>
							<td colspan="6" class="px-4 py-10 text-center text-slate-500">
								No recordings to show.
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>

		{#if result.totalPages > 1}
			<nav class="flex items-center justify-between" aria-label="Pagination">
				<a
					href="{resolve('/recordings')}{pageQuery(result.page - 1)}"
					aria-disabled={!result.hasPreviousPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					← Newer
				</a>

				<span class="text-sm text-slate-500">
					Page {result.page.toLocaleString()} of {result.totalPages.toLocaleString()}
				</span>

				<a
					href="{resolve('/recordings')}{pageQuery(result.page + 1)}"
					aria-disabled={!result.hasNextPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					Older →
				</a>
			</nav>
		{/if}
	{/if}
</section>
