export const IP_ENDPOINT = "https://api.ipify.org";

export function isIpAddress(value: unknown): value is string {
	return typeof value === "string" && /^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$/.test(value);
}

export function normalizeIpList(rawIps: unknown): string[] {
	if (typeof rawIps !== "string") {
		return [];
	}
	return rawIps
		.split(",")
		.map((entry) => entry.replace(/\s+/g, "").replace(/\|/g, ""))
		.filter((entry) => entry.length > 0);
}

export interface FetchPublicIpOptions {
	allowCacheFallback?: boolean;
}

export async function fetchPublicIp({
	allowCacheFallback = true
}: FetchPublicIpOptions = {}): Promise<string> {
	let lastError: unknown = null;

	try {
		const response = await fetch(IP_ENDPOINT, {
			cache: "no-store",
			referrerPolicy: "no-referrer"
		});
		if (!response.ok) {
			throw new Error(`IP lookup failed (${response.status})`);
		}
		const text = (await response.text()).trim();
		if (!isIpAddress(text)) {
			throw new Error(`Invalid IP returned: ${text}`);
		}
		return text;
	} catch (error) {
		lastError = error;
	}

	if (allowCacheFallback) {
		const { lastKnownIp } = (await browser.storage.local.get(
			"lastKnownIp"
		)) as { lastKnownIp?: unknown };
		if (isIpAddress(lastKnownIp)) {
			console.warn("Using cached IP after lookup failure.");
			return lastKnownIp;
		}
	}

	throw lastError instanceof Error
		? lastError
		: new Error("Unable to determine IP address.");
}
