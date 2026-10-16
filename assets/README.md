# Chrome Web Store assets

The canonical Store text and metadata now live in `docs/store/`. Use that directory when filling the Chrome Web Store dashboard:

- `description.txt` is ready to paste into the description field.
- `summary.txt`, `title.txt`, `category.txt`, and the URL files map to dashboard fields.
- `promo-small.txt`, `promo-marquee.txt`, and `screenshot-captions.md` contain the promotional copy and capture plan.
- `product-data.json` records current permissions, privacy behavior, status colors, and source assets.

The HTML mockups in this directory are capture references. Re-capture them after UI changes so Store screenshots show the current popup and Manage rules experience. Use Chrome's required image dimensions: screenshots at 1280x800 or 640x400, a small promo tile at 440x280, and a marquee tile at 1400x560. Use JPEG or 24-bit PNG without alpha for uploaded promotional images.

The current rendered PNGs are `store-screenshot-1-popup.png`, `store-screenshot-2-options.png`, `store-screenshot-3-features.png`, `store-promo-small.png`, and `store-promo-marquee.png`.

Keep the Store icon as a stable brand mark. The browser action icon uses the runtime status colors: green for an allowed matching rule, red for a limited matching rule, and gray when no rule matches the current page.
