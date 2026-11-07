using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VrUiKit;

// Which control a row gets (README "Row kinds"). Choices are judged by width:
// a segmented control fits on the right of a row (inline), or on its own line
// under the label (stacked); otherwise it's a cycler.
static class RowKinds
{
    // Segment text 16 px semibold: about 8.6 px a character; a segment adds 28 px
    // of padding, segments 4 px apart, the well 4 px inside its edge each side.
    public const float CharWidth = 8.6f, SegmentPadding = 28, SegmentGap = 4, WellPadding = 8;
    // Room on a section card in a two-column page (1200 wide: rail 120, page
    // padding 28 each side, column gap 16, card padding 18 each side: 468 px):
    // the whole line, and what's left on the right of a label (at least ~200 px).
    public const float ColumnWidth = 468, InlineWidth = 250;
    // Home's view card spans the content column (1200 - 120 - 320 - 56 - 32).
    public const float HomeWidth = 672, HomeInlineWidth = 450;
    // More options than this is a cycler, however short.
    public const int MaxSegments = 5;

    public static float SegmentedWidth(IReadOnlyList<string> options)
    {
        if (options == null || options.Count == 0) return 0;
        float w = WellPadding + SegmentGap * (options.Count - 1);
        foreach (var o in options) w += (o?.Length ?? 0) * CharWidth + SegmentPadding;
        return w;
    }

    public static RowKind Pick(VrSetting s, float inlineWidth = InlineWidth, float lineWidth = ColumnWidth)
    {
        switch (s.Kind)
        {
            case SettingKind.Toggle: return RowKind.Toggle;
            case SettingKind.Number: return RowKind.Number;
            case SettingKind.Action: return s.Destructive ? RowKind.DestructiveAction : RowKind.Action;
        }
        if (s.Cycler || !(s is IChoiceSetting c)) return RowKind.Cycler;
        return PickChoice(c.Options, inlineWidth, lineWidth);
    }

    public static RowKind PickChoice(IReadOnlyList<string> options, float inlineWidth = InlineWidth, float lineWidth = ColumnWidth)
    {
        int n = options?.Count ?? 0;
        if (n < 2 || n > MaxSegments) return RowKind.Cycler;
        float w = SegmentedWidth(options);
        if (n == 2 && w <= inlineWidth) return RowKind.ChoiceInline;
        if (w <= inlineWidth && inlineWidth >= HomeInlineWidth) return RowKind.ChoiceInline; // a wide card: inline when it fits
        return w <= lineWidth ? RowKind.ChoiceStacked : RowKind.Cycler;
    }

    // A section with 2 or more peer toggles shows them as tiles (README
    // "toggle (grouped)"): its toggles move from Rows to Tiles, in order.
    // Folded (More) rows and Developer sections stay as rows.
    public static void TileToggles(MenuSection section)
    {
        if (section.IsDeveloper) return;
        var toggles = section.Rows.Where(r => r.Kind == RowKind.Toggle).ToList();
        if (toggles.Count < 2) return;
        foreach (var t in toggles)
        {
            section.Rows.Remove(t);
            t.Kind = RowKind.ToggleTile;
            section.Tiles.Add(t);
        }
    }
}

// A row's value as drawn.
static class MenuText
{
    public static string Value(MenuRow r)
    {
        if (r.Action != null)
        {
            if (r.Capturing) return "Press a button...";
            return r.Action.Bindings.Count == 0 ? "Not set" : MenuBindings.KeysText(r.Action);
        }
        var s = r.Setting;
        if (s == null) return "";
        if (r.Kind == RowKind.DestructiveAction && r.Armed) return "Sure?";
        if (s is INumberSetting n)
            return string.IsNullOrEmpty(r.Unit) ? s.Value : s.Value + (r.Unit.StartsWith("°") || r.Unit.StartsWith("/") ? "" : " ") + r.Unit;
        return s.Value;
    }
}
