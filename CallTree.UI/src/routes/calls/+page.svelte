<script lang="ts">
	import {
		CALL_SOURCES,
		CALL_STATUSES,
		buildCallListQuery,
		type CallStatus,
		type CallSummary
	} from '$lib/api/calls';
	import { formatDuration, formatPhoneNumber, formatTimestamp } from '$lib/format';
	import { resolve } from '$app/paths';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	const result = $derived(data.result);
	const params = $derived(data.params);

	/** Colour carries meaning here: a screened-out call is the normal outcome for a spam probe. */
	const statusStyles: Record<CallStatus, string> = {
		Ringing: 'bg-slate-100 text-slate-700 ring-slate-200',
		Screening: 'bg-amber-50 text-amber-800 ring-amber-200',
		Dialing: 'bg-amber-50 text-amber-800 ring-amber-200',
		InProgress: 'bg-sky-50 text-sky-800 ring-sky-200',
		Completed: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
		ScreenedOut: 'bg-slate-100 text-slate-600 ring-slate-200',
		Missed: 'bg-orange-50 text-orange-800 ring-orange-200',
		Failed: 'bg-rose-50 text-rose-800 ring-rose-200'
	};

	/** Query string only. The path is resolved at the link itself, which is what lets
	 *  svelte/no-navigation-without-resolve see it. */
	function pageQuery(page: number): string {
		const search = buildCallListQuery({ ...params, page });
		return search.size > 0 ? `?${search}` : '';
	}

	function callerLabel(call: CallSummary): string {
		return formatPhoneNumber(call.remoteNumber) ?? call.rawCallerId ?? 'unknown';
	}

	const rangeStart = $derived(
		result && result.totalCount > 0 ? result.pageSize * (result.page - 1) + 1 : 0
	);
	const rangeEnd = $derived(result ? result.pageSize * (result.page - 1) + result.items.length : 0);
</script>

<svelte:head><title>Calls · CallTree</title></svelte:head>

<section class="space-y-6">
	<header class="flex flex-wrap items-end justify-between gap-4">
		<div>
			<h1 class="text-2xl font-semibold text-slate-900">Call log</h1>
			<p class="mt-1 text-sm text-slate-500">
				{#if result}
					{#if result.totalCount === 0}
						No calls match this filter.
					{:else}
						Showing {rangeStart}–{rangeEnd} of {result.totalCount.toLocaleString()} calls
					{/if}
				{:else}
					Could not reach the API.
				{/if}
			</p>
		</div>

		<!-- A plain GET form: the filter state lives in the URL, so a filtered view can be linked
		     and the browser's back button does the right thing without any client-side state. -->
		<form method="GET" action={resolve('/calls')} class="flex flex-wrap items-end gap-3">
			<label class="text-sm">
				<span class="mb-1 block font-medium text-slate-600">Source</span>
				<select
					name="source"
					value={params.source ?? ''}
					class="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-900 shadow-sm"
				>
					<option value="">All</option>
					{#each CALL_SOURCES as source (source)}
						<option value={source}>{source}</option>
					{/each}
				</select>
			</label>

			<label class="text-sm">
				<span class="mb-1 block font-medium text-slate-600">Status</span>
				<select
					name="status"
					value={params.status ?? ''}
					class="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-900 shadow-sm"
				>
					<option value="">All</option>
					{#each CALL_STATUSES as status (status)}
						<option value={status}>{status}</option>
					{/each}
				</select>
			</label>

			<button
				type="submit"
				class="rounded-md bg-slate-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-slate-700"
			>
				Apply
			</button>
		</form>
	</header>

	{#if data.error}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The call log could not be loaded.</p>
			<p class="mt-1">{data.error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if result}
		<div class="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
			<table class="w-full min-w-[52rem] border-collapse text-sm">
				<thead class="bg-slate-50 text-left text-xs tracking-wide text-slate-500 uppercase">
					<tr>
						<th scope="col" class="px-4 py-3 font-medium">Started</th>
						<th scope="col" class="px-4 py-3 font-medium">Caller</th>
						<th scope="col" class="px-4 py-3 font-medium">Source</th>
						<th scope="col" class="px-4 py-3 font-medium">Status</th>
						<th scope="col" class="px-4 py-3 text-right font-medium">Duration</th>
						<th scope="col" class="px-4 py-3 font-medium">Outcome</th>
						<th scope="col" class="px-4 py-3 font-medium">Recording</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-slate-100">
					{#each result.items as call (call.id)}
						<tr class="hover:bg-slate-50">
							<td class="px-4 py-3 whitespace-nowrap text-slate-700">
								{formatTimestamp(call.startedAt)}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span class="font-medium text-slate-900">{callerLabel(call)}</span>
								{#if !call.remoteNumber}
									<!-- The raw header did not parse as a number; scanners send junk here. -->
									<span class="ml-1 text-xs text-slate-400">(unparsed)</span>
								{/if}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span class="text-slate-700">{call.source}</span>
								{#if call.sourceClassification !== 'Default'}
									<span class="ml-1 text-xs text-slate-400">{call.sourceClassification}</span>
								{/if}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span
									class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset {statusStyles[
										call.status
									]}"
								>
									{call.status}
								</span>
							</td>
							<td class="px-4 py-3 text-right whitespace-nowrap text-slate-700 tabular-nums">
								{formatDuration(call.durationSeconds)}
							</td>
							<td
								class="max-w-xs truncate px-4 py-3 text-slate-500"
								title={call.terminationReason ?? ''}
							>
								{call.terminationReason ?? '—'}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								{#if call.recordingId}
									<a
										href={resolve('/recordings/[id]', { id: call.recordingId })}
										class="font-medium text-sky-700 hover:text-sky-900"
									>
										▶ Listen
									</a>
								{:else}
									<span class="text-slate-400">—</span>
								{/if}
							</td>
						</tr>
					{:else}
						<tr>
							<td colspan="7" class="px-4 py-10 text-center text-slate-500"> No calls to show. </td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>

		{#if result.totalPages > 1}
			<nav class="flex items-center justify-between" aria-label="Pagination">
				<a
					href="{resolve('/calls')}{pageQuery(result.page - 1)}"
					aria-disabled={!result.hasPreviousPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					← Newer
				</a>

				<span class="text-sm text-slate-500">
					Page {result.page.toLocaleString()} of {result.totalPages.toLocaleString()}
				</span>

				<a
					href="{resolve('/calls')}{pageQuery(result.page + 1)}"
					aria-disabled={!result.hasNextPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					Older →
				</a>
			</nav>
		{/if}
	{/if}
</section>
