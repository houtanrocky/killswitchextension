# Kill Switch Companion (Windows first)

The companion is a standalone desktop rule manager. Its local rule file is stored at `%LOCALAPPDATA%\KillSwitch\rules.json`; it does not need the browser extension to edit rules, import/export extension data, or inspect the current public IP.

The app shows its branded icon in the Windows taskbar while the window is open and in the notification area while it is running. Double-click the notification-area icon to reopen the window, or right-click it to open or exit.

Download the self-contained Windows x64 executable from the [latest GitHub release](https://github.com/houtanrocky/killswitchextension/releases/latest).

## Current implementation

- Edit and remove website or page rules.
- Import and export rules in the browser extension's `urls` / `allowed_ips` JSON shape.
- Preserve IPv4 allow-list and `!IPv4` deny-list semantics.
- Validate rule inputs and reject duplicate targets.
- Save rules atomically to a local JSON file.
- Query the current public IPv4 address without using a cached result.
- Evaluate whether that address is limited for each configured target.
- Refresh the displayed IP on startup and every minute.
- Optionally sync rules to the separate HTTPS gateway service. Gateway credentials are encrypted for the current Windows user with DPAPI.
- Enable a Windows Firewall egress gate after syncing policy. The app saves the current outbound profile actions and enabled outbound allow rules, disables those exceptions, blocks outbound traffic by default, and adds a single allow rule for the companion executable to the resolved gateway IPs and port. If Windows has the dynamic Internet Connection Sharing allow rule, the app adds a service-scoped outbound block for `SharedAccess` while the gate is active. It rejects other unmodifiable allow rules and verifies the effective profiles and exceptions before reporting the gate active.
- Start a loopback proxy and set the current user's Windows proxy to it. The app temporarily trusts its generated inspection CA in the current user's Windows certificate store.
- Send every parsed HTTP request through the gateway individually. It fails closed when the gateway is unavailable and returns the gateway's limited response to the client.

The egress gate requires administrator rights and restricts all applications while enabled: only the companion process can make an outbound TCP connection to the configured gateway address and port. Applications that honor the Windows proxy can browse through the local proxy; applications that do not honor it lose outbound connectivity. Certificate-pinned applications may reject intercepted TLS. The gateway checks each fetch using the public source address it observes at that time. An already-forwarded or in-flight request may still complete during an IP change, so the app makes no zero-request guarantee.

## Request gate and limits

The gateway checks the source IP on each `/v1/fetch` connection, ignores forwarded-IP headers, and evaluates the requested URL before forwarding it upstream. It also provides an authenticated `/v1/ip` endpoint so the companion can display the address the gateway sees while other outbound traffic is blocked. The gateway uses the network address it sees at request time. This cannot promise that an already-forwarded or in-flight request will not complete during an IP change.

The egress gate refuses activation if Windows Firewall is disabled, if the effective outbound profiles do not block, or if any other outbound allow rules remain. If policy or group settings prevent a gateway-only configuration, activation rolls back and the app stays inactive. A power loss or process crash leaves outbound traffic blocked; launching the app again as administrator restores the saved firewall and proxy settings.

## Gateway

The separate Go gateway requires a public HTTPS endpoint with a certificate and a secret bearer token. See [gateway/README.md](gateway/README.md) for configuration and operation. Syncing updates policy; the **Enable Windows egress gate** control applies the machine-wide outbound restriction and configures the Windows user proxy.

## Build and tests

```powershell
dotnet test companion/KillSwitch.Companion.Tests/KillSwitch.Companion.Tests.csproj
dotnet build companion/KillSwitch.Companion/KillSwitch.Companion.csproj
dotnet publish companion/KillSwitch.Companion/KillSwitch.Companion.csproj -p:PublishProfile=win-x64
```

The `win-x64` publish profile creates a self-contained, single-file Windows app under `companion/KillSwitch.Companion/bin/Release/net10.0-windows/win-x64/publish/`; users do not need to install the .NET runtime. The companion application runs independently of the browser extension. Its IP-aware enforcement still requires the separately hosted HTTPS gateway described above.

Manual Windows smoke test (run the app as administrator): configure a TLS gateway and rules, enable the egress gate, verify a direct outbound request from another process fails, and verify a browser honoring Windows proxy settings can reach an allowed target through the gateway. Test a limited target (451), gateway outage (502), and gateway policy sync failure (503). Change the public egress IP and confirm the next proxied fetch is evaluated against the gateway's newly observed peer IP. Disable the gate and confirm prior firewall profile settings, outbound allow-rule states, proxy settings, and certificate state are restored. Requests already in flight may finish during an IP change.

An elevated live firewall integration test is also available on an approved Windows machine. It temporarily applies the gateway-only policy and verifies all active profiles and allow rules, then restores the saved state. If Windows has an unmodifiable dynamic or Group Policy allow exception, it verifies the gate refuses without changing firewall state:

```powershell
$env:KILLSWITCH_RUN_WINDOWS_FIREWALL_INTEGRATION = "1"
dotnet test companion/KillSwitch.Companion.Tests/KillSwitch.Companion.Tests.csproj --filter TestCategory=WindowsFirewallIntegration
Remove-Item Env:KILLSWITCH_RUN_WINDOWS_FIREWALL_INTEGRATION
```
