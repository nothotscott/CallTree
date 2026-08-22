<script lang="ts">
	import './layout.css';
	import { resolve } from '$app/paths';

	let { children } = $props();
</script>

<svelte:head><link rel="icon" href="/favicon.ico" /></svelte:head>

<div class="min-h-screen bg-slate-50 text-slate-900">
	<header class="border-b border-slate-200 bg-white">
		<div class="mx-auto flex max-w-6xl items-center gap-6 px-6 py-4">
			<a
				href={resolve('/calls')}
				class="flex items-center gap-1 text-lg font-semibold tracking-tight"
			>
				<img src="/CallTree.png" alt="CallTree logo" class="inline size-8" />
				<span>CallTree</span>
			</a>
			<nav class="flex gap-4 text-sm text-slate-600">
				<a href={resolve('/calls')} class="hover:text-slate-900">Calls</a>
				<a href={resolve('/recordings')} class="hover:text-slate-900">Recordings</a>
				<a href={resolve('/status')} class="hover:text-slate-900">Status</a>
				<a href={resolve('/settings')} class="hover:text-slate-900">Settings</a>
			</nav>
		</div>
	</header>

	<main class="mx-auto max-w-6xl px-6 py-8">
		{@render children()}
	</main>
</div>
