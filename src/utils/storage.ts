import { normalizeIpList, isIpAddress } from "./ip";
import { parseStoredIps } from "./rules";

export interface RuleState {
	urls: string[];
	allowedIps: string[];
}

export interface RuleDraft {
	domain: string;
	ips: string;
}

export function getHostFromUrl(value: string): string | null {
	try {
		const url = new URL(value);
		return url.hostname || null;
	} catch {
		return null;
	}
}

export function getRuleError(domain: unknown, ips: unknown): string | null {
	const normalizedDomain = typeof domain === "string" ? domain.trim() : "";
	if (normalizedDomain.length === 0) {
		return "Please enter a domain or URL.";
	}

	const ipArray = normalizeIpList(ips);
	if (
		ipArray.length === 0 ||
		ipArray.some((ip) => !isIpAddress(ip.startsWith("!") ? ip.substring(1) : ip))
	) {
		return "Please enter at least one valid IPv4 address (optionally prefixed with ! for blacklist).";
	}
	return null;
}

export async function readRules(): Promise<RuleState> {
	const stored = (await browser.storage.local.get(["urls", "allowed_ips"])) as {
		urls?: unknown;
		allowed_ips?: unknown;
	};
	return {
		urls: Array.isArray(stored.urls) ? stored.urls.filter((url): url is string => typeof url === "string") : [],
		allowedIps: Array.isArray(stored.allowed_ips)
			? stored.allowed_ips.map((value) => (typeof value === "string" ? value : ""))
			: []
	};
}

export async function saveRule(
	domain: unknown,
	ips: unknown,
	manual: boolean
): Promise<void> {
	const error = getRuleError(domain, ips);
	if (error) {
		throw new Error(error);
	}

	const normalizedDomain = (domain as string).trim().toLowerCase();
	const inputIps = normalizeIpList(ips);
	const rules = await readRules();
	let index = rules.urls.indexOf(normalizedDomain);
	const isNew = index === -1;

	if (isNew) {
		rules.urls.push(normalizedDomain);
		index = rules.urls.length - 1;
	}

	let finalIps = inputIps;
	if (!manual && rules.allowedIps[index] != null) {
		const storedIps = parseStoredIps(rules.allowedIps[index]);
		for (const inputIp of inputIps) {
			for (const storedIp of storedIps.slice()) {
				if (
					inputIp === storedIp ||
					inputIp === `!${storedIp}` ||
					`!${inputIp}` === storedIp
				) {
					storedIps.splice(storedIps.indexOf(storedIp), 1);
				}
			}
		}
		finalIps = storedIps.concat(inputIps);
	}

	rules.allowedIps[index] = `${finalIps.join(",")}|`;
	await browser.storage.local.set({ urls: rules.urls, allowed_ips: rules.allowedIps });
}

export async function replaceRules(drafts: RuleDraft[]): Promise<void> {
	const normalizedUrls: string[] = [];
	const normalizedIps: string[] = [];

	for (const draft of drafts) {
		const error = getRuleError(draft.domain, draft.ips);
		if (error) {
			throw new Error(error);
		}
		const domain = draft.domain.trim().toLowerCase();
		if (normalizedUrls.includes(domain)) {
			throw new Error(`Duplicate rule target: ${domain}`);
		}
		normalizedUrls.push(domain);
		normalizedIps.push(`${normalizeIpList(draft.ips).join(",")}|`);
	}

	await browser.storage.local.set({
		urls: normalizedUrls,
		allowed_ips: normalizedIps
	});
}

export async function removeRule(domain: string): Promise<void> {
	const rules = await readRules();
	const index = rules.urls.indexOf(domain);
	if (index !== -1) {
		rules.urls.splice(index, 1);
		if (index < rules.allowedIps.length) {
			rules.allowedIps.splice(index, 1);
		}
	}
	await browser.storage.local.set({ urls: rules.urls, allowed_ips: rules.allowedIps });
}

export async function requestRuleRefresh(): Promise<void> {
	await browser.runtime.sendMessage({ type: "update" });
}
