"use strict";

const IP_ENDPOINTS = [
	"https://api.ipify.org",
	"https://ifconfig.me/ip",
	"https://ip.seeip.org"
];

function getActiveTab() {
	return new Promise((resolve, reject) => {
		chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
				return;
			}
			resolve(tabs && tabs.length > 0 ? tabs[0] : null);
		});
	});
}

function storageGet(keys) {
	return new Promise((resolve, reject) => {
		chrome.storage.local.get(keys, (result) => {
			const error = chrome.runtime.lastError;
			if (error) {
				reject(error);
				return;
			}
			resolve(result);
		});
	});
}

async function fetchPublicIp({ allowCacheFallback = true } = {}) {
	let lastError = null;
	for (const endpoint of IP_ENDPOINTS) {
		try {
			const response = await fetch(endpoint, { cache: "no-store" });
			if (!response.ok) {
				throw new Error(`IP lookup failed (${response.status})`);
			}
			const text = (await response.text()).trim();
			if (!isIP(text)) {
				throw new Error(`Invalid IP returned: ${text}`);
			}
			return text;
		} catch (error) {
			lastError = error;
		}
	}

	if (allowCacheFallback) {
		const { lastKnownIp } = await storageGet(["lastKnownIp"]);
		if (lastKnownIp && isIP(lastKnownIp)) {
			console.warn("Using cached IP after lookup failure.");
			return lastKnownIp;
		}
	}

	throw lastError || new Error("Unable to determine IP address.");
}

async function saveUrlRule(domainOnly, whitelist) {
	try {
		const tab = await getActiveTab();
		if (!tab || !tab.url) {
			throw new Error("No active tab URL available.");
		}

		let target = tab.url;
		if (domainOnly) {
			target = getHostFromURL(tab.url);
			if (!target) {
				throw new Error("Unable to parse domain from the current tab.");
			}
		}

		const ip = await fetchPublicIp();

		const ipValue = whitelist ? ip : `!${ip}`;
		await new Promise((resolve) => {
			saveRule(target, ipValue, false, resolve);
		});

		await new Promise((resolve, reject) => {
			chrome.runtime.sendMessage({ type: "update" }, (response) => {
				const error = chrome.runtime.lastError;
				if (error) {
					reject(error);
					return;
				}
				if (response && response.success === false) {
					reject(new Error(response.error));
					return;
				}
				resolve();
			});
		});
	} catch (error) {
		alert(error.message || "Unexpected error while saving the rule.");
		return;
	}

	setTimeout(() => {
		window.close();
	}, 300);
}

async function initializePopup() {
	const ipClasses = document.getElementsByClassName("ip");
	const setText = (text) => {
		for (let i = 0; i < ipClasses.length; i++) {
			ipClasses[i].textContent = text;
		}
	};

	try {
		const ip = await fetchPublicIp();
		setText(ip);
	} catch (error) {
		setText("Unavailable");
		console.error("Failed to fetch IP", error);
	}
}

document.getElementById("wl_domain").addEventListener("click", () => {
	saveUrlRule(true, true);
});

document.getElementById("wl_url").addEventListener("click", () => {
	saveUrlRule(false, true);
});

document.getElementById("bl_domain").addEventListener("click", () => {
	saveUrlRule(true, false);
});

document.getElementById("bl_url").addEventListener("click", () => {
	saveUrlRule(false, false);
});

document.getElementById("rules").addEventListener("click", () => {
	chrome.runtime.openOptionsPage();
});

window.onload = function () {
	initializePopup();
};

