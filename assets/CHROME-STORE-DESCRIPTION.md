# Kill Switch Extension - Chrome Web Store Description

## What is Kill Switch?

Kill Switch is a powerful browser extension that gives you complete control over website access based on your public IP address. Whether you're concerned about privacy, security, or need to restrict access when your IP changes, Kill Switch automatically blocks or allows websites according to your configured rules.

## Why You Need Kill Switch

**Protect Your Privacy**: Automatically block websites when you're on an untrusted network or when your IP address changes unexpectedly. Perfect for preventing accidental access to sensitive sites when your VPN disconnects or your network changes.

**Security-First Approach**: Create whitelist rules that only allow access to specific websites when you're on your trusted IP address. If your IP changes (VPN disconnect, network switch, etc.), those sites are immediately blocked until you're back on your authorized IP.

**Flexible Access Control**: Use blacklist rules to block specific websites when you're on certain IP addresses. Ideal for preventing access to distracting sites when working from different locations, or blocking sites that shouldn't be accessed from public networks.

**Real-Time Protection**: Kill Switch works instantly - as soon as your IP address changes, the extension automatically applies your rules. No manual intervention needed. The extension continuously monitors your public IP and enforces your access rules in real-time.

## Key Features

✅ **IP-Based Whitelisting**: Only allow access to specific websites when your IP matches your configured whitelist. All other access is automatically blocked.

✅ **IP-Based Blacklisting**: Block specific websites when your IP matches your configured blacklist. Perfect for preventing access to certain sites from specific networks.

✅ **One-Click Rule Creation**: Quickly whitelist or blacklist the current domain or page with your current IP address using simple one-click buttons in the popup menu.

✅ **Domain or Page-Level Control**: Choose to block entire domains or specific pages. Set rules for `example.com` (entire domain) or `https://example.com/specific-page` (single page).

✅ **Multiple IP Support**: Configure multiple IP addresses per rule. Support for both whitelist (allow only these IPs) and blacklist (block on these IPs) modes.

✅ **Automatic IP Detection**: The extension automatically detects your public IP address using multiple reliable services. No manual IP entry required.

✅ **Fallback Protection**: If IP detection services are temporarily unavailable, Kill Switch automatically blocks all configured domains to ensure maximum security.

✅ **Easy Rule Management**: Manage all your rules from a simple, intuitive options page. Add, edit, or remove rules with ease.

✅ **Visual Status Indicators**: Color-coded extension icon shows your current status:
- 🔴 Red: Blocking domains (IP doesn't match or providers unavailable)
- 🟢 Green: All domains allowed (IP matches whitelist)
- ⚫ Grey: No rules configured

## How It Works

1. **Install and Configure**: Install Kill Switch and set up your first rule using the popup menu or options page.

2. **Automatic IP Detection**: The extension continuously monitors your public IP address using multiple reliable services.

3. **Rule Evaluation**: When you visit a website, Kill Switch checks if it matches any of your configured rules and whether your current IP is allowed or blocked.

4. **Automatic Enforcement**: Websites are automatically blocked or allowed based on your rules. No page loads, no content access - complete protection.

5. **Real-Time Updates**: As your IP changes, rules are automatically re-evaluated and enforced instantly.

## Use Cases

**VPN Users**: Automatically block sensitive websites if your VPN disconnects unexpectedly. Only allow access when connected to your trusted VPN IP.

**Remote Workers**: Restrict access to certain websites when working from public networks or untrusted locations. Whitelist work-related sites to only work from your office IP.

**Privacy-Conscious Users**: Prevent accidental access to personal accounts or sensitive sites when your network changes. Block social media or banking sites when not on your home network.

**Network Security**: Create rules that block access to specific sites when on public Wi-Fi or untrusted networks. Protect yourself from accessing sensitive services on insecure connections.

**Parental Controls**: Use IP-based rules to restrict access to certain websites when children are on different networks (school, friend's house, etc.).

**Corporate Security**: Enforce access policies based on network location. Ensure employees can only access certain sites when on the corporate network.

## Getting Started

1. **Install the Extension**: Click "Add to Chrome" to install Kill Switch.

2. **Create Your First Rule**: 
   - Click the extension icon
   - Visit a website you want to control
   - Click "Whitelist this domain with IP" or "Blacklist this domain with IP"
   - Your current IP is automatically added to the rule

3. **Manage Rules**: Click "MY RULES" in the popup or right-click the extension icon → Options to manage all your rules.

4. **View Current IP**: Your current public IP address is displayed in the popup menu for reference.

## Technical Details

- **Manifest V3 Compatible**: Built with the latest Chrome extension standards for security and performance.

- **Privacy-Focused**: All rules and IP addresses are stored locally on your device. No data is sent to external servers except for IP detection services.

- **Lightweight**: Minimal resource usage. The extension only activates when needed and doesn't slow down your browsing.

- **Reliable IP Detection**: Uses multiple IP detection services with automatic fallback to ensure accurate IP detection even if one service is down.

- **No Tracking**: Kill Switch doesn't track your browsing habits or collect any personal information. All functionality is local to your browser.

## Safety & Privacy

Kill Switch requires permission to access all websites (`<all_urls>`) because it needs to evaluate and potentially block requests to any website based on your rules. This is essential for the extension's core functionality. However:

- All rule data is stored locally on your device
- No browsing data is collected or transmitted
- No tracking or analytics
- Open source code available for review
- No external servers except for IP detection (which only receives your public IP, same as any website)

## Support

For issues, questions, or feature requests, please visit the extension's GitHub repository or contact the developer.

## Important Notes

- Kill Switch blocks websites at the network level. Once a site is blocked, it cannot be accessed until your IP matches the rule or the rule is removed.

- The extension requires internet access to detect your public IP address. If you're offline, the extension will use the last known IP address.

- IP addresses can change frequently on some networks. Make sure to update your rules if your IP changes and you need to maintain access.

- The extension works best when you have a stable public IP address or when using VPN services with static IPs.

---

**Take control of your web access today. Install Kill Switch and protect your browsing based on your network location.**

