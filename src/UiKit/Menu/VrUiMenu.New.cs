using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// The new VR menu (docs/menu-design, bead vr-54x): 1200 x 800, a left rail of
// pages, a page header, then a scrolling area of hero cards and two columns of
// section cards. It uses the camera, quad, carrying and clicks in
// (VrUiMenu.cs), which calls BuildNew to draw and TickNew each frame.
//
// What is on each page, and what a click does, comes from MenuModel (pages,
// sections, rows and their kinds) and MenuState (page, open folds, armed
// buttons); this file only draws them, with VrShapes' rounded boxes.
static partial class VrUiMenu
{
    // From VrUi.Init.
    internal static void InitMenu(ConfigFile config)
    {
        MenuState.Init(config);
        UseOwnFolder();
    }

    // The core is compiled into the mod's DLL, and the build copies fonts\ and icons\
    // next to it (DvrUnityCore.props): look there. A mod's own UseModFolder call made
    // before VrUi.Init still wins (first call wins).
    static void UseOwnFolder()
    {
        string dir = null;
        try { dir = System.IO.Path.GetDirectoryName(typeof(VrUiMenu).Assembly.Location); } catch (Exception) { }
        if (string.IsNullOrEmpty(dir)) return;
        VrIcons.UseModFolder(dir);
        var fonts = System.IO.Path.Combine(dir, "fonts");
        VrFont.UseModFolder(System.IO.Directory.Exists(fonts) ? fonts : dir);
    }

    // ---- design tokens (docs/menu-design/README.md) ----
    static Color Rgba(uint rgb, float a = 1) { var c = Hex(rgb); c.a = a; return c; }
    static readonly Color NBack = Hex(0x181a25), NDeep = Hex(0x10111a), NCard = Hex(0x23202f);
    static readonly Color NPink = Hex(0xc80e58), NPinkHi = Hex(0xff3f86);
    static readonly Color NCream = Hex(0xf5f0e8), NDanger = Hex(0xa8233f), NBad = Hex(0xff5e5b), NWarn = Hex(0xf7b801), NInfo = Hex(0xc9a3d9);
    static Color Cream(float a) => Rgba(0xf5f0e8, a);
    static readonly Color None = new(0, 0, 0, 0);

    const float RailW = 120, BodyX = RailW + 28, BodyW = W - RailW - 56, ColGap = 16, HomeRightW = 320;
    static float ColW => (BodyW - ColGap) / 2;
    static MenuPage Current => MenuState.Page;

    // ---- scrolling (VrScroll: the offset, the bar, the stick and the coast) ----
    // Page content is drawn into one container that moves up and down; the rail and
    // header are drawn after it, opaque, so they cover what scrolls under them (no
    // mask: the stand-in UI shaders may not clip). A control in the container is
    // clickable only inside the viewport, at its scrolled place, and not on the bar.
    static RectTransform scrollRoot;
    static float viewTop = 112;
    static MenuPage scrolledPage;
    static readonly VrScroll scroller = new();
    static bool stickClaimed;
    static Vector2? laserPoint;   // canvas px under the laser, this frame
    static Image thumb;

    // The scrolled area's right edge: Home keeps a fixed column on the right.
    static float ScrollRight => Current == MenuPage.Home ? W - HomeRightW : W;
    static bool InView(Vector2 p) => scroller.InViewport(p);

    // Raycast's test for one control.
    static bool Hits(Control c, Vector2 p) => !c.InScroll ? c.Rect.Contains(p) : scroller.Hits(c.Rect, p);

    // Per frame while the new menu is shown: the laser hand's stick scrolls while the
    // laser is on the page (and is kept from the game meanwhile), a press on the bar
    // drags the thumb or steps an arrow, and a flick coasts.
    static void TickNew()
    {
        bool carrying = VrUi.CarryingPanel;
        float stick = carrying ? 0 : VrInput.HandStick(false).y;
        bool want = scroller.Update(carrying ? null : laserPoint, VrInput.PointerTriggerHeld, stick, Time.unscaledDeltaTime) && !carrying;
        if (want && !stickClaimed) { VrInput.ClaimStick(false); stickClaimed = true; }
        else if (!want && stickClaimed) { VrInput.UnclaimStick(); stickClaimed = false; }
        if (scrollRoot != null) scroller.Apply(scrollRoot);
        PlaceThumb();
    }

