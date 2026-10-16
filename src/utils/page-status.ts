import { patternMatchesPage } from "./rules";

export type PageStatus = "grey" | "red" | "green";

const ICON_PATHS: Record<PageStatus, Record<number, string>> = {
	red: {
		16: "/img/icon-48x48-red.png",
		32: "/img/icon-48x48-red.png",
		48: "/img/icon-48x48-red.png",
		128: "/img/icon-128x128-red.png"
	},
	green: {
		16: "/img/icon-48x48-green.png",
		32: "/img/icon-48x48-green.png",
		48: "/img/icon-48x48-green.png",
		128: "/img/icon-128x128-green.png"
	},
	grey: {
		16: "/img/icon-48x48-grey.png",
		32: "/img/icon-48x48-grey.png",
		48: "/img/icon-48x48-grey.png",
		128: "/img/icon-128x128-grey.png"
	}
};

export function getPageStatus(
	configuredUrls: string[],
	blockedUrls: string[],
	activeUrl: string | undefined
): PageStatus {
	if (!activeUrl) {
		return "grey";
	}
	const matchingUrls = configuredUrls.filter((pattern) =>
		patternMatchesPage(pattern, activeUrl)
	);
	if (matchingUrls.length === 0) {
		return "grey";
	}
	return blockedUrls.some((pattern) => patternMatchesPage(pattern, activeUrl))
		? "red"
		: "green";
}

export async function getActiveTabUrl(): Promise<string | undefined> {
	const tabs = await browser.tabs.query({ active: true, lastFocusedWindow: true });
	return tabs[0]?.url;
}

export async function updateIconForActivePage(
	configuredUrls: string[],
	blockedUrls: string[]
): Promise<PageStatus> {
	let activeUrl: string | undefined;
	try {
		activeUrl = await getActiveTabUrl();
	} catch (error) {
		console.warn("Unable to inspect the active tab", error);
	}

	const status = getPageStatus(configuredUrls, blockedUrls, activeUrl);
	await browser.action.setIcon({ path: ICON_PATHS[status] });
	await browser.action.setTitle({
		title:
			status === "red"
				? "Kill Switch: This page is limited"
				: status === "green"
					? "Kill Switch: Rule applied to this page"
					: "Kill Switch: No rule for this page"
	});
	return status;
}
