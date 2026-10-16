import { defineConfig } from "wxt";

export default defineConfig({
	srcDir: "src",
	outDir: ".output",
	manifestVersion: 3,
	manifest: {
		name: "Kill Switch Extension",
		short_name: "Kill Switch Extension",
		description:
			"Block or allow websites by your public IP. Create rules for domains and individual pages.",
		browser_specific_settings: {
			gecko: {
				id: "kill-switch-extension@example.com",
				data_collection_permissions: {
					required: ["none"]
				}
			}
		},
		permissions: ["storage", "declarativeNetRequest", "tabs"],
		host_permissions: ["<all_urls>"],
		declarative_net_request: {
			rule_resources: []
		},
		icons: {
			48: "/img/icon-48x48-red.png",
			128: "/img/icon-128x128-red.png",
			512: "/img/icon-512x512-red.png"
		},
		action: {
			default_icon: {
				16: "/img/icon-48x48-grey.png",
				32: "/img/icon-48x48-grey.png",
				48: "/img/icon-48x48-grey.png",
				128: "/img/icon-128x128-grey.png"
			},
			default_title: "Kill Switch Extension"
		}
	}
});
