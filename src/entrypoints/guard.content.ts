import { defineContentScript } from "wxt/utils/define-content-script";
import { patternMatchesPage } from "~/utils/rules";

export default defineContentScript({
	matches: ["http://*/*", "https://*/*"],
	runAt: "document_start",
	async main() {
		const pageUrl = location.href;
		try {
			const { urls = [] } = await browser.storage.local.get("urls") as { urls?: unknown };
			const configuredUrls = Array.isArray(urls)
				? urls.filter((url): url is string => typeof url === "string")
				: [];
			if (!configuredUrls.some((pattern) => patternMatchesPage(pattern, pageUrl))) return;

			const overlay = document.createElement("div");
			overlay.setAttribute("data-kill-switch-guard", "true");
			overlay.textContent = "Checking network access…";
			Object.assign(overlay.style, {
				position: "fixed", inset: "0", zIndex: "2147483647", display: "grid",
				placeItems: "center", background: "#10131a", color: "#fff",
				font: "600 16px system-ui, sans-serif"
			});
			(document.documentElement ?? document).append(overlay);

			try {
				const result = await browser.runtime.sendMessage({ type: "check-page", url: pageUrl }) as {
					success?: boolean;
					blocked?: boolean;
				};
				if (result?.success && !result.blocked) {
					overlay.remove();
				} else {
					overlay.textContent = "Access limited by Kill Switch";
				}
			} catch {
				overlay.textContent = "Access limited by Kill Switch";
			}
		} catch (error) {
			console.error("Kill Switch could not verify this page", error);
		}
	}
});
