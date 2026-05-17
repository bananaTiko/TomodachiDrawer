# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added
- Added `ColourPickerRouter` to centralize RGB -> HSV conversion and in-game picker step mapping.
- Added `ColourToHSVStepsTool` UI window and Tools menu entry for converting hex colours to HSV step/tap guidance.
- Added image preset support in the Avalonia UI with persistent preset selection.
- Added macOS packaging support files and `.app` bundling workflow updates.
- Added dynamic bucket heuristics in `CanvasDrawer` (`GetDynamicBucketHeuristics`) to better optimize draw-time behavior.
- Added this `CHANGELOG.md` file.

### Changed
- Updated `ColourPalette` to use `ColourPickerRouter` instead of inline conversion helpers.
- Tuned TSP defaults and recommendations for faster image output times.
- Updated default arbitrary colour limit and TSP UI numeric settings for speed-focused workflows.
- Improved bucket-click route timeout scaling to reduce cursor travel overhead on larger bucket workloads.
- Improved `MainWindow` image processing flow (preset resizing/temp output path usage).
- Improved macOS-aware settings/firmware path handling and release packaging behavior.
- Updated CI publish matrix/flags (including dropping unsupported targets and using runtime-appropriate publish settings).

### Fixed
- Fixed transparency handling consistency in arbitrary quantization workflows.
- Fixed first-start update-check initialization behavior.

