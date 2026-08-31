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

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
	['second', 60],
	['minute', 60],
	['hour', 24],
	['day', 7],
	['week', 4.348],
	['month', 12],
	['year', Number.POSITIVE_INFINITY]
];

/**
 * "3 minutes ago". Used for registration timestamps, where the age is the point — "registered at
 * 09:12" tells you nothing until you work out what time it is now.
 */
export function formatRelative(iso: string | null, now: number = Date.now()): string {
	if (!iso) return '—';
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) return '—';

	let delta = (date.getTime() - now) / 1000;
	for (const [unit, step] of RELATIVE_UNITS) {
		if (Math.abs(delta) < step) return relativeFormat.format(Math.round(delta), unit);
		delta /= step;
	}
	return relativeFormat.format(Math.round(delta), 'year');
}

/** "1.2 MB", "340 KB", etc. A dash while a recording hasn't finalized (size isn't known until then). */
export function formatBytes(bytes: number | null): string {
	if (bytes === null || !Number.isFinite(bytes) || bytes < 0) return '—';
	if (bytes < 1024) return `${bytes} B`;

	const units = ['KB', 'MB', 'GB'];
	let value = bytes / 1024;
	let unitIndex = 0;
	while (value >= 1024 && unitIndex < units.length - 1) {
		value /= 1024;
		unitIndex++;
	}
	return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`;
}

/** Groups an E.164 NANP number for readability; anything else is shown as-is. */
export function formatPhoneNumber(e164: string | null): string | null {
	if (!e164) return null;
	const nanp = /^\+1(\d{3})(\d{3})(\d{4})$/.exec(e164);
	return nanp ? `(${nanp[1]}) ${nanp[2]}-${nanp[3]}` : e164;
}

const dateOnlyFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
const timeOnlyFormat = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' });

/**
 * Date and time as two separate strings, for a table cell that stacks them.
 *
 * Seconds are dropped on purpose. They earn their place in the call log, where the question is often how
 * long something took, and they are noise in the message log, where the question is which day a code
 * arrived — and dropping them is most of what lets that column stop being the widest one on the row.
 */
export function formatDateParts(iso: string | null): { date: string; time: string } {
	if (!iso) return { date: '—', time: '' };
	const value = new Date(iso);
	if (Number.isNaN(value.getTime())) return { date: '—', time: '' };
	return { date: dateOnlyFormat.format(value), time: timeOnlyFormat.format(value) };
}
