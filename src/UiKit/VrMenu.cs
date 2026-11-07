using System;
using System.Collections.Generic;

namespace VrUiKit;

// What the game puts on the VR menu's Welcome tab: its name, version and tips.
// The tab adds the controls for the current mode (from the bindings) and any
// notices that are up (e.g. "video needs ffmpeg").
static class VrMenu
{
    public static string Title = "Diorama VR";
    public static string Version;
    public static readonly List<string> Tips = new();
    static readonly List<Func<string>> notices = new();

    public static void Welcome(string title, string version, params string[] tips)
    {
        Title = title;
        Version = version;
        Tips.Clear();
        Tips.AddRange(tips);
        VrUiMenu.MarkDirty();
    }

    // ---- turning things off ----
    // A game can leave out a whole tab, a section, or one row, including the
    // ones the core adds (e.g. hide Record where recording doesn't work yet).
    // A section covers setting rows and controls (VrInput's sections) alike.

    static readonly HashSet<string> hiddenTabs = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> hiddenSections = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> hiddenRows = new(StringComparer.OrdinalIgnoreCase);

    // Tab names as on the menu: Welcome, Mode, UI, Controls, Record, Graphics, Game.
    // hide: false puts it back.
    public static void HideTab(string tab, bool hide = true) => Set(hiddenTabs, tab, hide);
    public static void HideSection(string section, bool hide = true) => Set(hiddenSections, section, hide);
    public static void HideRow(string section, string label, bool hide = true) => Set(hiddenRows, section + "/" + label, hide);

    static void Set(HashSet<string> set, string key, bool hide)
    {
        if (hide) set.Add(key);
        else set.Remove(key);
        VrUiMenu.MarkDirty();
    }

    internal static bool Shows(string tab) => !hiddenTabs.Contains(tab);
    internal static bool Shows(VrSetting s) =>
        !hiddenSections.Contains(s.Section) && !hiddenRows.Contains(s.Section + "/" + s.Label) && (s.When == null || s.When());
    internal static bool Shows(VrAction a) =>
        !hiddenSections.Contains(a.Section) && !hiddenRows.Contains(a.Section + "/" + a.Name);

    // A line shown on the Welcome tab while the function returns text (null hides it).
    public static void Notice(Func<string> text) => notices.Add(text);

    internal static IEnumerable<string> Notices()
    {
        foreach (var n in notices)
        {
            string text = null;
            try { text = n(); } catch (Exception) { }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }
}
