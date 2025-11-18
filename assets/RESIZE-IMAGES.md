# Quick Image Resize Guide

If you captured images at 2x resolution (e.g., 2560x1600 instead of 1280x800), here's how to resize:

## Method 1: Online Tools (Easiest)

1. Go to https://www.iloveimg.com/resize-image
2. Upload your image
3. Select "Custom size"
4. Enter exact dimensions:
   - Screenshots: **1280 x 800**
   - Small promo: **440 x 280**
   - Marquee promo: **1400 x 560**
5. Make sure "Maintain aspect ratio" is **UNCHECKED**
6. Download the resized image

## Method 2: Windows Paint

1. Open image in Paint
2. Click **Resize** button
3. Select **Pixels** (not percentage)
4. Uncheck "Maintain aspect ratio"
5. Enter exact dimensions:
   - Screenshots: **1280 x 800**
   - Small promo: **440 x 280**
   - Marquee promo: **1400 x 560**
6. Click **OK**
7. Save as PNG or JPEG

## Method 3: Mac Preview

1. Open image in Preview
2. Go to **Tools** → **Adjust Size**
3. Enter exact dimensions:
   - Screenshots: **1280 x 800**
   - Small promo: **440 x 280**
   - Marquee promo: **1400 x 560**
4. Make sure "Scale proportionally" is **UNCHECKED**
5. Click **OK**
6. Save

## Method 4: Command Line (ImageMagick)

```bash
# Screenshots
magick screenshot.png -resize 1280x800! screenshot-resized.png

# Small promo
magick promo-small.png -resize 440x280! promo-small-resized.png

# Marquee promo
magick promo-marquee.png -resize 1400x560! promo-marquee-resized.png
```

The `!` forces exact dimensions without maintaining aspect ratio.

## Verification

After resizing, verify dimensions:
- **Windows**: Right-click → Properties → Details → Check "Dimensions"
- **Mac**: Right-click → Get Info → Check dimensions
- Should be exactly: 1280x800, 440x280, or 1400x560

