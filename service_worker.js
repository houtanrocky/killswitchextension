"use strict";

const IP_ENDPOINTS = [
	"https://api.ipify.org",
	"https://ifconfig.me/ip",
	"https://ip.seeip.org"
];
const RULE_ID_BASE = 1000;
const ICON_VARIANTS = ["red", "green", "grey"];

let currentRefreshPromise = null;

const ICON_PATHS = ICON_VARIANTS.reduce((acc, color) => {
	acc[color] = {
		16: `/img/icon-48x48-${color}.png`,
		32: `/img/icon-48x48-${color}.png`,
		48: `/img/icon-48x48-${color}.png`,
		128: `/img/icon-128x128-${color}.png`
	};
	return acc;
}, {});

function storageGet(keys) {
	return new Promise((resolve, reject) => {
		chrome.storage.local.get(keys, (result) => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve(result);
			}
		});
	});
}

function storageSet(items) {
	return new Promise((resolve, reject) => {
		chrome.storage.local.set(items, () => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve();
			}
		});
	});
}

function getDynamicRules() {
	return new Promise((resolve, reject) => {
		chrome.declarativeNetRequest.getDynamicRules((rules) => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve(rules);
			}
		});
	});
}

function updateDynamicRulesChrome(updateOptions) {
	return new Promise((resolve, reject) => {
		chrome.declarativeNetRequest.updateDynamicRules(
			updateOptions,
			() => {
				const error = chrome.runtime.lastError;
				if (error) {
					reject(error);
				} else {
					resolve();
				}
			}
		);
	});
}

function setActionIcon(paths) {
	return new Promise((resolve, reject) => {
		chrome.action.setIcon({ path: paths }, () => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve();
			}
		});
	});
}

function setActionTitle(title) {
	return new Promise((resolve, reject) => {
		chrome.action.setTitle({ title }, () => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve();
			}
		});
	});
}

function queryTabs(queryInfo) {
	return new Promise((resolve, reject) => {
		chrome.tabs.query(queryInfo, (tabs) => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve(tabs);
			}
		});
	});
}

function reloadTab(tabId) {
	return new Promise((resolve, reject) => {
		chrome.tabs.reload(tabId, () => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
			} else {
				resolve();
			}
		});
	});
}

