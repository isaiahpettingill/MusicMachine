# MusicMachine icon

Original, manually authored SVG artwork for MusicMachine, distributed under the
repository's [MIT license](../../LICENSE). It uses the flat geometric forms,
layered color, restrained shadow, and top highlight associated with the Papirus
visual style. No existing Papirus asset was copied or incorporated. No image
model was used.

The musical note's **circular notehead is the comet's snowball**. One smooth,
curved golden tail flows into it. The eighth-note flag curves toward the upper
left in the same direction as the comet tail. There is no background or tile: all
assets have transparent backgrounds. A fine muted edge keeps the ivory note
visible against light and dark desktop/browser chrome.

## Assets

- `musicmachine.svg`: editable, self-contained 256 × 256 vector master
- `musicmachine.ico`: Windows application icon and browser favicon, with native
  16, 24, 32, 48, 64, 128, and 256 px transparent PNG frames
- `musicmachine-{size}.png`: transparent square PNGs at 16, 24, 32, 48, 64, 128,
  180, 192, 256, 512, and 1024 px
- `render.py`: reproducible SVG-to-PNG/ICO rendering and validation with Inkscape
  and Pillow; run `python assets/icon/render.py` from the repository root

Use the SVG as `favicon.svg` with MIME type `image/svg+xml`, the ICO as
`favicon.ico` for a fallback, and the 180 px PNG for an optional Apple touch icon.
Use the 256 or 512 px PNG for Linux launchers and the application window icon.

The SVG has no fonts, external references, raster content, scripts, or filters.
PNG transparency, exact dimensions, and all ICO entries were checked. The
16, 24, 32, and 48 px results were visually inspected on light and dark surfaces.
