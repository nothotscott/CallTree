import { redirect } from '@sveltejs/kit';

// The call log is the only thing here so far, but it gets a real route rather than living at the
// root so that later pages (a recording player, settings) can sit alongside it.
export function load() {
	redirect(307, '/calls');
}
