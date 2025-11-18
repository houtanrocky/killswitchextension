# How to Capture All Assets at Exact Dimensions

## ⚠️ IMPORTANT: Use Responsive Design Mode for Exact Dimensions

### For Screenshots (1280x800) - STEP BY STEP:

1. **Open the HTML file in Chrome** (e.g., `screenshot-1-popup.html`)

2. **Open DevTools**:
   - Press **F12** (or right-click → Inspect)

3. **Enable Responsive Design Mode**:
   - Press **Ctrl+Shift+M** (Cmd+Shift+M on Mac)
   - OR click the device toolbar icon (📱) in DevTools

4. **Set Exact Dimensions**:
   - In the responsive mode toolbar, click the dimensions dropdown
   - Select **"Edit..."** or **"Add custom device"**
   - Set width: **1280** and height: **800**
   - OR manually type in the dimensions: `1280 x 800`
   - **IMPORTANT**: Click the "..." menu (three dots) in responsive mode toolbar
   - Set **Device Pixel Ratio (DPR)** to **1** (not 2 or 3)
   - Make sure zoom is set to **100%** (check the zoom dropdown)

5. **Capture Screenshot**:
   - Press **Ctrl+Shift+P** (Cmd+Shift+P on Mac)
   - Type: `Capture screenshot`
   - Select: **"Capture screenshot"** (NOT "full size" - just regular screenshot)
   - This captures exactly what you see in the viewport (1280x800)

### For Small Promo (440x280):

1. Open `promo-small.html` in Chrome
2. Press **F12** → **Ctrl+Shift+M** (responsive mode)
3. Set dimensions to **440 x 280**
4. Press **Ctrl+Shift+P** → `Capture screenshot`
5. Select **"Capture screenshot"**

### For Marquee Promo (1400x560):

1. Open `promo-marquee.html` in Chrome
2. Press **F12** → **Ctrl+Shift+M** (responsive mode)
3. Set dimensions to **1400 x 560**
4. Press **Ctrl+Shift+P** → `Capture screenshot`
5. Select **"Capture screenshot"**

## Alternative: Using Browser Zoom Method

If responsive mode doesn't work:

1. Open HTML file in Chrome
2. Press **Ctrl+0** (Cmd+0 on Mac) to reset zoom to 100%
3. Press **F12** to open DevTools
4. In Console, run: `document.body.style.zoom = "1"`
5. Use your OS screenshot tool:
   - **Windows**: Snipping Tool, select exact area
   - **Mac**: Cmd+Shift+4, drag to select
6. Verify dimensions after capture

## Verification After Capture

**Check image dimensions:**
- **Windows**: Right-click image → Properties → Details tab → Check "Dimensions"
- **Mac**: Right-click image → Get Info → Check dimensions
- **Online**: Upload to imgur or similar to see dimensions

**Required dimensions:**
- Screenshots: **1280 x 800** pixels (or 640 x 400)
- Small promo: **440 x 280** pixels
- Marquee promo: **1400 x 560** pixels

## Troubleshooting

**If dimensions are still wrong (e.g., getting 2560x1600 instead of 1280x800):**
1. **Set Device Pixel Ratio to 1**: In responsive mode, click the "..." menu → Set DPR to **1** (not 2 or 3)
2. Make sure browser zoom is exactly 100%
3. Make sure responsive mode dimensions match exactly
4. Try using "Capture screenshot" (not "full size")
5. Verify the HTML file has correct viewport meta tag (should be set)

**If you captured at 2x resolution (2560x1600):**
- You can resize the image to exactly half: **1280x800**
- Use any image editor (Paint, Photoshop, GIMP, online tools)
- Or use command line: `magick input.png -resize 1280x800 output.png` (ImageMagick)

**If background is inconsistent:**
- The files have been updated with `background-size: 100% 100%` to ensure full coverage
- If still seeing issues, try refreshing the page (Ctrl+R)

## Quick Test

To verify your setup:
1. Open any HTML file
2. Press F12 → Ctrl+Shift+M
3. Set to 1280x800
4. Check if the content fills the entire viewport
5. If yes, capture should work perfectly
