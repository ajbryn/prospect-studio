# Fonts (added in chunk C10)

Place the brand's font files here and list them in `../brand.json`.

For the placeholder brand, download these SIL Open Font License fonts from the Google Fonts repository (github.com/google/fonts, `ofl/` folder) and include each family's `OFL.txt`:

- Barlow Condensed: Bold, SemiBold
- Barlow: Regular, Medium, SemiBold
- IBM Plex Mono: Medium

If the files are missing, the renderer must fall back to the `fallback` stacks in `brand.json` and report a `get_brand_kit` warning. It must never fetch fonts from the network at render time.
