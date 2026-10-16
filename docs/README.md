# GitHub Pages site

The `docs/` directory is a static GitHub Pages site for Kill Switch Extension.

## Local preview

From the repository root, run:

```powershell
python -m http.server 8080 --directory docs
```

Open `http://localhost:8080/` in a browser.

## Publishing

The repository includes `.github/workflows/pages.yml`. In GitHub, open **Settings → Pages** and select **GitHub Actions** as the source. A push to the default branch publishes the contents of `docs/` to:

`https://houtanrocky.github.io/killswitchextension/`
