# MidTerm visual assets

This directory contains reusable product artwork, screenshots, launch exports,
and the scripts that regenerate them. It is an asset workspace, not product
documentation or a public roadmap.

Current product truth lives in:

- the root [README](../../README.md)
- the [feature guide](../FEATURES.md)
- the [architecture guide](../ARCHITECTURE.md)
- [tlbx.ai](https://tlbx.ai)

## Canonical asset sets

- `readme/` contains the SVG artwork used by the repository README.
- `Screenshots/` contains real MidTerm product captures.
- `launch-assets-2026-07/` contains raster exports derived from the README
  artwork, with regeneration instructions in its own README.
- [`ScreenshotAutomation/`](ScreenshotAutomation/README.md) contains the
  Playwright-based capture and export tooling used to produce repeatable
  product demonstrations.
- `Icons/` and `Memes/` contain source artwork retained for future exports.

Generated files should not be treated as evidence for a feature. Verify claims
against the current application, README, feature guide, or architecture guide
before publishing them.
