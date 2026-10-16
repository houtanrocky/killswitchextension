import { getHostFromUrl, readRules, saveRule, requestRuleRefresh } from "./storage";
import { fetchPublicIp, isIpAddress } from "./ip";
import { getPageStatus, type PageStatus } from "./page-status";

export interface PopupState {
	host: string | null;
	status: PageStatus;
	currentIp: string | null;
	providerUnavailable: boolean;
}

export async function getPopupState(): Promise<PopupState> {
	const tabs = await browser.tabs.query({ active: true, currentWindow: true });
	const tabUrl = tabs[0]?.url;
	const host = tabUrl ? getHostFromUrl(tabUrl) : null;
	const stored = (await browser.storage.local.get([
		"lastKnownIp",
		"lastBlockedDomains",
		"ipProviderUnavailable"
	])) as {
		lastKnownIp?: unknown;
		lastBlockedDomains?: unknown;
		ipProviderUnavailable?: unknown;
	};
	const rules = await readRules();
	const blockedUrls = Array.isArray(stored.lastBlockedDomains)
		? stored.lastBlockedDomains.filter((url): url is string => typeof url === "string")
		: [];

	return {
		host,
		status: getPageStatus(rules.urls, blockedUrls, tabUrl),
		currentIp: isIpAddress(stored.lastKnownIp) ? stored.lastKnownIp : null,
		providerUnavailable: stored.ipProviderUnavailable === true
	};
}

export async function saveUrlRule(
	domainOnly: boolean,
	whitelist: boolean
): Promise<void> {
	const tabs = await browser.tabs.query({ active: true, currentWindow: true });
	const tabUrl = tabs[0]?.url;
	if (!tabUrl) {
		throw new Error("No active tab URL available.");
	}

	const target = domainOnly ? getHostFromUrl(tabUrl) : tabUrl;
	if (!target) {
		throw new Error("Unable to parse domain from the current tab.");
	}

	const ip = await fetchPublicIp();
	await saveRule(target, whitelist ? ip : `!${ip}`, false);
	await requestRuleRefresh();
}
