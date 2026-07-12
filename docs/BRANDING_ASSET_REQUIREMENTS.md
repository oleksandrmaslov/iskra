# Iskra branding asset handoff

The previous `design_assets/` pack is intentionally retired. Please supply a
fresh approved package with the following files. Source files should use sRGB,
transparent backgrounds, embedded geometry, and no linked raster assets.

## Required masters

| File | Exact requirement |
|---|---|
| `iskra-symbol.svg` | Square SVG/viewBox, paths only, transparent |
| `iskra-wordmark-horizontal-light.svg` | Transparent horizontal lockup for dark backgrounds, tightly cropped, text converted to paths |
| `iskra-wordmark-horizontal-dark.svg` | Transparent horizontal lockup for light backgrounds, tightly cropped, text converted to paths |
| `iskra-symbol-monochrome.svg` | One-color mark; also provide black, white, or `currentColor` variants if they differ |
| `iskra-symbol-small-safe.svg` | Pixel-tuned simplified mark designed for 16–32 px, not merely a scaled full logo |
| `iskra-app-icon-master.svg` | Square icon composition with a generous safe area and transparent background |
| `iskra-app-icon-1024.png` | 1024×1024, 32-bit RGBA, transparent |

Please also provide a short brand guide containing exact HEX colors, semantic UI
colors, clear space, minimum sizes, light/dark/high-contrast rules, and forbidden
uses. PASS, FAIL, warning, and disabled colors must remain accessible semantic
states rather than decorative brand colors.

## Platform icon exports

- Windows multi-frame `.ico`: 16, 20, 24, 32, 40, 48, 64, 128, and 256 px,
  all 32-bit RGBA.
- macOS iconset/`.icns`: 16, 32, 64, 128, 256, 512, and 1024 px (the normal
  and `@2x` counterparts).
- Linux: scalable SVG plus transparent PNG at 16, 24, 32, 48, 64, 128, 256,
  and 512 px.

If only the SVG master and 1024 PNG are supplied, the repository can generate
the derived sizes after visual approval of the small-safe rendering.

## Windows installer assets

| File | Exact requirement |
|---|---|
| `burn-logo.png` | 64×64, 32-bit RGBA |
| `wix-banner.bmp` | 493×58, 24-bit BMP |
| `wix-dialog.bmp` | 493×312, 24-bit BMP |
| `iskra.ico` | The multi-frame Windows icon above; used by executable, window, Start menu, Add/Remove Programs, MSI, and bundle |

Keep important artwork away from text-safe areas in the WiX compositions. Do not
embed version numbers into installer art.

## Repository and release artwork

- `github-readme-banner-dark.png`: 1400×420.
- `github-readme-banner-light.png`: 1400×420, if both themes are approved.
- `github-social-preview.png`: 1280×640.
- Do not provide a permanent UI screenshot yet; generate screenshots from the
  approved Avalonia redesign so operator text stays Ukrainian and current.

## Typography

If the design requires a bundled font, provide TTF/OTF Regular, Medium,
Semibold, and Bold with Ukrainian Cyrillic coverage plus a redistribution
license. Otherwise specify approved system-font fallbacks for Windows, Linux,
and macOS.

## Decisions to include with the files

1. Primary lockup: `ISKRA`, `ІСКРА`, or both.
2. Marketing tagline language/text. Operator UI remains Ukrainian only.
3. Light-only or light+dark themes (light+dark recommended).
4. Approved color values and font/license.
5. Whether the symbol may appear without the wordmark at small sizes.

