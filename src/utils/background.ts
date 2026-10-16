import { fetchPublicIp } from "./ip";
import {
	buildRuleFromPattern,
	parseStoredIps,
	RULE_ID_BASE,
	shouldBlockForIp,
	type DynamicRule
} from "./rules";
import { updateIconForActivePage } from "./page-status";

let currentRefreshPromise: Promise<void> | null = null;

interface StoredState {
	urls?: unknown;
	allowed_ips?: unknown;
	lastBlockedDomains?: unknown;
	ipVerificationPending?: unknown;
}

async function getStoredState(keys: Array<keyof StoredState>): Promise<StoredState> {
	return (await browser.storage.local.get(keys)) as StoredState;
}

async function setStoredState(items: Record<string, unknown>): Promise<void> {
	await browser.storage.local.set(items);
}

async function applyDynamicRules(rules: DynamicRule[]): Promise<void> {
	const existing = await browser.declarativeNetRequest.getDynamicRules();
	await browser.declarativeNetRequest.updateDynamicRules({
		removeRuleIds: existing.map((rule) => rule.id),
		addRules: rules
	});
}

export async function updateIconFromStoredState(): Promise<void> {
	try {
		const {
			urls = [],
			lastBlockedDomains = [],
			ipVerificationPending = false
		} = await getStoredState([
			"urls",
			"lastBlockedDomains",
			"ipVerificationPending"
		]);
		const configuredUrls = Array.isArray(urls) ? urls.filter((url): url is string => typeof url === "string") : [];
		const blockedUrls = ipVerificationPending
			? configuredUrls
			: Array.isArray(lastBlockedDomains)
				? lastBlockedDomains.filter((url): url is string => typeof url === "string")
				: [];
		await updateIconForActivePage(configuredUrls, blockedUrls);
	} catch (error) {
		console.warn("Unable to update the active page icon", error);
	}
}

export async function refreshRules(reason = "manual"): Promise<void> {
	if (currentRefreshPromise) {
		return currentRefreshPromise;
	}

	currentRefreshPromise = (async () => {
		try {
			const stored = await getStoredState(["urls", "allowed_ips"]);
			const urls = Array.isArray(stored.urls)
				? stored.urls.filter((url): url is string => typeof url === "string")
				: [];
			const allowedIps = Array.isArray(stored.allowed_ips)
				? stored.allowed_ips
					.map((value) => (typeof value === "string" ? value : ""))
				: [];

			if (urls.length === 0) {
				await applyDynamicRules([]);
				await setStoredState({
					lastKnownIp: null,
					lastUpdatedReason: reason,
					lastBlockedDomains: [],
					ipProviderUnavailable: false,
					ipVerificationPending: false
				});
				await updateIconForActivePage([], []);
				return;
			}

			const provisionalRules = urls
				.map((pattern, index) => buildRuleFromPattern(pattern, RULE_ID_BASE + index))
				.filter((rule): rule is DynamicRule => rule !== null);
			if (provisionalRules.length > 0) {
				await applyDynamicRules(provisionalRules);
				await setStoredState({ ipVerificationPending: true });
				await updateIconForActivePage(urls, urls);
			}

			let currentIp: string | null = null;
		try {
			currentIp = await fetchPublicIp();
		} catch (error) {
			console.error("All IP providers failed", error);
		}

			const blockedDomains: string[] = [];
			const rulesToAdd: DynamicRule[] = [];
		const fallbackBlockAll = currentIp === null;

			urls.forEach((pattern, index) => {
				const storedIps = parseStoredIps(allowedIps[index] ?? "");
				const shouldBlock = fallbackBlockAll
					? true
					: shouldBlockForIp(currentIp, storedIps);
				if (shouldBlock) {
					const rule = buildRuleFromPattern(pattern, RULE_ID_BASE + index);
					if (rule) {
						rulesToAdd.push(rule);
						blockedDomains.push(pattern);
					}
				}
			});

			await applyDynamicRules(rulesToAdd);
			await setStoredState({
				lastKnownIp: currentIp,
				lastUpdatedReason: reason,
				lastBlockedDomains: blockedDomains,
				ipProviderUnavailable: fallbackBlockAll,
				ipVerificationPending: false
			});
			await updateIconForActivePage(urls, blockedDomains);
		} catch (error) {
			console.error("Failed to refresh rules", error);
			try {
				await applyDynamicRules([]);
				await updateIconForActivePage([], []);
				await setStoredState({
					ipProviderUnavailable: true,
					ipVerificationPending: false
				});
			} catch (cleanupError) {
				console.error("Failed to clear rules after refresh failure", cleanupError);
			}
		} finally {
			currentRefreshPromise = null;
		}
	})();

	return currentRefreshPromise;
}

export function registerBackgroundListeners(): void {
	browser.runtime.onInstalled.addListener(() => {
		void refreshRules("installed");
	});
	browser.runtime.onStartup.addListener(() => {
		void refreshRules("startup");
	});
	browser.storage.onChanged.addListener((changes, areaName) => {
		if (areaName === "local" && (changes.urls || changes.allowed_ips)) {
			void refreshRules("storage-change");
		}
	});
	browser.tabs.onActivated.addListener(() => {
		void updateIconFromStoredState();
	});
	browser.tabs.onUpdated.addListener((_tabId, changeInfo) => {
		if (changeInfo.url || changeInfo.status === "complete") {
			void updateIconFromStoredState();
		}
	});
	browser.runtime.onMessage.addListener(async (request) => {
		if (!request || !request.type) {
			return undefined;
		}
		if (request.type === "update") {
			try {
				await refreshRules("manual-update");
				return { success: true };
			} catch (error) {
				return { success: false, error: error instanceof Error ? error.message : String(error) };
			}
		}
		if (request.type === "refresh") {
			try {
				const tabs = await browser.tabs.query({ active: true, lastFocusedWindow: true });
				if (tabs[0]?.id !== undefined) {
					await browser.tabs.reload(tabs[0].id);
				}
				return { success: true };
			} catch (error) {
				return { success: false, error: error instanceof Error ? error.message : String(error) };
			}
		}
		return undefined;
	});
}