    static void PlaceThumb()
    {
        if (thumb == null) return;
        thumb.gameObject.SetActive(scroller.CanScroll);
        var d = scroller.ThumbDrawn;
        var rt = thumb.rectTransform;
        rt.anchoredPosition = new Vector2(d.x, -d.y);
        rt.sizeDelta = new Vector2(d.width, d.height);
    }

    // The bar, drawn fixed over the page: track, thumb and the two arrows.
    static void DrawScrollbar()
    {
        thumb = null;
        if (!scroller.CanScroll) return;
        var t = scroller.Track;
        float cx = t.x + (t.width - scroller.ThumbWidth) / 2;
        NBox(cx, t.y, scroller.ThumbWidth, t.height, Cream(0.06f), pill: true);
        thumb = NBox(cx, t.y, scroller.ThumbWidth, scroller.MinThumb, Cream(0.38f), pill: true);
        foreach (var (r, icon, glyph) in new[] { (scroller.UpArrow, "chevron-up", "−"), (scroller.DownArrow, "chevron-down", "+") })
        {
            if (r.width <= 0) continue;
            NBox(r.x + 4, r.y + 4, r.width - 8, r.height - 8, Cream(0.07f), radius: VrShapes.Button);
            if (!Icon(icon, r.x + (r.width - 22) / 2, r.y + (r.height - 22) / 2, 22, Cream(0.8f)))
                NText(glyph, r.x, r.y + 10, r.width, 28, 20, Cream(0.8f), TextAlignmentOptions.Center);
        }
        PlaceThumb();
    }

    // ---- building ----

    static Transform body;   // where drawing goes: the scroller, or the fixed layer
    static bool inScroll;    // controls made now are in the scroller

    static void BuildNew(IVrUiHost host)
    {
        var page = Current;
        if (!MenuModel.Shows(page, host)) { MenuState.Page = page = MenuPage.Home; }
        if (page != MenuPage.Controls) VrInput.CancelCapture();
        if (page != scrolledPage) { scrolledPage = page; scroller.ResetOffset(); }

        // Page content first (so the rail and header draw over it), into the scroller.
        scrollRoot = Place(content, "Scroll", 0, 0, W, H);
        body = scrollRoot;
        inScroll = true;
        MenuPageModel model = null;
        float bottom;
        if (page == MenuPage.Home) { viewTop = 0; bottom = DrawHome(host); }
        else if (page == MenuPage.Help) { viewTop = 112; bottom = DrawHelp(MenuModel.Help()); }
        else { viewTop = 112; model = MenuModel.Page(page, host); bottom = DrawPage(model); }
        scroller.Viewport = new Rect(RailW, viewTop, ScrollRight - RailW, H - viewTop);
        scroller.ContentHeight = bottom + 28 - viewTop;   // px from the viewport top
        body = content;
        inScroll = false;

        DrawRail(host);
        if (page == MenuPage.Home) DrawHomeRight(host);
        else
        {
            NBox(RailW, 0, W - RailW, viewTop, NBack);
            string title = model?.Title ?? "Help", sub = model?.Subtitle ?? "Questions players ask";
            NText(title, BodyX, 20, 640, 40, 30, NCream, heading: true);
            NText(sub, BodyX, 64, 700, 24, 16, Cream(0.62f));
            if (model?.ModePill is string pill)
            {
                float pw = 40 + pill.Length * 10;
                NBox(W - 20 - 48 - 12 - pw, 24, pw, 40, Rgba(0xc80e58, 0.16f), Rgba(0xff3f86, 0.5f), pill: true);
                NText(pill, W - 20 - 48 - 12 - pw, 32, pw, 24, 16, NCream, TextAlignmentOptions.Center, weight: 600);
            }
            CloseButton(W - 20 - 48, 20);
        }

        // The scrollbar (its presses go to VrScroll in TickNew, not to a control).
        DrawScrollbar();
        scroller.Apply(scrollRoot);
    }

    static void DrawRail(IVrUiHost host)
    {
        NBox(0, 0, RailW, H, NDeep, radius: 0);
        NText("DVR", 0, 16, RailW, 44, 26, NCream, TextAlignmentOptions.Center, heading: true);
        float y = 76;
        foreach (var item in MenuModel.Nav(host))
        {
            var it = item;
            bool on = item.Selected;
            if (item.Highlight)
            {
                float hy = H - 16 - 68;
                NButton(8, hy, 104, 68, on ? Rgba(0xc9a3d9, 0.18f) : None, it.Select, item.Label, Rgba(0xc9a3d9, 0.55f), VrShapes.Button);
                NText(item.Label, 8, hy + 38, 104, 22, 14, NInfo, TextAlignmentOptions.Center, weight: 600);
                Icon(item.Icon, 8 + 41, hy + 10, 22, NInfo);
                continue;
            }
            NButton(8, y, 104, 68, on ? Rgba(0xc80e58, 0.18f) : None, it.Select, item.Label, null, VrShapes.Button);
            NText(item.Label, 8, y + 38, 104, 22, 14, on ? NCream : Cream(0.8f), TextAlignmentOptions.Center, weight: 600);
            Icon(item.Icon, 8 + 41, y + 10, 22, on ? NPinkHi : Cream(0.62f));
            y += 74;
        }
    }

