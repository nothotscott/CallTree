const dateTimeFormat = new Intl.DateTimeFormat(undefined, {
	dateStyle: 'medium',
	timeStyle: 'medium'
});

export function formatTimestamp(iso: string | null): string {
	if (!iso) return '—';
	const date = new Date(iso);
	return Number.isNaN(date.getTime()) ? '—' : dateTimeFormat.format(date);
}

/** m:ss, or h:mm:ss once a call runs past the hour. A dash while the call is still in flight. */
export function formatDuration(seconds: number | null): string {
	if (seconds === null || !Number.isFinite(seconds)) return '—';

	const total = Math.max(0, Math.round(seconds));
	const hours = Math.floor(total / 3600);
	const minutes = Math.floor((total % 3600) / 60);
	const secs = total % 60;

	return hours > 0
		? `${hours}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
		: `${minutes}:${String(secs).padStart(2, '0')}`;
}

/** Groups an E.164 NANP number for readability; anything else is shown as-is. */
export function formatPhoneNumber(e164: string | null): string | null {
	if (!e164) return null;
	const nanp = /^\+1(\d{3})(\d{3})(\d{4})$/.exec(e164);
	return nanp ? `(${nanp[1]}) ${nanp[2]}-${nanp[3]}` : e164;
}
