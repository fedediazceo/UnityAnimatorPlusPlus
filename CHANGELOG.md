# Changelog

All notable changes to **Animator++** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - 2026-06-19

### Added
- **Node colouring tool.** Organise the graph by tinting state and sub-state-machine
  nodes with a custom colour. Tints recolour the node fill and border while keeping the
  rounded corners, top→bottom gradient, drop shadow, and play-mode progress bar.
  - Right-click a node (or a multi-selection) → **Color ▸ Pick a Color…** to choose a
    custom colour, or **Color ▸ Clear** to remove the tint.
  - Floating **Colors** palette pinned to the canvas's top-left with 10 reusable slots:
    left-click a slot to paint the current node selection, right-click a slot to set its
    colour, reset it, or paint the selection, and use the caret to collapse the panel.
  - Tints are stored per node in the controller's `AnimatorRerouteData` sub-asset
    (with full Undo/Redo) so they travel with the `.controller` asset.
  - The 10 palette slots are an editor-wide preference saved in `EditorPrefs`.

- Ovearll fixes: Added motion drag and drop like the unity Animator, added blend tree cascade view, like the Animator

## [0.1.0] - 2025

### Added
- Initial release: enhanced Animator graph window (Window ▸ Animation ▸ Animator ++)
  with layers, parameters, sub-state-machines, blend-tree inspection, transition
  reroute points, marquee multi-selection, grid snapping, and play-mode live link.

[0.1.1]: https://github.com/fedediaz/com.fedediaz.animator-plus-plus/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/fedediaz/com.fedediaz.animator-plus-plus/releases/tag/v0.1.0
