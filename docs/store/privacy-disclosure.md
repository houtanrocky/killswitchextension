# Store privacy disclosure

## Data handled

- **Public IP address:** requested from `api.ipify.org` and stored locally as the last verified IP for rule evaluation.
- **Active tab URL:** read locally to determine whether a configured rule matches the current page.
- **Configured rules:** domains, page targets, IP conditions, and allow/limit settings are stored locally.

## Data sent outside the browser

Only the public-IP lookup request leaves the browser. The lookup service can see the public IP and ordinary request metadata. Kill Switch does not send configured targets, page URLs, browsing history, rule lists, analytics, or tracking identifiers.

## Permissions

- `storage`: local rules and status data.
- `tabs`: active-page status and manual tab reload.
- `declarativeNetRequest`: browser-level blocking for limited targets.
- `<all_urls>`: apply configured rules to HTTP and HTTPS destinations.

## User controls

Users can search, edit, and remove rules in Manage rules. Uninstalling the extension removes its local storage. Google Analytics is disabled.
