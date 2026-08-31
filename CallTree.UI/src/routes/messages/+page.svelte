<script lang="ts">
	import {
		MESSAGE_SOURCES,
		buildMessageListQuery,
		statusesFor,
		type MessageStatus,
		type MessageSummary,
		type RelayDelivery
	} from '$lib/api/messages';
	import { messagingCapability } from '$lib/messaging.svelte';
	import { formatDateParts, formatPhoneNumber } from '$lib/format';
	import { resolve } from '$app/paths';
	import type { PageProps } from './$types';

	let { data }: PageProps = $props();

	const result = $derived(data.result);
	const params = $derived(data.params);

	// Whether this line can send decides half of this page. Without an API key nothing is ever relayed,
	// so every column and filter about relaying is dead space — and there is a lot of it.
	const canSend = $derived(messagingCapability.canSend);
	const columnCount = $derived(canSend ? 5 : 4);

	/**
	 * Colour carries meaning, and the meanings are not simply good and bad: Recorded is the ordinary end
	 * of the line on a receive-only number, Rejected is a command CallTree could not read, and Failed is
	 * the provider saying no.
	 */
	const statusStyles: Record<MessageStatus, string> = {
		Received: 'bg-slate-100 text-slate-700 ring-slate-200',
		Recorded: 'bg-sky-50 text-sky-800 ring-sky-200',
		Relaying: 'bg-amber-50 text-amber-800 ring-amber-200',
		Relayed: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
		Rejected: 'bg-slate-100 text-slate-600 ring-slate-200',
		Failed: 'bg-rose-50 text-rose-800 ring-rose-200'
	};

	const deliveryStyles: Record<RelayDelivery, string> = {
		Queued: 'text-slate-500',
		Sent: 'text-slate-500',
		Delivered: 'text-emerald-700',
		Unconfirmed: 'text-amber-700',
		Failed: 'text-rose-700'
	};

	/** Query string only. The path is resolved at the link itself, which is what lets
	 *  svelte/no-navigation-without-resolve see it. */
	function pageQuery(page: number): string {
		const search = buildMessageListQuery({ ...params, page });
		return search.size > 0 ? `?${search}` : '';
	}

	function label(e164: string | null): string {
		return formatPhoneNumber(e164) ?? e164 ?? 'unknown';
	}

	/**
	 * Who the message was sent on to, and nothing else.
	 *
	 * It used to fall back to the failure reason, which put the same sentence on the row twice — the
	 * Status cell already carries it — and, because this column does not wrap, one long reason stretched
	 * the table past the viewport and squeezed the message body down to two words a line. A dash is the
	 * honest answer here: nothing was relayed, and why is the next column's job.
	 */
	function outcome(message: MessageSummary): string {
		return message.relayRecipient ? `to ${label(message.relayRecipient)}` : '—';
	}

	const rangeStart = $derived(
		result && result.totalCount > 0 ? result.pageSize * (result.page - 1) + 1 : 0
	);
	const rangeEnd = $derived(result ? result.pageSize * (result.page - 1) + result.items.length : 0);
</script>

<svelte:head><title>Messages · CallTree</title></svelte:head>

