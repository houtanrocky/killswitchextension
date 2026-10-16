import { beforeEach, afterEach, describe, expect, test, vi } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fetchPublicIp, isIpAddress, normalizeIpList } from "~/utils/ip";
import { IP_ENDPOINT } from "~/utils/ip";
import {
	buildRuleFromPattern,
	parseStoredIps,
	patternMatchesPage,
	shouldBlockForIp
} from "~/utils/rules";
import { getPageStatus, updateIconForActivePage } from "~/utils/page-status";
import {
	getRuleError,
	readRules,
	removeRule,
	replaceRules,
	saveRule
} from "~/utils/storage";
import { refreshRules } from "~/utils/background";
import { registerBackgroundListeners } from "~/utils/background";
import { getPopupState, saveUrlRule } from "~/utils/popup";
import { browser } from "wxt/browser";

interface FakeState {
	urls?: unknown;
	allowed_ips?: unknown;
	lastKnownIp?: unknown;
	lastBlockedDomains?: unknown;
	ipVerificationPending?: unknown;
	ipProviderUnavailable?: unknown;
	lastUpdatedReason?: unknown;
}

function createBrowser({
	activeUrl = "https://example.com/page",
	initialState = {}
}: { activeUrl?: string; initialState?: FakeState } = {}) {
	const state: FakeState = {
		urls: [],
		allowed_ips: [],
		lastKnownIp: null,
		lastBlockedDomains: [],
		ipVerificationPending: false,
		...initialState
	};
	const dynamicRules: Record<string, unknown>[] = [];
	const icons: string[] = [];
	const titles: string[] = [];
	const sentMessages: unknown[] = [];
	let currentUrl = activeUrl;
	let storageChangedListener: ((changes: Record<string, unknown>, areaName: string) => void) | undefined;

	const fakeBrowser = browser as any;
	vi.spyOn(fakeBrowser.storage.local, "get").mockImplementation(async (...args: any[]) => {
		const keys = args[0] as string | string[];
		const requested = Array.isArray(keys) ? keys : [keys];
		return Object.fromEntries(requested.map((key) => [key, state[key as keyof FakeState]]));
	});
	vi.spyOn(fakeBrowser.storage.local, "set").mockImplementation(async (...args: any[]) => {
		const items = args[0] as Record<string, unknown>;
		Object.assign(state, items);
	});
	vi.spyOn(fakeBrowser.action, "setIcon").mockImplementation(async (...args: any[]) => {
		const path = args[0].path as Record<number, string>;
		icons.push(path[48] ?? "");
	});
	vi.spyOn(fakeBrowser.action, "setTitle").mockImplementation(async (...args: any[]) => {
		titles.push(args[0].title as string);
	});
	vi.spyOn(fakeBrowser.tabs, "query").mockImplementation(async () =>
		currentUrl ? [{ id: 1, url: currentUrl }] : []
	);
	vi.spyOn(fakeBrowser.tabs, "reload").mockImplementation(async () => undefined);
	vi.spyOn(fakeBrowser.declarativeNetRequest, "getDynamicRules").mockImplementation(async () => dynamicRules);
	vi.spyOn(fakeBrowser.declarativeNetRequest, "updateDynamicRules").mockImplementation(async (...args: any[]) => {
		const {
			removeRuleIds = [],
			addRules = []
		} = args[0] as { removeRuleIds?: number[]; addRules?: Record<string, unknown>[] };
		for (let index = dynamicRules.length - 1; index >= 0; index -= 1) {
			const rule = dynamicRules[index];
			if (rule && removeRuleIds.includes(rule.id as number)) {
				dynamicRules.splice(index, 1);
			}
		}
		dynamicRules.push(...addRules);
	});
	vi.spyOn(fakeBrowser.runtime, "sendMessage").mockImplementation(async (message: unknown) => {
		sentMessages.push(message);
		return { success: true };
	});
	vi.spyOn(fakeBrowser.storage.onChanged, "addListener").mockImplementation((listener: any) => {
		storageChangedListener = listener;
	});

	return {
		browser: fakeBrowser,
		state,
		dynamicRules,
		icons,
		titles,
		sentMessages,
		setActiveUrl(url: string) {
			currentUrl = url;
		},
		async invokeStorageChange(changes: Record<string, unknown>, areaName = "local") {
			storageChangedListener?.(changes, areaName);
			await Promise.resolve();
			await Promise.resolve();
			await new Promise((resolve) => setTimeout(resolve, 0));
		}
	};
}

beforeEach(() => {
	vi.stubGlobal(
		"fetch",
		vi.fn(async () => ({ ok: true, status: 200, text: async () => "203.0.113.10" }))
	);
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
});

