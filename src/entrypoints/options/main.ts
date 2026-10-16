import "./style.css";
import { readRules, replaceRules, type RuleDraft } from "~/utils/storage";

const content = document.getElementById("content");
const message = document.getElementById("message");
const ruleCount = document.getElementById("rule-count");
const searchInput = document.getElementById("rule-search") as HTMLInputElement | null;

let drafts: RuleDraft[] = [];
let searchTerm = "";

function displayMessage(text: string, error = false): void {
	if (!message) return;
	message.textContent = text;
	message.className = error ? "notice notice-error" : "notice notice-success";
}

function syncDraftsFromDom(): void {
	if (!content) return;
	content.querySelectorAll<HTMLElement>(".rule-card").forEach((card) => {
		const index = Number(card.dataset.index);
		const domain = card.querySelector<HTMLInputElement>(".domain_regex")?.value ?? "";
		const ips = card.querySelector<HTMLTextAreaElement>(".ips")?.value ?? "";
		if (Number.isInteger(index) && drafts[index]) {
			drafts[index] = { domain, ips };
		}
	});
}

function matchesSearch(draft: RuleDraft): boolean {
	if (!searchTerm) return true;
	const haystack = `${draft.domain} ${draft.ips}`.toLowerCase();
	return haystack.includes(searchTerm);
}

function updateRuleCount(visibleCount: number): void {
	if (!ruleCount) return;
	if (searchTerm && visibleCount !== drafts.length) {
		ruleCount.textContent = `${visibleCount} of ${drafts.length} rules`;
	} else {
		ruleCount.textContent = `${drafts.length} ${drafts.length === 1 ? "rule" : "rules"}`;
	}
}

function createRuleRow(draft: RuleDraft, index: number): HTMLElement {
	const row = document.createElement("article");
	row.className = "rule-card";
	row.dataset.index = String(index);

	const heading = document.createElement("div");
	heading.className = "rule-card-heading";
	const title = document.createElement("h2");
	title.textContent = "Access rule";
	heading.append(title);

	const remove = document.createElement("button");
	remove.type = "button";
	remove.className = "button button-quiet button-danger";
	remove.textContent = "Remove";
	remove.addEventListener("click", () => {
		syncDraftsFromDom();
		drafts.splice(index, 1);
		renderRules();
		displayMessage("Rule removed. Save changes to keep it removed.");
	});
	heading.append(remove);
	row.append(heading);

	const targetLabel = document.createElement("label");
	targetLabel.textContent = "Website or page";
	const targetInput = document.createElement("input");
	targetInput.className = "domain_regex";
	targetInput.type = "text";
	targetInput.placeholder = "example.com or https://example.com/private";
	targetInput.value = draft.domain;
	targetInput.autocomplete = "off";
	targetLabel.append(targetInput);
	row.append(targetLabel);

	const ipLabel = document.createElement("label");
	ipLabel.textContent = "IP conditions";
	const ipInput = document.createElement("textarea");
	ipInput.className = "ips";
	ipInput.rows = 2;
	ipInput.placeholder = "203.0.113.10, !198.51.100.20";
	ipInput.value = draft.ips;
	ipLabel.append(ipInput);
	row.append(ipLabel);

	const hint = document.createElement("p");
	hint.className = "field-hint";
	hint.textContent = "Use ! before an IP to block only on that address. Separate multiple values with commas.";
	row.append(hint);
	return row;
}

function renderRules(): void {
	if (!content) return;
	content.replaceChildren();
	const visibleDrafts = drafts
		.map((draft, index) => ({ draft, index }))
		.filter(({ draft }) => matchesSearch(draft));

	if (visibleDrafts.length === 0) {
		const empty = document.createElement("div");
		empty.className = "empty-state";
		const emptyTitle = document.createElement("strong");
		emptyTitle.textContent = searchTerm ? "No matching rules" : "No rules yet";
		const emptyText = document.createElement("span");
		emptyText.textContent = searchTerm
			? "Try a different domain, page, or IP address."
			: "Add a rule to control access by IP.";
		empty.append(emptyTitle, emptyText);
		content.append(empty);
	} else {
		for (const { draft, index } of visibleDrafts) {
			content.append(createRuleRow(draft, index));
		}
	}
	updateRuleCount(visibleDrafts.length);
}

async function initializeOptions(): Promise<void> {
	const rules = await readRules();
	drafts = rules.urls.map((url, index) => ({
		domain: url,
		ips: (rules.allowedIps[index] ?? "").replace(/\|/g, "")
	}));
	renderRules();
}

async function saveChanges(): Promise<void> {
	syncDraftsFromDom();
	try {
		await replaceRules(drafts);
		displayMessage("Rules saved. Protection is updating now.");
		await initializeOptions();
	} catch (error) {
		displayMessage(error instanceof Error ? error.message : String(error), true);
	}
}

searchInput?.addEventListener("input", () => {
	syncDraftsFromDom();
	searchTerm = searchInput.value.trim().toLowerCase();
	renderRules();
});

document.getElementById("add")?.addEventListener("click", () => {
	syncDraftsFromDom();
	searchTerm = "";
	if (searchInput) searchInput.value = "";
	drafts.push({ domain: "", ips: "" });
	renderRules();
	content?.querySelector<HTMLInputElement>(".rule-card:last-of-type .domain_regex")?.focus();
});

document.getElementById("save")?.addEventListener("click", () => {
	void saveChanges();
});

document.getElementById("reset")?.addEventListener("click", () => {
	searchTerm = "";
	if (searchInput) searchInput.value = "";
	void initializeOptions();
	displayMessage("Changes discarded.");
});

void initializeOptions();
