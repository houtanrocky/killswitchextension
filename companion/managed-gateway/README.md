# Managed gateway deployment

This Worker is the managed HTTPS relay used by the standalone Windows Companion. Each installation receives a random bearer key; its policy is isolated in a Durable Object keyed by a hash of that key. The key is stored by the Companion using Windows DPAPI. The service has no account, email, or recovery flow.

## Deploy into a Cloudflare account

1. Install a current Node.js LTS release.
2. From this directory run `npm install`, then `npm run dry-run`.
3. Run `npx wrangler login` and `npm run deploy`.
4. Wrangler prints the Worker URL, normally `https://killswitch-managed-gateway.<your-account-subdomain>.workers.dev`.
5. Set `endpoint` in `../../docs/managed-gateway.json` to that URL and change `enabled` to `true`.
6. Publish the repository's GitHub Pages site so the Companion can discover the setting at `https://houtanrocky.github.io/killswitchextension/managed-gateway.json`.

Never put device keys or other secrets in the discovery file. It is public configuration.

## Before enabling public registration

The registration endpoint is open and rate-limited per observed IPv4 address. Cloudflare's standard Worker and Durable Object limits still apply, and a distributed registration flood can increase usage. Configure billing alerts and spending controls, review Worker and Durable Object usage, and verify abuse controls for the Cloudflare plan in use before publishing `enabled: true`. Do not promise a fixed price or unlimited service.

The Worker does not intentionally log destination URLs, request headers, request bodies, or client IPs. Cloudflare operates the network and platform and may process connection metadata under its own terms. Request URLs, headers, and bodies pass through the Worker to the destination. Tell users this before enabling protection. An already-forwarded request may finish if the user's public IP changes while the request is in flight.

## Local development

Run `npm run dev` to start Wrangler's local development server. The production endpoint remains disabled until the discovery file is published with a real Worker URL.