describe("IP and rule evaluation", () => {
	test("validates and normalizes IPv4 lists", () => {
		expect(isIpAddress("203.0.113.10")).toBe(true);
		expect(isIpAddress("999.0.0.1")).toBe(false);
		expect(normalizeIpList(" 203.0.113.10, !198.51.100.20| ")).toEqual([
			"203.0.113.10",
			"!198.51.100.20"
		]);
	});

	test("gives blacklist entries precedence regardless of order", () => {
		for (const entries of [
			["203.0.113.10", "!203.0.113.10"],
			["!203.0.113.10", "203.0.113.10"]
		]) {
			expect(shouldBlockForIp("203.0.113.10", entries)).toBe(true);
		}
		expect(shouldBlockForIp("198.51.100.20", ["!203.0.113.10"])).toBe(false);
		expect(shouldBlockForIp("198.51.100.20", ["203.0.113.10"])).toBe(true);
		expect(shouldBlockForIp("invalid", ["203.0.113.10"])).toBe(true);
	});

	test("builds host, page, and wildcard declarative rules", () => {
		const hostRule = buildRuleFromPattern("example.com", 1000);
		expect(hostRule?.condition.urlFilter).toBe("*://example.com/*");
		const pageRule = buildRuleFromPattern("https://example.com/private", 1001);
		expect(pageRule?.condition.urlFilter).toBe("*://example.com/private*");
		const wildcardRule = buildRuleFromPattern("https://example.com/*/admin", 1002);
		expect(wildcardRule?.condition.regexFilter).toBe("^https://example\\.com/.*/admin$");
		expect(buildRuleFromPattern("", 1003)).toBeNull();
	});

	test("matches only the configured page pattern", () => {
		expect(patternMatchesPage("example.com", "https://example.com/path")).toBe(true);
		expect(patternMatchesPage("example.com", "https://sub.example.com/path")).toBe(false);
		expect(patternMatchesPage("https://example.com/private", "https://example.com/private/child")).toBe(true);
		expect(patternMatchesPage("https://example.com/private", "https://example.com/public")).toBe(false);
	});

	test("parses stored IP delimiters", () => {
		expect(parseStoredIps("203.0.113.10, !198.51.100.20|")).toEqual([
			"203.0.113.10",
			"!198.51.100.20"
		]);
	});
});

describe("active page status", () => {
	test("returns gray, green, and red page states", () => {
		expect(getPageStatus([], [], "https://example.com")).toBe("grey");
		expect(getPageStatus(["example.com"], [], "https://example.com")).toBe("green");
		expect(getPageStatus(["example.com"], ["example.com"], "https://example.com")).toBe("red");
		expect(getPageStatus(["example.com"], ["example.com"], "chrome://extensions")).toBe("grey");
	});

	test("updates the browser icon and title for the active page", async () => {
		const harness = createBrowser();
		vi.stubGlobal("browser", harness.browser);
		await updateIconForActivePage(["example.com"], []);
		harness.setActiveUrl("https://other.example/page");
		await updateIconForActivePage(["example.com"], ["example.com"]);
		expect(harness.icons.map((icon) => icon.match(/-(green|grey)\.png$/)?.[1])).toEqual([
			"green",
			"grey"
		]);
		expect(harness.titles.at(-1)).toContain("No rule");
	});
});

