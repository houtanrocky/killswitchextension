# Kill Switch Companion for Windows v1.1.7

- Shows the current startup stage while protection is being enabled.
- Times out stalled gateway DNS lookup and Windows Firewall setup rather than waiting indefinitely.
- Stops a timed-out Firewall PowerShell process and attempts to restore saved firewall settings.
- Bounds firewall restoration attempts so the window can report a recovery problem instead of appearing frozen.