    static void CloseButton(float x, float y)
    {
        NButton(x, y, 48, 48, Cream(0.07f), Close, "X", Cream(0.10f), VrShapes.Button);
        if (!Icon("x", x + 13, y + 13, 22, NCream)) NText("X", x, y + 10, 48, 28, 22, NCream, TextAlignmentOptions.Center);
    }

    // ---- primitives ----

    // A rounded box: the fill, and a 2 px ring over it when there's a border colour.
    // pill: fully round ends (radius = half the height).
    static Image NBox(float x, float y, float w, float h, Color fill, Color? border = null, int radius = VrShapes.Card, bool pill = false)
    {
        var img = Box(body, "Box", x, y, w, h, fill);
        Shape(img, radius == 0 ? null : pill ? VrShapes.Pill(Mathf.Min(w, h)) : VrShapes.Fill(radius));
        if (border is Color b)
        {
            var ring = Box(body, "Border", x, y, w, h, b);
            Shape(ring, pill ? VrShapes.PillBorder(Mathf.Min(w, h)) : VrShapes.Border(Math.Max(radius, VrShapes.Line + 1)));
        }
        return img;
    }

    static void Shape(Image img, Sprite s)
    {
        if (s == null) return;
        img.sprite = s;
        img.type = Image.Type.Sliced;
    }

    // A clickable box. The hover tint goes on the fill.
    static Image NButton(float x, float y, float w, float h, Color fill, Action click, string label, Color? border = null, int radius = VrShapes.Button, bool pill = false)
    {
        var img = NBox(x, y, w, h, fill, border, radius, pill);
        controls.Add(new Control { Label = label, Rect = new Rect(x, y, w, h), Image = img, Color = fill, Click = click, InScroll = inScroll });
        return img;
    }

    static void NText(string text, float x, float y, float w, float h, float size, Color colour, TextAlignmentOptions align = TextAlignmentOptions.Left, bool heading = false, bool wrap = false, int weight = 400)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Wrapped text starts at the top of its box (TMP's Left is the middle line).
        if (wrap && align == TextAlignmentOptions.Left) align = TextAlignmentOptions.TopLeft;
        Text(body, text, x, y, w, h, size, align, colour, wrap, heading, heading ? 0 : weight);
    }

    // A Lucide icon, tinted. False when there's no icon (no sprite yet, or no name).
    static bool Icon(string name, float x, float y, float size, Color colour)
    {
        var sprite = IconSprite?.Invoke(name);
        if (sprite == null) return false;
        var img = Box(body, "Icon " + name, x, y, size, size, colour);
        img.sprite = sprite;
        img.preserveAspect = true;
        return true;
    }

    // name -> sprite or null (VrIcons; a test can swap it).
    internal static Func<string, Sprite> IconSprite = name => string.IsNullOrEmpty(name) ? null : VrIcons.Get(name);

    // Rough text width at a size, for wrapping estimates (no layout pass: the menu is drawn once).
    static float TextWidth(string s, float size) => (s?.Length ?? 0) * size * 0.52f;
    static int Lines(string s, float size, float width) => string.IsNullOrEmpty(s) ? 0 : Mathf.Max(1, Mathf.CeilToInt(TextWidth(s, size) / Mathf.Max(1, width)));

    // ---- a page: heroes, then two columns of sections ----

    static float DrawPage(MenuPageModel m)
    {
        float y = viewTop + 4;
        if (m.Heroes.Count > 0) y = DrawHeroes(y, BodyX, BodyW, m.Heroes, 96);
        float left = y, right = y;
        // Pages with one column of sections use the full width.
        bool one = m.Left.Count == 0 || m.Right.Count == 0;
        float w = one ? BodyW : ColW;
        foreach (var s in m.Left) if (!s.IsEmpty) left += DrawSection(s, BodyX, left, w) + 16;
        foreach (var s in m.Right) if (!s.IsEmpty) right += DrawSection(s, one ? BodyX : BodyX + ColW + ColGap, one ? left : right, w) + 16;
        return Mathf.Max(left, right);
    }

