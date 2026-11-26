# Kill Switch Companion for Windows v1.1.0

- Added a Windows installer and uninstaller with Start menu integration.
- Added a highest-privilege scheduled task to show the companion at sign-in by default; the installer can skip startup registration.
- Added a Start menu shortcut and an Apps & Features uninstall entry.
- The companion opens visibly at sign-in so users can see whether the egress gate is active.
- Startup alone does not enable protection. Configure and sync a trusted HTTPS gateway, then enable the Windows egress gate in the app.

The installer includes a self-contained Windows x64 build. No .NET runtime installation is required.