<section class="space-y-6">
	<header class="flex flex-wrap items-end justify-between gap-4">
		<div>
			<h1 class="text-2xl font-semibold text-slate-900">Messages</h1>
			<p class="mt-1 text-sm text-slate-500">
				{#if result}
					{#if result.totalCount === 0}
						No messages match this filter.
					{:else}
						Showing {rangeStart}–{rangeEnd} of {result.totalCount.toLocaleString()} messages
					{/if}
				{:else}
					Could not reach the API.
				{/if}
			</p>
		</div>

		<!-- A plain GET form: the filter state lives in the URL, so a filtered view can be linked
		     and the browser's back button does the right thing without any client-side state. -->
		<form method="GET" action={resolve('/messages')} class="flex flex-wrap items-end gap-3">
			<label class="text-sm">
				<span class="mb-1 block font-medium text-slate-600">Search</span>
				<input
					name="search"
					value={params.search ?? ''}
					placeholder="message text"
					class="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-900 shadow-sm"
				/>
			</label>

			<!-- Both directions only exist on a line that can send. On a receive-only one every row is
			     Inbound, so the filter would be a control with one useful setting. -->
			{#if canSend}
				<label class="text-sm">
					<span class="mb-1 block font-medium text-slate-600">Source</span>
					<select
						name="source"
						value={params.source ?? ''}
						class="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-900 shadow-sm"
					>
						<option value="">All</option>
						{#each MESSAGE_SOURCES as source (source)}
							<option value={source}>{source}</option>
						{/each}
					</select>
				</label>
			{/if}

			<label class="text-sm">
				<span class="mb-1 block font-medium text-slate-600">Status</span>
				<select
					name="status"
					value={params.status ?? ''}
					class="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-900 shadow-sm"
				>
					<option value="">All</option>
					{#each statusesFor(canSend) as status (status)}
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

	<!-- Said once, at the top, instead of as a reason repeated on every single row. -->
	{#if !messagingCapability.enabled}
		<p class="rounded-lg border border-slate-200 bg-slate-50 p-4 text-sm text-slate-600">
			SMS is switched off, so nothing new will arrive here. Anything below is from when it was on.
			Turn it back on under Messaging on the
			<a href={resolve('/settings')} class="font-medium text-slate-900 underline">settings page</a>.
		</p>
	{:else if !canSend}
		<p class="rounded-lg border border-sky-200 bg-sky-50 p-4 text-sm text-sky-900">
			<span class="font-medium">This line receives only.</span> Texts to your DID are recorded here
			and nothing is sent on: no forward to your mobile, and no
			<code class="rounded bg-sky-100 px-1 py-0.5">&#123;number&#125; body</code> commands. Add a
			messaging API key on the
			<a href={resolve('/settings')} class="font-medium underline">settings page</a> to start relaying.
		</p>
	{/if}

	{#if data.error}
		<div class="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-900">
			<p class="font-medium">The message log could not be loaded.</p>
			<p class="mt-1">{data.error}</p>
			<p class="mt-2 text-rose-800">
				Check that the backend is running:
				<code class="rounded bg-rose-100 px-1 py-0.5">dotnet run --project CallTree.Api</code>
				from <code class="rounded bg-rose-100 px-1 py-0.5">CallTree.Core</code>.
			</p>
		</div>
	{:else if result}
		<div class="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
			<table class="w-full min-w-[40rem] border-collapse text-sm">
				<thead class="bg-slate-50 text-left text-xs tracking-wide text-slate-500 uppercase">
					<tr>
						<th scope="col" class="px-4 py-3 font-medium">Received</th>
						<th scope="col" class="px-4 py-3 font-medium">From</th>
						<!-- The body is why anyone opens this page, so it takes every pixel the fixed-width
						     columns do not need. -->
						<th scope="col" class="w-full px-4 py-3 font-medium">Message</th>
						<th scope="col" class="px-4 py-3 font-medium">Status</th>
						{#if canSend}
							<th scope="col" class="px-4 py-3 font-medium">Relayed</th>
						{/if}
					</tr>
				</thead>
				<tbody class="divide-y divide-slate-100">
					{#each result.items as message (message.id)}
						{@const received = formatDateParts(message.receivedAt)}
						<tr class="align-top hover:bg-slate-50">
							<td class="px-4 py-3 whitespace-nowrap">
								<span class="block text-slate-700">{received.date}</span>
								<span class="block text-xs text-slate-500">{received.time}</span>
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span class="block font-medium text-slate-900">{label(message.from)}</span>
								<!-- What the Source column used to say. Inbound is the overwhelming majority and
								     needs no label; the row worth flagging is the one from the operator's own
								     mobile, which is a command rather than a message. -->
								{#if message.source === 'Outbound'}
									<span class="block text-xs text-slate-500">your mobile</span>
								{/if}
							</td>
							<td class="px-4 py-3">
								<span
									class="line-clamp-3 break-words whitespace-pre-wrap text-slate-800"
									title={message.body}
								>
									{message.body || '—'}
								</span>
								{#if message.mediaCount > 0}
									<!-- Attachments are recorded but never forwarded, so this is the only place
									     anyone learns there was a picture. -->
									<span class="mt-0.5 block text-xs text-amber-700">
										{message.mediaCount}
										{message.mediaCount === 1 ? 'attachment' : 'attachments'} (not forwarded)
									</span>
								{/if}
							</td>
							<td class="px-4 py-3 whitespace-nowrap">
								<span
									class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset {statusStyles[
										message.status
									]}"
								>
									{message.status}
								</span>
								{#if message.failureReason}
									<span
										class="mt-0.5 block max-w-[16rem] truncate text-xs text-slate-500"
										title={message.failureReason}
									>
										{message.failureReason}
									</span>
								{/if}
							</td>
							{#if canSend}
								<td class="px-4 py-3 whitespace-nowrap text-slate-700">
									{outcome(message)}
									{#if message.relayDelivery}
										<span class="mt-0.5 block text-xs {deliveryStyles[message.relayDelivery]}">
											{message.relayDelivery}
										</span>
									{/if}
								</td>
							{/if}
						</tr>
					{:else}
						<tr>
							<td colspan={columnCount} class="px-4 py-10 text-center text-slate-500">
								No messages to show.
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>

		{#if result.totalPages > 1}
			<nav class="flex items-center justify-between" aria-label="Pagination">
				<a
					href="{resolve('/messages')}{pageQuery(result.page - 1)}"
					aria-disabled={!result.hasPreviousPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					← Newer
				</a>

				<span class="text-sm text-slate-500">
					Page {result.page.toLocaleString()} of {result.totalPages.toLocaleString()}
				</span>

				<a
					href="{resolve('/messages')}{pageQuery(result.page + 1)}"
					aria-disabled={!result.hasNextPage}
					class="rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 aria-disabled:pointer-events-none aria-disabled:opacity-40"
				>
					Older →
				</a>
			</nav>
		{/if}

		<!-- Only worth saying on a line that can act on it; the receive-only banner says the rest. -->
		{#if canSend}
			<p class="text-xs text-slate-500">
				To send a text, message your DID from your mobile in the form
				<code class="rounded bg-slate-100 px-1 py-0.5">3055551234 Your message here</code>. Anything
				else texted to the DID is forwarded to your mobile.
			</p>
		{/if}
	{/if}
</section>
