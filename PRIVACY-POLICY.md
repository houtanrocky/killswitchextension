# Privacy Policy for Kill Switch Extension

**Last Updated:** November 26, 2026

Kill Switch Extension processes the minimum information needed to evaluate your local website access rules. No account is required.

## Information processed

- **Public IP address:** The extension requests the current public IP from `https://api.ipify.org` and stores status locally. A previous IP is not reused to permit configured targets after a lookup failure.
- **Active tab URL:** The current HTTP or HTTPS tab URL is read locally to determine whether a configured rule matches. It is not sent to a server or stored as browsing history.
- **Configured rules:** Domains, page targets, IP conditions, and allow or limit settings are stored in the browser's local extension storage.

The extension does not collect browsing history, page content, account information, analytics, or tracking identifiers.

## External communication

The only external communication is a plain-text request to `https://api.ipify.org` with `referrerPolicy: no-referrer`. The lookup service can see the public IP of the network making the request and ordinary request metadata. Kill Switch does not send configured domains, page URLs, rule lists, analytics, or tracking identifiers to that service.

If a lookup fails, configured targets remain blocked until a fresh IP can be checked.

## Optional Windows Companion and gateway

The separate Windows Companion can route browser traffic through an HTTPS gateway configured by the user. The gateway receives requested URLs and request data needed to fetch pages, then evaluates matching rules using the source IP it observes. Requests to URLs with no matching rule are allowed through. The gateway operator may be able to access this traffic, so use a gateway you trust. Requests already in flight may complete during an IP change.

## Permissions

- `storage` stores rules, the last verified IP, and local status metadata.
- `tabs` reads the active page URL locally and supports manual reload after a refresh request.
- `declarativeNetRequest` applies browser-level blocking rules to limited targets.
- `<all_urls>` allows configured rules to apply to HTTP and HTTPS destinations.

## Your controls

You can view, search, edit, and remove every rule from Manage rules. Uninstalling the extension removes its local storage. Google Analytics is disabled and no remote fonts are loaded.

## Children's privacy

The extension is not intended for children under 13. We do not knowingly collect information from children under 13.

## Contact and changes

Questions can be sent to [houtan.rocky@gmail.com](mailto:houtan.rocky@gmail.com) or reported through the [GitHub support page](https://houtanrocky.github.io/killswitchextension/support.html). We may update this policy by changing the date above.