describe("storage and popup flows", () => {
	test("validates, saves, toggles, and removes rules", async () => {
		const harness = createBrowser();
		vi.stubGlobal("browser", harness.browser);
		expect(getRuleError("", "203.0.113.10")).toBe("Please enter a domain or URL.");
		expect(getRuleError("example.com", "bad")).toContain("valid IPv4");

		await saveRule(" Example.COM ", "203.0.113.10", true);
		expect(harness.state.urls).toEqual(["example.com"]);
		expect(harness.state.allowed_ips).toEqual(["203.0.113.10|"]);
		await saveRule("example.com", "!203.0.113.10", false);
		expect(harness.state.allowed_ips).toEqual(["!203.0.113.10|"]);

		expect(await readRules()).toEqual({
			urls: ["example.com"],
			allowedIps: ["!203.0.113.10|"]
		});
		await removeRule("example.com");
		expect(harness.state.urls).toEqual([]);
		expect(harness.state.allowed_ips).toEqual([]);
	});

	test("uses one no-referrer IP provider and uses cached fallback", async () => {
		const harness = createBrowser();
		const fetchMock = vi.fn()
			.mockResolvedValueOnce({ ok: true, status: 200, text: async () => "198.51.100.20" });
		vi.stubGlobal("fetch", fetchMock);
		expect(await fetchPublicIp()).toBe("198.51.100.20");
		expect(fetchMock).toHaveBeenCalledTimes(1);
		expect(fetchMock).toHaveBeenCalledWith(IP_ENDPOINT, {
			cache: "no-store",
			referrerPolicy: "no-referrer"
		});

		harness.state.lastKnownIp = "203.0.113.10";
		vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("offline")));
		expect(await fetchPublicIp()).toBe("203.0.113.10");
	});

	test("saves a rule from the active tab and requests a refresh", async () => {
		const harness = createBrowser({ activeUrl: "https://example.com/private" });
		vi.stubGlobal("browser", harness.browser);
		await saveUrlRule(true, false);
		expect(harness.state.urls).toEqual(["example.com"]);
		expect(harness.state.allowed_ips).toEqual(["!203.0.113.10|"]);
		expect(harness.sentMessages).toEqual([{ type: "update" }]);
	});

	test("replaces edited rules atomically and rejects duplicate targets", async () => {
		const harness = createBrowser();
		vi.stubGlobal("browser", harness.browser);
		await replaceRules([
			{ domain: "Example.com", ips: "203.0.113.10" },
			{ domain: "https://example.net/private", ips: "!198.51.100.20" }
		]);
		expect(harness.state.urls).toEqual(["example.com", "https://example.net/private"]);
		expect(harness.state.allowed_ips).toEqual(["203.0.113.10|", "!198.51.100.20|"]);
		await expect(replaceRules([
			{ domain: "example.com", ips: "203.0.113.10" },
			{ domain: "EXAMPLE.COM", ips: "198.51.100.20" }
		])).rejects.toThrow("Duplicate rule target");
	});

	test("reports the current page status without sending the page URL", async () => {
		const harness = createBrowser({
			initialState: {
				urls: ["example.com"],
				allowed_ips: ["203.0.113.10|"],
				lastKnownIp: "203.0.113.10",
				lastBlockedDomains: []
			}
		});
		vi.stubGlobal("browser", harness.browser);
		const state = await getPopupState();
		expect(state).toMatchObject({ host: "example.com", status: "green", currentIp: "203.0.113.10" });
		expect(harness.sentMessages).toEqual([]);
	});
});

describe("background rule refresh", () => {
	test("allows matching IPs and blocks mismatches", async () => {
		const harness = createBrowser({
			initialState: {
				urls: ["example.com"],
				allowed_ips: ["203.0.113.10|"]
			}
		});
		vi.stubGlobal("browser", harness.browser);
		await refreshRules("test-allowed");
		expect(harness.dynamicRules).toHaveLength(0);
		expect(harness.state.lastBlockedDomains).toEqual([]);

		harness.state.allowed_ips = ["!203.0.113.10|"];
		await refreshRules("test-blocked");
		expect(harness.dynamicRules).toHaveLength(1);
		expect(harness.state.lastBlockedDomains).toEqual(["example.com"]);
		expect(harness.icons.at(-1)).toContain("red");
	});

	test("fails closed when all IP providers are unavailable", async () => {
		const harness = createBrowser({
			initialState: {
				urls: ["example.com"],
				allowed_ips: ["203.0.113.10|"]
			}
		});
		vi.stubGlobal("browser", harness.browser);
		vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("offline")));
		await refreshRules("test-offline");
		expect(harness.dynamicRules).toHaveLength(1);
		expect(harness.state.ipProviderUnavailable).toBe(true);
		expect(harness.state.lastBlockedDomains).toEqual(["example.com"]);
	});

	test("only rule storage changes trigger a background refresh", async () => {
		const harness = createBrowser({
			initialState: {
				urls: ["example.com"],
				allowed_ips: ["203.0.113.10|"]
			}
		});
		vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, text: async () => "203.0.113.10" })));
		registerBackgroundListeners();
		await harness.invokeStorageChange({ lastKnownIp: { newValue: "203.0.113.10" } });
		expect(harness.state.lastUpdatedReason).toBeUndefined();
		await harness.invokeStorageChange({ urls: { newValue: ["example.com"] } });
		expect(harness.state.lastUpdatedReason).toBe("storage-change");
	});
});

describe("privacy boundaries", () => {
	test("extension pages contain no remote font or tracking requests", () => {
		const popup = readFileSync(resolve("src/entrypoints/popup/index.html"), "utf8");
		const options = readFileSync(resolve("src/entrypoints/options/index.html"), "utf8");
		expect(`${popup}\n${options}`).not.toMatch(/https?:\/\//i);
		expect(readFileSync(resolve("src/utils/ip.ts"), "utf8")).not.toMatch(/ifconfig\.me|ip\.seeip\.org/);
	});
});
