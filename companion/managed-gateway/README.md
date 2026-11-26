# Managed gateway deployment

This Worker is the managed HTTPS relay used by the standalone Windows Companion. Each installation receives a random bearer key; its policy is isolated in a Durable Object keyed by a hash of that key. The key is stored by the Companion using Windows DPAPI. The service has no account, email, or recovery flow.

## Connect this repository in Cloudflare

1. In Cloudflare, open **Workers & Pages** and choose **Create application** / **Connect to Git**. Install or authorize the Cloudflare GitHub App for `houtanrocky/killswitchextension`.
2. Select the `master` branch.
3. Set **Root directory** to `companion/managed-gateway`.
4. Leave **Build command** blank. Set **Deploy command** to `npm run deploy`. Leave preview deployments off unless you explicitly want non-production branches to deploy.
5. Keep the Worker name `killswitch-managed-gateway`, and ensure a `workers.dev` subdomain is enabled for the account. Cloudflare should then deploy the Worker and both SQLite Durable Object classes from `wrangler.jsonc`.

The first deployment prints the Worker URL, normally `https://killswitch-managed-gateway.<your-account-subdomain>.workers.dev`. The account subdomain is assigned by Cloudflare, so it cannot be safely guessed in this repository. Copy the exact URL into `endpoint` in `../../docs/managed-gateway.json` and change `enabled` to `true`, then commit and push that one config change. GitHub Pages will publish the discovery config at `https://houtanrocky.github.io/killswitchextension/managed-gateway.json`; the Companion will then connect automatically. Keep the Worker public and do not put it behind Cloudflare Access, because the Companion must reach it directly.

If deploying locally instead, from this directory run `npm install`, `npm run dry-run`, `npx wrangler login`, then `npm run deploy`.

Never put device keys or other secrets in the discovery file. It is public configuration.

## Before enabling public registration

The registration endpoint is open and rate-limited per observed IPv4 address. Cloudflare's standard Worker and Durable Object limits still apply, and a distributed registration flood can increase usage. Configure billing alerts and spending controls, review Worker and Durable Object usage, and verify abuse controls for the Cloudflare plan in use before publishing `enabled: true`. Do not promise a fixed price or unlimited service.

The Worker does not intentionally log destination URLs, request headers, request bodies, or client IPs. Cloudflare operates the network and platform and may process connection metadata under its own terms. Request URLs, headers, and bodies pass through the Worker to the destination. Tell users this before enabling protection. An already-forwarded request may finish if the user's public IP changes while the request is in flight.

## Local development

Run `npm run dev` to start Wrangler's local development server. The production endpoint remains disabled until the discovery file is published with a real Worker URL.