    static float DrawHeroes(float y, float x0, float width, IList<MenuHero> heroes, float h)
    {
        float gap = 12, w = (width - gap * (heroes.Count - 1)) / heroes.Count;
        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            bool on = hero.Selected;
            float x = x0 + i * (w + gap);
            NButton(x, y, w, h, on ? Rgba(0xc80e58, 0.12f) : NCard, hero.Select, hero.Name, on ? NPinkHi : Cream(0.10f), VrShapes.Card);
            NBox(x + 16, y + (h - 48) / 2, 48, 48, on ? NPink : Cream(0.07f), radius: VrShapes.Button);
            Icon(hero.Icon, x + 28, y + (h - 24) / 2, 24, NCream);
            float tw = w - 78 - 52;
            NText(hero.Title, x + 78, y + 14, tw, 26, 18, NCream, weight: 700);
            NText(hero.Description, x + 78, y + 42, tw, h - 48, 15, Cream(0.62f), wrap: true);
            if (on && !Icon("circle-check", x + w - 40, y + (h - 24) / 2, 24, NPinkHi))
                NText("✓", x + w - 44, y + (h - 32) / 2, 28, 32, 22, NPinkHi, TextAlignmentOptions.Center);
        }
        return y + h + 16;
    }

    const float SecHead = 52, TileH = 60;

    // A section card. Returns its height.
    static float DrawSection(MenuSection s, float x, float y, float w)
    {
        float inner = w - 36, h = NSectionHeight(s, inner);
        NBox(x, y, w, h, NCard, radius: VrShapes.Card);
        // The header: the whole of it toggles a collapsible section.
        if (s.Collapsible) NButton(x, y, w, SecHead + 4, None, s.ToggleOpen, "section:" + s.Id, null, VrShapes.Card);
        float hx = x + 18;
        if (Icon(s.Icon, hx, y + 4 + (SecHead - 20) / 2, 20, NPinkHi)) hx += 30;
        NText(s.Title, hx, y + 4 + 12, w - (hx - x) - 150, 30, 19, NCream, heading: true);
        float noteRight = x + w - 18;
        if (s.Collapsible)
        {
            noteRight -= 28;
            if (!Icon(s.Open ? "chevron-up" : "chevron-down", x + w - 18 - 20, y + 4 + 16, 20, Cream(0.62f)))
                NText(s.Open ? "−" : "+", x + w - 18 - 20, y + 4 + 12, 20, 28, 20, Cream(0.62f), TextAlignmentOptions.Center);
        }
        NText(s.Note, noteRight - 160, y + 4 + 16, 160, 22, 15, Cream(0.62f), TextAlignmentOptions.Right);
        if (!s.Open) return h;

        float ry = y + 4 + SecHead;
        if (s.Tiles.Count > 0)
        {
            float tw = (inner - 8) / 2;
            for (int i = 0; i < s.Tiles.Count; i++)
                DrawTile(s.Tiles[i], x + 18 + (i % 2) * (tw + 8), ry + (i / 2) * (TileH + 8), tw);
            ry += ((s.Tiles.Count + 1) / 2) * (TileH + 8) + 4;
        }
        foreach (var r in s.ShownRows) ry += DrawRow(r, x + 18, ry, inner);
        if (s.MoreCount > 0)
        {
            Divider(x + 18, ry, inner);
            NButton(x + 18, ry + 2, inner, 46, None, s.ToggleMore, "more:" + s.Id, null, VrShapes.Button);
            float tx = x + 18;
            if (Icon(s.MoreOpen ? "chevron-up" : "chevron-down", tx, ry + 16, 18, NPinkHi)) tx += 26;
            NText(s.MoreText, tx, ry + 13, inner - (tx - x - 18), 24, 16, NPinkHi, weight: 600);
        }
        return h;
    }

    static float NSectionHeight(MenuSection s, float inner)
    {
        float h = 4 + SecHead + 8;
        if (!s.Open) return h;
        if (s.Tiles.Count > 0) h += ((s.Tiles.Count + 1) / 2) * (TileH + 8) + 4;
        foreach (var r in s.ShownRows) h += NRowHeight(r, inner);
        if (s.MoreCount > 0) h += 48;
        return h;
    }

    static void Divider(float x, float y, float w) => Box(body, "Rule", x, y, w, 2, Cream(0.06f));

    // The control's width on the right of the label, by kind.
    static float ControlWidth(MenuRow r) => r.Kind switch
    {
        RowKind.Toggle => 56,
        RowKind.Number => 48 + 8 + 84 + 8 + 48,
        RowKind.Cycler => 48 + 8 + 128 + 8 + 48,
        RowKind.ChoiceInline => 8 + r.Options.Sum(o => 28 + TextWidth(o, 16) + 4),
        RowKind.Action or RowKind.DestructiveAction => Mathf.Max(112, TextWidth(r.ValueText, 16) + 36),
        RowKind.Binding => 132 + 8 + 48 + 40,
        _ => 0,
    };

    static float LabelWidth(MenuRow r, float inner) => r.Kind == RowKind.ChoiceStacked || r.Kind == RowKind.Text ? inner : inner - ControlWidth(r) - 14;

    static float NRowHeight(MenuRow r, float inner)
    {
        float lw = LabelWidth(r, inner);
        float text = Lines(r.Label, 17, lw) * 22 + (string.IsNullOrEmpty(r.HelpLine) ? 0 : 4 + Lines(r.HelpLine, 15, lw) * 20);
        float line = r.Kind == RowKind.ChoiceStacked ? text + 10 + 44 + 8 : Mathf.Max(48, text);
        return 2 + 10 + line + 10;
    }

    // One row: label and help on the left, the control by kind on the right. Returns its height.
    static float DrawRow(MenuRow r, float x, float y, float w)
    {
        float h = NRowHeight(r, w);
        Divider(x, y, w);
        float lw = LabelWidth(r, w);
        float labelH = Lines(r.Label, 17, lw) * 22;
        float helpH = string.IsNullOrEmpty(r.HelpLine) ? 0 : 4 + Lines(r.HelpLine, 15, lw) * 20;
        bool stacked = r.Kind == RowKind.ChoiceStacked;
        float top = y + 12, controlH = 48;
        float textTop = stacked ? top : top + Mathf.Max(0, (Mathf.Max(controlH, labelH + helpH) - labelH - helpH) / 2);
        NText(r.Label, x, textTop, lw, labelH, 17, NCream, wrap: true, weight: 600);
        var helpColour = r.Armed ? NBad : r.Clash ? NWarn : Cream(0.62f);
        NText(r.HelpLine, x, textTop + labelH + 4, lw, helpH, 15, helpColour, wrap: true);
        float cx = x + w - ControlWidth(r), cy = top + (Mathf.Max(controlH, labelH + helpH) - controlH) / 2;
        switch (r.Kind)
        {
            case RowKind.Toggle: DrawSwitch(r, x + w - 56, cy); break;
            case RowKind.ToggleTile: DrawSwitch(r, x + w - 56, cy); break;
            case RowKind.Number: DrawStepper(r, cx, cy, 84, "minus", "plus"); break;
            case RowKind.Cycler: DrawStepper(r, cx, cy, 128, "chevron-left", "chevron-right"); break;
            case RowKind.ChoiceInline: DrawSegments(r, cx, cy, ControlWidth(r)); break;
            case RowKind.ChoiceStacked: DrawSegments(r, x, top + labelH + helpH + 10, w); break;
            case RowKind.Action:
            case RowKind.DestructiveAction: DrawAction(r, cx, cy, ControlWidth(r)); break;
            case RowKind.Binding: DrawBinding(r, x + w - 132 - 8 - 48 - 40, cy); break;
        }
        return h;
    }

    static void DrawSwitch(MenuRow r, float x, float y)
    {
        bool on = r.IsOn;
        // Hit area 56 x 48; the track 52 x 30, the knob 22.
        NButton(x, y, 56, 48, None, r.Activate, r.Label, null, 0);
        NBox(x + 2, y + 9, 52, 30, on ? NPink : Cream(0.18f), pill: true);
        NBox(on ? x + 2 + 52 - 4 - 22 : x + 2 + 4, y + 13, 22, 22, NCream, pill: true);
    }

    static void DrawStepper(MenuRow r, float x, float y, float valueW, string down, string up)
    {
        var row = r;
        StepButton(x, y, r.CanStepDown, () => row.Step(-1), r.Label + " -", down, "−");
        NText(r.ValueText, x + 56, y + 12, valueW, 24, 17, NCream, TextAlignmentOptions.Center, weight: 700);
        StepButton(x + 56 + valueW + 8, y, r.CanStepUp, () => row.Step(1), r.Label + " +", up, "+");
    }

    static void StepButton(float x, float y, bool can, Action click, string label, string icon, string glyph)
    {
        NButton(x, y, 48, 48, Cream(can ? 0.07f : 0.03f), can ? click : null, label, null, VrShapes.Button);
        var c = can ? NCream : Cream(0.38f);
        if (!Icon(icon, x + 13, y + 13, 22, c)) NText(glyph, x, y + 9, 48, 30, 24, c, TextAlignmentOptions.Center);
    }

    static void DrawSegments(MenuRow r, float x, float y, float w)
    {
        var options = r.Options;
        if (options.Count == 0) return;
        NBox(x, y, w, 52, NDeep, radius: VrShapes.Card);
        bool stretch = r.Kind == RowKind.ChoiceStacked;
        float sx = x + 4, total = w - 8 - 4 * (options.Count - 1);
        foreach (var o in options)
        {
            var opt = o;
            float sw = stretch ? total / options.Count : 28 + TextWidth(o, 16);
            bool on = o == r.Selected;
            NButton(sx, y + 4, sw, 44, on ? NPink : None, () => r.Select(opt), r.Label + ":" + o, null, VrShapes.Segment);
            NText(o, sx, y + 4 + 11, sw, 24, 16, on ? NCream : Cream(0.8f), TextAlignmentOptions.Center, weight: 600);
            sx += sw + 4;
        }
    }

    static void DrawAction(MenuRow r, float x, float y, float w)
    {
        bool armed = r.Armed;
        Color fill = armed ? NDanger : r.Primary ? NPink : Cream(0.07f);
        Color? border = armed ? NBad : r.Primary ? null : Cream(0.10f);
        NButton(x, y, w, 48, fill, r.Activate, r.Label, border, VrShapes.Button);
        if (r.Primary && !armed) { var hl = Box(body, "Highlight", x, y, w, 48, Rgba(0xffffff, 0.14f)); Shape(hl, VrShapes.Highlight(VrShapes.Button)); }
        NText(r.ValueText, x, y + 12, w, 24, 16, NCream, TextAlignmentOptions.Center, weight: 600);
    }

    static void DrawBinding(MenuRow r, float x, float y)
    {
        bool none = r.Action == null || r.Action.Bindings.Count == 0;
        var fill = r.Capturing ? NPink : r.Clash ? Rgba(0xf7b801, 0.12f) : Cream(0.07f);
        NButton(x, y, 172, 48, fill, r.Activate, "bind:" + r.Action?.Id, r.Clash ? NWarn : Cream(0.10f), VrShapes.Button);
        NText(r.ValueText, x + 8, y + 13, 156, 22, 15, none && !r.Capturing ? Cream(0.38f) : NCream, TextAlignmentOptions.Center, weight: 700);
        NButton(x + 180, y, 48, 48, Cream(0.05f), r.AddBinding, "add:" + r.Action?.Id, null, VrShapes.Button);
        if (!Icon("plus", x + 180 + 13, y + 13, 22, NCream)) NText("+", x + 180, y + 9, 48, 30, 24, NCream, TextAlignmentOptions.Center);
    }

    static void DrawTile(MenuRow r, float x, float y, float w)
    {
        bool on = r.IsOn;
        NButton(x, y, w, TileH, on ? Rgba(0xc80e58, 0.16f) : Cream(0.05f), r.Activate, r.Label, on ? NPinkHi : Cream(0.10f), VrShapes.Button);
        float tx = x + 12;
        if (Icon(r.Icon, tx, y + (TileH - 20) / 2, 20, on ? NPinkHi : Cream(0.62f))) tx += 30;
        NText(r.Label, tx, y + 8, w - (tx - x) - 12, 24, 16, NCream, weight: 600);
        NText(on ? "On" : "Off", tx, y + 32, 60, 20, 14, on ? NPinkHi : Cream(0.62f), weight: 700);
    }

    // ---- Help ----

    static float DrawHelp(HelpModel help)
    {
        float y = viewTop + 4, w = Mathf.Min(840, BodyW);
        foreach (var item in help.Items)
        {
            if (item.Heading != null)
            {
                if (y > viewTop + 4) y += 12;
                NText(item.Heading, BodyX + 4, y, w - 8, 28, 20, NCream, heading: true);
                y += 36;
            }
            bool open = item.Open;
            float aLines = open ? Lines(item.Answer, 16, w - 36) * 24 + 16 : 0;
            float h = 56 + aLines;
            NButton(BodyX, y, w, 56, None, item.Toggle, "faq:" + item.Id, null, VrShapes.Card);
            NBox(BodyX, y, w, h, NCard, open ? Rgba(0xc9a3d9, 0.55f) : (Color?)null, VrShapes.Card);
            NText(item.Question, BodyX + 18, y + 16, w - 70, 26, 17, NCream, weight: 700);
            if (!Icon(open ? "chevron-up" : "chevron-down", BodyX + w - 40, y + 18, 20, open ? NInfo : Cream(0.62f)))
                NText(open ? "−" : "+", BodyX + w - 40, y + 14, 20, 28, 20, open ? NInfo : Cream(0.62f), TextAlignmentOptions.Center);
            if (open) NText(item.Answer, BodyX + 18, y + 56, w - 36, aLines, 16, Cream(0.8f), wrap: true);
            y += h + 8;
        }
        return y;
    }

    // ---- Home ----

    static float DrawHome(IVrUiHost host)
    {
        var home = MenuModel.Home(host);
        homeModel = home;
        float x = BodyX, y = 24, w = W - RailW - HomeRightW - 56;
        NText(home.Title, x, y, w - 120, 40, 30, NCream, heading: true);
        if (!string.IsNullOrEmpty(home.Version)) NText("v" + home.Version.TrimStart('v'), x + w - 120, y + 12, 120, 22, 16, Cream(0.62f), TextAlignmentOptions.Right);
        y += 58;
        if (home.ShowFirstTime)
        {
            float bh = 32 + 26 + Lines(home.FirstTimeText, 16, w - 300) * 22 + 12;
            bh = Mathf.Max(bh, 100);
            NBox(x, y, w, bh, Rgba(0xc80e58, 0.10f), NPinkHi);
            NText(home.FirstTimeTitle, x + 20, y + 16, w - 300, 24, 17, NPinkHi, weight: 700);
            NText(home.FirstTimeText, x + 20, y + 44, w - 300, bh - 52, 16, Cream(0.8f), wrap: true);
            NButton(x + w - 266, y + (bh - 48) / 2, 130, 48, None, () => MenuState.Page = MenuPage.Help, "Read the FAQ", null, VrShapes.Button);
            NText("Read the FAQ", x + w - 266, y + (bh - 48) / 2 + 12, 130, 24, 16, NPinkHi, TextAlignmentOptions.Center, weight: 600);
            NButton(x + w - 120, y + (bh - 48) / 2, 100, 48, Cream(0.07f), home.DismissFirstTime, "Got it", Cream(0.10f), VrShapes.Button);
            NText("Got it", x + w - 120, y + (bh - 48) / 2 + 12, 100, 24, 16, NCream, TextAlignmentOptions.Center, weight: 600);
            y += bh + 18;
        }
        // Your view.
        NText("Your view", x, y, 300, 28, 20, NCream, heading: true);
        NButton(x + w - 210, y - 4, 210, 36, None, () => MenuState.Page = MenuPage.View, "All view settings", null, VrShapes.Button);
        NText("All view settings", x + w - 210, y + 2, 186, 24, 16, NPinkHi, TextAlignmentOptions.Right, weight: 600);
        Icon("arrow-right", x + w - 18, y + 5, 18, NPinkHi);
        Box(body, "Rule", x, y + 34, w, 2, Cream(0.10f));
        y += 48;
        if (home.Modes.Count > 0) y = DrawHeroes(y, x, w, home.Modes, 112);
        if (home.ViewRows.Count > 0)
        {
            float inner = w - 36, h = 8;
            foreach (var r in home.ViewRows) h += NRowHeight(r, inner);
            NBox(x, y, w, h, NCard, radius: VrShapes.Card);
            float ry = y + 4;
            foreach (var r in home.ViewRows) ry += DrawRow(r, x + 18, ry, inner);
            y += h + 18;
        }
        // Quick actions: equal buttons, 56 high.
        var actions = home.QuickActions.Where(a => a.Available).ToList();
        if (actions.Count > 0)
        {
            float aw = (w - 12 * (actions.Count - 1)) / actions.Count;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                float ax = x + i * (aw + 12);
                NButton(ax, y, aw, 56, a.Primary ? NPink : Cream(0.07f), a.Run, "quick:" + a.Id, a.Primary ? null : Cream(0.10f), VrShapes.Button);
                if (a.Primary) { var hl = Box(body, "Highlight", ax, y, aw, 56, Rgba(0xffffff, 0.14f)); Shape(hl, VrShapes.Highlight(VrShapes.Button)); }
                var iconColour = a.Id == "video" ? NBad : a.Id == "help" ? NInfo : NCream;
                float textW = TextWidth(a.Text, 17), iw = IconSprite?.Invoke(a.Icon) != null ? 28 : 0;
                float start = ax + (aw - textW - iw) / 2;
                if (iw > 0) Icon(a.Icon, start, y + 18, 20, iconColour);
                NText(a.Text, start + iw, y + 16, aw - iw, 24, 17, NCream, weight: 600);
            }
            y += 56 + 16;
        }
        return y;
    }

    static HomeModel homeModel;

    // Home's right column: the cheat sheet and the button bars. Not scrolled.
    // The bars sit at the bottom and take what they need (at most three rows of
    // tiles; more are on Game UI); the cheat sheet fills what's left above them,
    // and "All N controls" says there are more (TPM's bars once overlapped it).
    const int HomeBarRows = 3;

    static void DrawHomeRight(IVrUiHost host)
    {
        var home = homeModel ?? MenuModel.Home(host);
        float rx = W - HomeRightW, rw = HomeRightW;
        NBox(rx, 0, rw, H, NDeep, radius: 0);
        NText("Controls", rx + 20, 18, 200, 28, 20, NCream, heading: true);
        NText(home.ControlsSubtitle, rx + 20, 50, 220, 22, 15, Cream(0.62f));
        CloseButton(W - 16 - 48, 16);
        float innerW = rw - 36;

        // The bars' block first: where it starts bounds the cheat sheet.
        var bars = home.ButtonBars;
        int shown = Math.Min(bars.Count, HomeBarRows * 2);
        bool moreBars = bars.Count > shown;
        int rows = (shown + 1) / 2;
        float barsTop = H;
        if (shown > 0)
        {
            // The subtitle's real height (a game's font can wrap it), so the tiles never sit on it.
            const string sub = "Keys the controllers don't have.";
            float subH = Lines(sub, 14, innerW) * 20;
            float head = 40 + subH + 10;   // rule to the first tile
            barsTop = H - 24 - rows * (TileH + 8) - head - (moreBars ? 36 : 0);
            Box(body, "Rule", rx + 20, barsTop, innerW, 2, Cream(0.10f));
            NText("Button bars", rx + 20, barsTop + 12, 200, 24, 18, NCream, heading: true);
            NText(sub, rx + 20, barsTop + 38, innerW, subH, 14, Cream(0.62f), wrap: true);
            float tw = (innerW - 8) / 2;
            for (int i = 0; i < shown; i++)
                DrawTile(bars[i], rx + 20 + (i % 2) * (tw + 8), barsTop + head + (i / 2) * (TileH + 8), tw);
            if (moreBars)
            {
                float ly = barsTop + head + rows * (TileH + 8);
                string text = $"All {bars.Count} button bars";
                NButton(rx + 16, ly, 240, 32, None, () => MenuState.Page = MenuPage.GameUi, "All button bars", null, VrShapes.Button);
                NText(text, rx + 20, ly + 4, 220, 24, 16, NPinkHi, weight: 600);
                Icon("arrow-right", rx + 20 + TextWidth(text, 16) + 6, ly + 7, 18, NPinkHi);
            }
        }

        // The cheat sheet, as many rows as fit above the bars with room for the link.
        const float RowStep = 38, LinkH = 40;
        float y = 92;
        int fit = Math.Max(0, (int)((barsTop - 12 - LinkH - y) / RowStep));
        foreach (var c in home.Cheat.Take(fit))
        {
            Box(body, "Rule", rx + 20, y, innerW, 2, Cream(0.06f));
            NText(c.Hand.ToString(), rx + 20, y + 10, 16, 20, 13, c.Hand == 'R' ? NPinkHi : NInfo, weight: 700);
            NText(c.Action, rx + 42, y + 8, 150, 24, 16, NCream);
            NText(c.Keys, rx + 192, y + 9, innerW - 172, 22, 14, Cream(0.8f), TextAlignmentOptions.Right, weight: 700);
            y += RowStep;
        }
        if (home.TotalControls > 0)
        {
            NButton(rx + 16, y + 4, 220, 32, None, () => MenuState.Page = MenuPage.Controls, "All controls", null, VrShapes.Button);
            NText(home.AllControlsText, rx + 20, y + 8, 220, 24, 16, NPinkHi, weight: 600);
            Icon("arrow-right", rx + 20 + TextWidth(home.AllControlsText, 16) + 6, y + 11, 18, NPinkHi);
        }
    }
}
