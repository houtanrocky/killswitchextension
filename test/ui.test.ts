/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { browser } from "wxt/browser";

const fetchPublicIpMock = vi.fn();
const getPopupStateMock = vi.fn();
const saveUrlRuleMock = vi.fn();
const readRulesMock = vi.fn();
const replaceRulesMock = vi.fn();

vi.mock("~/utils/ip", () => ({ fetchPublicIp: fetchPublicIpMock }));
vi.mock("~/utils/popup", () => ({
	getPopupState: getPopupStateMock,
	saveUrlRule: saveUrlRuleMock
}));
vi.mock("~/utils/storage", () => ({
	readRules: readRulesMock,
	replaceRules: replaceRulesMock,
	removeRule: vi.fn()
}));

function setBody(html: string): void {
	document.body.innerHTML = html;
}

beforeEach(() => {
	vi.resetModules();
	vi.clearAllMocks();
	vi.spyOn(browser.runtime, "openOptionsPage").mockResolvedValue(undefined);
	vi.stubGlobal("fetch", vi.fn());
	Object.defineProperty(window, "close", { value: vi.fn(), configurable: true });
	Object.defineProperty(window, "alert", { value: vi.fn(), configurable: true });
});

afterEach(() => {
	vi.restoreAllMocks();
	vi.unstubAllGlobals();
	document.body.innerHTML = "";
});

describe("popup UI", () => {
	test("renders local status and routes each quick action", async () => {
		setBody(`
			<div id="status-card"></div><span id="status-label"></span><h2 id="page-target"></h2>
			<p id="status-detail"></p><strong id="current-ip"></strong><span id="provider-status"></span>
			<button id="wl_domain" class="action-button"></button>
			<button id="bl_domain" class="action-button"></button>
			<button id="wl_url" class="action-button"></button>
			<button id="bl_url" class="action-button"></button>
			<button id="rules"></button><button id="manage-rules"></button>
		`);
		getPopupStateMock.mockResolvedValue({
			host: "example.com",
			status: "green",
			currentIp: "203.0.113.10",
			providerUnavailable: false
		});
		await import("~/entrypoints/popup/main");
		await Promise.resolve();

		expect(document.getElementById("status-label")?.textContent).toBe("Allowed on this IP");
		expect(document.getElementById("page-target")?.textContent).toBe("example.com");
		expect(document.getElementById("current-ip")?.textContent).toBe("203.0.113.10");

		await (document.getElementById("bl_domain") as HTMLButtonElement).click();
		expect(saveUrlRuleMock).toHaveBeenCalledWith(true, false);
		await (document.getElementById("manage-rules") as HTMLButtonElement).click();
		expect(browser.runtime.openOptionsPage).toHaveBeenCalledTimes(1);
	});

	test("shows unavailable IP status without attempting a second provider", async () => {
		setBody(`
			<div id="status-card"></div><span id="status-label"></span><h2 id="page-target"></h2>
			<p id="status-detail"></p><strong id="current-ip"></strong><span id="provider-status"></span>
			<button id="wl_domain" class="action-button"></button><button id="bl_domain" class="action-button"></button>
			<button id="wl_url" class="action-button"></button><button id="bl_url" class="action-button"></button>
			<button id="rules"></button><button id="manage-rules"></button>
		`);
		getPopupStateMock.mockResolvedValue({
			host: null,
			status: "grey",
			currentIp: null,
			providerUnavailable: true
		});
		await import("~/entrypoints/popup/main");
		await Promise.resolve();
		expect(fetchPublicIpMock).not.toHaveBeenCalled();
		expect(document.getElementById("current-ip")?.textContent).toBe("Unavailable");
	});
});

describe("options UI", () => {
	test("renders, adds, edits, removes, and saves rule rows", async () => {
		setBody(`
			<div id="message"></div><span id="rule-count"></span><div id="content"></div>
			<button id="add"></button><button id="save"></button><button id="reset"></button>
		`);
		readRulesMock.mockResolvedValue({
			urls: ["example.com"],
			allowedIps: ["203.0.113.10|"]
		});
		replaceRulesMock.mockResolvedValue(undefined);
		await import("~/entrypoints/options/main");
		await Promise.resolve();

		expect(document.querySelectorAll(".rule-card")).toHaveLength(1);
		expect(document.getElementById("rule-count")?.textContent).toBe("1 rule");
		await (document.getElementById("add") as HTMLButtonElement).click();
		expect(document.querySelectorAll(".rule-card")).toHaveLength(2);
		(document.querySelectorAll<HTMLInputElement>(".domain_regex")[0]!).value = "edited.example";
		(document.querySelectorAll<HTMLTextAreaElement>(".ips")[0]!).value = "198.51.100.20";
		(document.querySelectorAll<HTMLButtonElement>(".button-danger")[0]!).click();
		expect(document.querySelectorAll(".rule-card")).toHaveLength(1);
		await (document.getElementById("save") as HTMLButtonElement).click();
		await Promise.resolve();
		expect(replaceRulesMock).toHaveBeenCalledWith([
			{ domain: "", ips: "" }
		]);
	});

	test("filters a large rule list by target or IP", async () => {
		setBody(`
			<div id="message"></div><span id="rule-count"></span><div id="content"></div>
			<label><input id="rule-search" /></label>
			<button id="add"></button><button id="save"></button><button id="reset"></button>
		`);
		readRulesMock.mockResolvedValue({
			urls: Array.from({ length: 100 }, (_, index) => `rule-${index}.example`),
			allowedIps: Array.from({ length: 100 }, (_, index) => `203.0.113.${(index % 9) + 1}|`)
		});
		await import("~/entrypoints/options/main");
		await Promise.resolve();
		expect(document.querySelectorAll(".rule-card")).toHaveLength(100);

		const search = document.getElementById("rule-search") as HTMLInputElement;
		search.value = "rule-42";
		search.dispatchEvent(new Event("input", { bubbles: true }));
		expect(document.querySelectorAll(".rule-card")).toHaveLength(1);
		expect(document.getElementById("rule-count")?.textContent).toBe("1 of 100 rules");
		expect(document.querySelector<HTMLInputElement>(".domain_regex")?.value).toBe("rule-42.example");

		search.value = "203.0.113.7";
		search.dispatchEvent(new Event("input", { bubbles: true }));
		expect(document.querySelectorAll(".rule-card").length).toBeGreaterThan(1);
	});
});
