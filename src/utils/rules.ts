import { isIpAddress } from "./ip";
import type { Browser } from "wxt/browser";

export const RULE_ID_BASE = 1000;

export const BLOCK_RESOURCE_TYPES = [
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

export type DynamicRule = Browser.declarativeNetRequest.Rule;

export function escapeRegex(value: string): string {
	return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function parseStoredIps(rawValue: unknown): string[] {
	if (typeof rawValue !== "string" || rawValue.length === 0) {
		return [];
	}
	return rawValue
		.replace(/\|/g, "")
		.split(",")
		.map((entry) => entry.trim())
		.filter((entry) => entry.length > 0);
}

export function shouldBlockForIp(userIp: string | null, storedIps: string[]): boolean {
	if (!userIp || !isIpAddress(userIp) || storedIps.length === 0) {
		return true;
	}

	let whitelistExists = false;
	let whitelistMatch = false;
	let blacklistMatch = false;

	for (const entry of storedIps) {
		const isNegation = entry.startsWith("!");
		const ip = isNegation ? entry.substring(1) : entry;

		if (isNegation) {
			blacklistMatch ||= ip === userIp;
		} else {
			whitelistExists = true;
			whitelistMatch ||= ip === userIp;
		}
	}

	if (blacklistMatch) {
		return true;
	}
	return whitelistExists ? !whitelistMatch : false;
}

export function isPlainHostname(pattern: unknown): boolean {
	if (typeof pattern !== "string") {
		return false;
	}
	const trimmed = pattern.trim();
	if (trimmed.length === 0 || /^https?:\/\//i.test(trimmed)) {
		return false;
	}
	return /^[a-z0-9.-]+$/i.test(trimmed);
}

export function buildRuleFromPattern(
	pattern: unknown,
	id: number
): DynamicRule | null {
	if (typeof pattern !== "string" || pattern.trim().length === 0) {
		return null;
	}

	const trimmed = pattern.trim();
	const condition: DynamicRule["condition"] = {
		resourceTypes: BLOCK_RESOURCE_TYPES as DynamicRule["condition"]["resourceTypes"]
	};
	const asUrlFilter = (value: string): string => {
		if (!value.includes("://")) {
			return `*://${value.replace(/^\*+\./, "")}/*`;
		}
		const withoutProtocol = value.replace(/^https?:\/\//i, "");
		return `*://${withoutProtocol}`;
	};

	if (isPlainHostname(trimmed)) {
		condition.urlFilter = asUrlFilter(trimmed);
	} else if (/^https?:\/\//i.test(trimmed) && !trimmed.includes("*")) {
		condition.urlFilter = asUrlFilter(`${trimmed}*`);
	} else if (!trimmed.includes("*")) {
		condition.urlFilter = asUrlFilter(trimmed);
	} else {
		const escaped = trimmed
			.split("*")
			.map(escapeRegex)
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

export function patternMatchesPage(pattern: unknown, pageUrl: unknown): boolean {
	if (typeof pattern !== "string" || typeof pageUrl !== "string") {
		return false;
	}

	let page: URL;
	try {
		page = new URL(pageUrl);
	} catch {
		return false;
	}

	if (page.protocol !== "http:" && page.protocol !== "https:") {
		return false;
	}

	const trimmed = pattern.trim().toLowerCase();
	if (trimmed.length === 0) {
		return false;
	}

	const pageWithoutProtocol = pageUrl.replace(/^https?:\/\//i, "");
	const patternWithoutProtocol = trimmed.replace(/^https?:\/\//i, "");

	if (isPlainHostname(trimmed)) {
		return page.hostname.toLowerCase() === trimmed;
	}

	if (trimmed.includes("*")) {
		const wildcardPattern = patternWithoutProtocol
			.split("*")
			.map(escapeRegex)
			.join(".*");
		return new RegExp(`^${wildcardPattern}$`, "i").test(
			pageWithoutProtocol
		);
	}

	return (
		pageWithoutProtocol === patternWithoutProtocol ||
		pageWithoutProtocol.startsWith(`${patternWithoutProtocol}/`) ||
		pageWithoutProtocol.startsWith(`${patternWithoutProtocol}?`) ||
		pageWithoutProtocol.startsWith(`${patternWithoutProtocol}#`)
	);
}
