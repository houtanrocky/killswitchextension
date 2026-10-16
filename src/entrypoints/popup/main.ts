import "./style.css";
import { fetchPublicIp } from "~/utils/ip";
import { getPopupState, saveUrlRule } from "~/utils/popup";

const statusCard = document.getElementById("status-card");
const statusLabel = document.getElementById("status-label");
const pageTarget = document.getElementById("page-target");
const statusDetail = document.getElementById("status-detail");
const currentIp = document.getElementById("current-ip");
const providerStatus = document.getElementById("provider-status");
const actionButtons = Array.from(document.querySelectorAll<HTMLButtonElement>(".action-button"));

const statusCopy = {
	grey: { label: "No rule for this page", detail: "This page is outside your configured protection." },
	green: { label: "Allowed on this IP", detail: "Your current connection matches an access rule." },
	red: { label: "Limited on this IP", detail: "This page is blocked until your IP matches an allowed rule." }
};

function setText(element: HTMLElement | null, value: string): void {
	if (element) element.textContent = value;
}

async function initializePopup(): Promise<void> {
	try {
		const state = await getPopupState();
		const copy = statusCopy[state.status];
		statusCard?.classList.remove("status-grey", "status-green", "status-red");
		statusCard?.classList.add(`status-${state.status}`);
		setText(statusLabel, copy.label);
		setText(pageTarget, state.host ?? "Current page");
		setText(statusDetail, copy.detail);
		setText(currentIp, state.currentIp ?? "Unavailable");
		setText(providerStatus, state.providerUnavailable ? "Provider unavailable" : "Stored locally");

		if (!state.currentIp && !state.providerUnavailable) {
			try {
				const ip = await fetchPublicIp();
				setText(currentIp, ip);
			} catch {
				setText(currentIp, "Unavailable");
				setText(providerStatus, "Provider unavailable");
			}
		}
	} catch (error) {
		setText(statusLabel, "Status unavailable");
		setText(statusDetail, "Reload the extension and try again.");
		setText(currentIp, "Unavailable");
		console.error("Failed to initialize popup", error);
	}
}

function bindRuleButton(id: string, domainOnly: boolean, whitelist: boolean): void {
	document.getElementById(id)?.addEventListener("click", async () => {
		const button = document.getElementById(id) as HTMLButtonElement | null;
		actionButtons.forEach((item) => { item.disabled = true; });
		if (button) button.classList.add("is-loading");
		try {
			await saveUrlRule(domainOnly, whitelist);
			setText(statusLabel, whitelist ? "Page allowed" : "Page limited");
			setText(statusDetail, "Your rule was saved and protection is updating.");
			window.setTimeout(() => window.close(), 450);
		} catch (error) {
			window.alert(error instanceof Error ? error.message : String(error));
			actionButtons.forEach((item) => { item.disabled = false; });
			button?.classList.remove("is-loading");
		}
	});
}

bindRuleButton("wl_domain", true, true);
bindRuleButton("bl_domain", true, false);
bindRuleButton("wl_url", false, true);
bindRuleButton("bl_url", false, false);

for (const id of ["rules", "manage-rules"]) {
	document.getElementById(id)?.addEventListener("click", () => {
		void browser.runtime.openOptionsPage();
	});
}

void initializePopup();
