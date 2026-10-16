# Repository Guidelines

## Project Structure & Module Organization

This is a WXT Manifest V3 extension. Runtime source lives under `src/`: `src/entrypoints/background.ts` is the service worker, while `src/entrypoints/popup/` and `src/entrypoints/options/` contain the HTML, TypeScript, and CSS for each UI. Shared behavior is in `src/utils/`, including IP lookup, rule evaluation, storage, page status, and background refresh logic. Runtime icons are copied from `public/img/` into each build. `assets/` contains Chrome Web Store mockups and documentation. WXT generates `.wxt/` types and `.output/` build artifacts; do not edit either directory.

## Build, Test, and Development Commands

Install dependencies with `npm install`, then use:

```powershell
npm run dev              # WXT development mode with reload support
npm run build            # Chrome MV3 production build
npm run build:firefox    # Firefox production build
npm run zip              # Package the Chrome build
npm run typecheck        # Strict TypeScript validation
npm test                 # Run the Vitest suite
npm run verify           # Typecheck, test, and build Chrome and Firefox
```

Load `.output/chrome-mv3` through `chrome://extensions` for a manual smoke test. Use `wxt -b firefox` or `.output/firefox-mv3` for Firefox checks.

## Coding Style & Naming Conventions

Use TypeScript with strict checking, tabs for indentation, semicolons, and descriptive camelCase names. Keep browser API calls inside entrypoint runtime functions or shared functions invoked by them; WXT imports entrypoints during build-time manifest generation. Use `PascalCase` for types and interfaces, and lowercase hyphenated asset names. Use the `~/` alias for imports from `src/`.

## Testing Guidelines

Tests use Vitest with WXT’s testing plugin and browser API mocks. Keep tests in `test/**/*.test.ts`, group them by behavior, and cover rule decisions, storage updates, provider failures, icon states, and generated manifest behavior. Run `npm test` and `npm run typecheck` before submitting changes. Add a live browser smoke test when changing entrypoints, permissions, or manifest behavior.

## Commit & Pull Request Guidelines

Use short imperative commit subjects such as `Add ...`, `Fix ...`, and `Remove ...`. Pull requests should describe user-visible behavior, list `npm test`, `npm run typecheck`, and build results, identify permission or manifest changes, and include popup/options screenshots when UI changes. Do not commit `.output/`, `.wxt/`, `node_modules/`, secrets, or packaged artifacts.
