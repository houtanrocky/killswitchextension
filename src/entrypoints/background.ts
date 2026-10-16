import { defineBackground } from "wxt/utils/define-background";
import { refreshRules, registerBackgroundListeners } from "~/utils/background";

export default defineBackground(() => {
	registerBackgroundListeners();
	void refreshRules("boot");
});
