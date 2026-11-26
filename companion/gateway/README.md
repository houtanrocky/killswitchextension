# Kill Switch request gateway

This service is the egress decision point for the standalone companion. It accepts one proxied HTTP request per TLS 1.2+ / HTTP/1.1 connection at `POST /v1/fetch`, evaluates the URL against configured rules, and uses the TCP peer address (`RemoteAddr`) as the client's public source IP. It deliberately ignores forwarded-IP headers. A denied request returns HTTP 451 before an upstream request is created. HTTP/2 is disabled and each response closes its connection so a later request has a newly observed peer address. Deploy the gateway directly on its TLS endpoint; a load balancer or forward proxy would hide the original peer address unless a separate trusted peer-address mechanism is designed.

Rules can be replaced with `PUT /v1/policy` using the same bearer credential. Updates are validated and persisted in `rulesFile`. The gateway rejects plaintext HTTP and requires a TLS certificate and key. The desktop app stores the bearer token protected with Windows DPAPI and syncs its rules over HTTPS. When the user enables the egress gate, the app configures Windows Firewall to block outbound connections except the companion process to the configured gateway IPs and port. It also configures the current user's Windows proxy and temporarily trusts a local inspection CA so compatible clients can send each HTTP request to the gateway.

## Config

Copy `gateway.example.json`, set a long random `bearerToken`, and provide a valid TLS certificate and key. Keep the config, private key, and rules file out of source control. Start the service with:

```powershell
$env:KILLSWITCH_GATEWAY_CONFIG = "C:\path\to\gateway.json"
go run .
```

Only applications that honor the current user's Windows proxy settings can browse through the gateway. Other applications lose outbound access while the egress gate is active. The gate verifies that no other effective outbound allow rules remain and refuses to activate if Windows policy prevents that. Certificate-pinned clients may reject local TLS inspection. The gateway evaluates each request as it arrives but cannot prevent an already-forwarded request from completing during an IP change.

## Tests

```powershell
go test ./...
```
