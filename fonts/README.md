# Fonts for the VR menu

| File | Font | Use |
|---|---|---|
| `TiltWarp.ttf` | Tilt Warp (variable; Unity uses its default instance) | The DVR display font, as in the installer: headings and the title |
| `AtkinsonHyperlegibleNext-Regular.ttf`, `-SemiBold.ttf`, `-Bold.ttf` | Atkinson Hyperlegible Next, 400 / 600 / 700 | The menu redesign's body text (docs/menu-design): labels, help, values, buttons |

- **Tilt Warp:**
  - from Google Fonts, github.com/google/fonts (ofl/tiltwarp), downloaded 2026-11-06;
  - SIL Open Font License 1.1 (`OFL-TiltWarp.txt`).
- **Atkinson Hyperlegible Next:**
  - the static TTFs from github.com/googlefonts/atkinson-hyperlegible-next (fonts/ttf), at commit 7925f50, the one Google Fonts ships. Downloaded 2026-11-06.
  - Static files, one per weight, because TextMesh Pro reads only a variable font's default instance.
  - SIL Open Font License 1.1 (`OFL-AtkinsonHyperlegibleNext.txt`).
  - It has no arrows (← → ↑ ↓): the menu draws those as icons (icons/arrow-*.png).
- **Licences:** free to bundle and ship, and each licence goes with its font.
- **Wiring:** `VrFont.UseModFolder(dir)` finds both fonts beside the mod's DLL or in `<dir>/fonts`. TextMesh Pro makes them into font assets at run time, where it can (Unity 6's).
- Castoro, the installer's other font, is only for the scrire wordmark, so it isn't used here.