function escapeRegex(value) {
	return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function parseStoredIps(rawValue) {
	if (typeof rawValue !== "string" || rawValue.length === 0) {
		return [];
	}
	return rawValue
		.replace(/\|/g, "")
		.split(",")
		.map((entry) => entry.trim())
		.filter((entry) => entry.length > 0);
}

function isIpAddress(value) {
	return /^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$/.test(
		value
	);
}

async function fetchCurrentIp(options = { allowCacheFallback: true }) {
	let lastError = null;
	for (const endpoint of IP_ENDPOINTS) {
		try {
			const response = await fetch(endpoint, { cache: "no-store" });
			if (!response.ok) {
				throw new Error(
					`IP lookup failed (${endpoint} -> ${response.status})`
				);
			}
			const text = (await response.text()).trim();
			if (!isIpAddress(text)) {
				throw new Error(
					`Invalid IP address received from ${endpoint}: ${text}`
				);
			}
			return text;
		} catch (error) {
			lastError = error;
		}
	}

	if (options.allowCacheFallback) {
		const { lastKnownIp } = await storageGet(["lastKnownIp"]);
		if (lastKnownIp && isIpAddress(lastKnownIp)) {
			await storageSet({ ipProviderUnavailable: true });
			console.warn("Using cached IP after lookup failure.");
			return lastKnownIp;
		}
	}

	throw lastError || new Error("Unable to determine IP address.");
}

function shouldBlockForIp(userIp, storedIps) {
	if (!userIp || !isIpAddress(userIp) || storedIps.length === 0) {
		return true;
	}

	const whitelistExists = storedIps.some((ip) => !ip.startsWith("!"));
	let shouldBlock = whitelistExists;

	for (const entry of storedIps) {
		let ip = entry;
		let isNegation = false;

		if (entry.startsWith("!")) {
			ip = entry.substring(1);
			isNegation = true;
		}

		if (ip === userIp) {
			if (isNegation) {
				return true;
			}
			return false;
		}

		if (!whitelistExists && isNegation) {
			shouldBlock = false;
		}
	}

	return shouldBlock;
}

function isPlainHostname(pattern) {
	if (typeof pattern !== "string") {
		return false;
	}
	const trimmed = pattern.trim();
	if (trimmed.length === 0 || /^https?:\/\//i.test(trimmed)) {
		return false;
	}
	return /^[a-z0-9.-]+$/i.test(trimmed);
}

const BLOCK_RESOURCE_TYPES = [
	"main_frame",
	"sub_frame",
	"stylesheet",
	"script",
	"image",
	"font",
	"xmlhttprequest",
	"ping",
	"other"
];

function buildRuleFromPattern(pattern, id) {
	if (typeof pattern !== "string" || pattern.trim().length === 0) {
		return null;
	}

	const trimmed = pattern.trim();
	const condition = {
		resourceTypes: BLOCK_RESOURCE_TYPES
	};

	const asUrlFilter = (value) => {
		if (!value.includes("://")) {
			return `*://${value.replace(/^\*+\./, "")}/*`;
		}
		const withoutProtocol = value.replace(/^https?:\/\//i, "");
		return `*://${withoutProtocol}`;
	};

	if (isPlainHostname(trimmed)) {
		condition.urlFilter = asUrlFilter(trimmed);
	} else if (/^https?:\/\//i.test(trimmed) && !trimmed.includes("*")) {
		condition.urlFilter = asUrlFilter(trimmed + "*");
	} else if (!trimmed.includes("*")) {
		condition.urlFilter = asUrlFilter(trimmed);
	} else {
		const escaped = trimmed
			.split("*")
			.map((segment) => escapeRegex(segment))
			.join(".*");
		condition.regexFilter = `^${escaped}$`;
	}

	return {
		id,
		priority: 1,
		action: { type: "block" },
		condition
	};
}

async function applyIcon(color) {
	const icon = ICON_PATHS[color] || ICON_PATHS.grey;
	await setActionIcon(icon);
}

async function applyDynamicRules(rules) {
	const existing = await getDynamicRules();
	const removeRuleIds = existing.map((rule) => rule.id);
	await updateDynamicRulesChrome({
		removeRuleIds,
		addRules: rules
	});
}

async function refreshRules(reason) {
	if (currentRefreshPromise) {
		return currentRefreshPromise;
	}

	currentRefreshPromise = (async () => {
		try {
			const { urls = [], allowed_ips: allowedIpsRaw = [] } =
				await storageGet(["urls", "allowed_ips"]);

			const sanitizedUrls = Array.isArray(urls) ? urls : [];
			const sanitizedAllowedIps = Array.isArray(allowedIpsRaw)
				? allowedIpsRaw
				: [];

			if (sanitizedUrls.length === 0) {
				await applyDynamicRules([]);
				await applyIcon("grey");
				await setActionTitle("Kill Switch: Waiting for rules");
				await storageSet({
					lastKnownIp: null,
					lastUpdatedReason: reason || "manual",
					lastBlockedDomains: [],
					ipProviderUnavailable: false,
					ipVerificationPending: false
				});
				return;
			}

			const provisionalRules = sanitizedUrls
				.map((pattern, index) =>
					buildRuleFromPattern(pattern, RULE_ID_BASE + index)
				)
				.filter(Boolean);

			if (provisionalRules.length > 0) {
				await applyDynamicRules(provisionalRules);
				await applyIcon("red");
				await setActionTitle(
					"Kill Switch: Blocking while verifying IP"
				);
				await storageSet({ ipVerificationPending: true });
			}

			let currentIp = null;
			try {
				currentIp = await fetchCurrentIp();
			} catch (ipError) {
				console.error("All IP providers failed", ipError);
				await storageSet({ ipProviderUnavailable: true });
			}
			const rulesToAdd = [];
			const blockedDomains = [];

			const fallbackBlockAll = currentIp === null;

			sanitizedUrls.forEach((pattern, index) => {
				const rawIps = sanitizedAllowedIps[index] || "";
				const ips = parseStoredIps(rawIps);
				const shouldBlock = fallbackBlockAll
					? true
					: shouldBlockForIp(currentIp, ips);
				if (shouldBlock) {
					const rule = buildRuleFromPattern(
						pattern,
						RULE_ID_BASE + index
					);
					if (rule) {
						rulesToAdd.push(rule);
						blockedDomains.push(pattern);
					}
				}
			});

			await applyDynamicRules(rulesToAdd);

			if (blockedDomains.length > 0) {
				await applyIcon("red");
				await setActionTitle(
					fallbackBlockAll
						? "Kill Switch: Blocking all domains (IP unavailable)"
						: `Kill Switch: Blocking ${blockedDomains.length} domain(s)`
				);
			} else {
				await applyIcon("green");
				await setActionTitle("Kill Switch: All domains allowed");
			}

			await storageSet({
				lastKnownIp: currentIp,
				lastUpdatedReason: reason || "manual",
				lastBlockedDomains: blockedDomains,
				ipProviderUnavailable: fallbackBlockAll,
				ipVerificationPending: false
			});
		} catch (error) {
			console.error("Failed to refresh rules", error);
			await applyDynamicRules([]);
			await applyIcon("grey");
			await setActionTitle("Kill Switch: Unable to determine status");
			await storageSet({
				ipProviderUnavailable: true,
				ipVerificationPending: false
			});
		} finally {
			currentRefreshPromise = null;
		}
	})();

	return currentRefreshPromise;
}

chrome.runtime.onInstalled.addListener(() => {
	refreshRules("installed");
});

chrome.runtime.onStartup.addListener(() => {
	refreshRules("startup");
});

chrome.storage.onChanged.addListener((changes, areaName) => {
	if (areaName === "local" && (changes.urls || changes.allowed_ips)) {
		refreshRules("storage-change");
	}
});

chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
	if (!request || !request.type) {
		return;
	}

	if (request.type === "update") {
		refreshRules("manual-update")
			.then(() => sendResponse({ success: true }))
			.catch((error) => {
				sendResponse({ success: false, error: error?.message });
			});
		return true;
	}

	if (request.type === "refresh") {
		queryTabs({ active: true, lastFocusedWindow: true })
			.then((tabs) => {
				if (tabs.length > 0 && tabs[0].id !== undefined) {
					return reloadTab(tabs[0].id);
				}
				return undefined;
			})
			.then(() => sendResponse({ success: true }))
			.catch((error) =>
				sendResponse({ success: false, error: error?.message })
			);
		return true;
	}
});

// Initial kick-off when the service worker wakes up.
refreshRules("boot");