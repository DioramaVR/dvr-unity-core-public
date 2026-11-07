# Icons for the VR menu

- **What:** 77 [Lucide](https://lucide.dev) icons, as 48 px white PNGs with alpha. The menu tints them with `Image.color`.
- **Version:** baked from `lucide-static` 0.460.0, as the design (docs/menu-design) uses. Beyond the design's list: info and sparkles, which the prototype uses, and the four arrows, which the body font lacks.
- **For the button bars** (`HotkeyButton.Icon`, docs/HOOKS.md "Pictures on buttons"):
  - rotate-ccw, rotate-cw, refresh-cw;
  - zoom-in, zoom-out, search;
  - pause, play, fast-forward, rewind, skip-forward;
  - undo-2 (Back), redo-2, corner-up-left, corner-up-right;
  - flip-horizontal-2, flip-vertical-2;
  - arrow-up-down, move-vertical, move-horizontal (tilt), chevrons-up, chevrons-down;
  - trash-2, hammer, eye, eye-off, layers, map, settings, list, square-stack.

  The menu's own icons work on bars too: the arrows, plus, minus, x, camera, house, and the rest.
- **Using them in a game:** any name in `VrIcons.All`, the same as the file name without `.png`.
- **Licence:** ISC, in `LICENSE-lucide.txt`. Free to ship; the licence goes with them.
- **Remaking them:** `tools/bake-icons.sh`. It needs node, and fetches lucide-static and resvg-js into a temp folder. The list is in `tools/bake-icons.mjs` and `VrIcons.All`, and a test keeps the two the same.
- **Shipping:** a mod copies this folder next to its DLL, as `icons/`, and calls `VrIcons.UseModFolder(<dll dir>)` at load. Without it the menu draws without icons.
