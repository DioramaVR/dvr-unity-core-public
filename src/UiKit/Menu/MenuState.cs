using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace VrUiKit;

// What the menu remembers, in the game's config file ([VrUi.Menu]):
//   Page           the page it was on
//   Open           which sections, More folds and FAQ items are open or closed
//                  (only the ones the player changed: "key=1;key=0;...")
//   FirstTimeDone  the Home page's "First time here?" box was dismissed
//   SwapSticks     the player's Swap sticks (VrInput.SwapSticks)
// A destructive row's "Sure?" is in memory only: a second click, any other
// click, or leaving the page clears it. Without Init (sims, tests) it all
// lives in memory.
static class MenuState
{
    static ConfigEntry<string> pageEntry, openEntry;
    static ConfigEntry<bool> firstTimeEntry, swapEntry;
    static readonly Dictionary<string, bool> open = new();
    static MenuPage page = MenuPage.Home;
    static bool firstTimeDone, swap;
    static string armed;

    public const string Section = "VrUi.Menu";

    public static void Init(ConfigFile config)
    {
        pageEntry = config.Bind(Section, "Page", "Home", "The VR menu page last open.");
        openEntry = config.Bind(Section, "Open", "", "Sections, More folds and help items the player opened or closed (key=1 / key=0, separated by ';').");
        firstTimeEntry = config.Bind(Section, "FirstTimeDone", false, "The Home page's \"First time here?\" box was dismissed.");
        swapEntry = config.Bind(Section, "SwapSticks", false, "Swap the two thumbsticks' jobs, in every mode.");
        page = Enum.TryParse<MenuPage>(pageEntry.Value, out var p) ? p : MenuPage.Home;
        open.Clear();
        foreach (var part in (openEntry.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq > 0) open[part.Substring(0, eq)] = part.Substring(eq + 1) == "1";
        }
        firstTimeDone = firstTimeEntry.Value;
        swap = swapEntry.Value;
        VrInput.SwapSticks = swap;
        armed = null;
    }

    // Forget everything (tests); a later Init reads the file again.
    internal static void Reset()
    {
        pageEntry = openEntry = null;
        firstTimeEntry = swapEntry = null;
        open.Clear();
        page = MenuPage.Home;
        firstTimeDone = swap = false;
        armed = null;
    }

    // The page shown. Changing it clears a pending "Sure?".
    public static MenuPage Page
    {
        get => page;
        set
        {
            if (page != value) armed = null;
            page = value;
            if (pageEntry != null) pageEntry.Value = value.ToString();
            VrUiMenu.MarkDirty();
        }
    }

    // ---- open / closed ----

    public static string SectionKey(MenuPage p, string section) => $"{p}/{section}";
    public static string MoreKey(MenuPage p, string section) => $"{p}/{section}/more";
    public static string FaqKey(string item) => "faq/" + item;

    public static bool IsOpen(string key, bool defaultOpen) => open.TryGetValue(key, out var o) ? o : defaultOpen;

    public static void SetOpen(string key, bool isOpen)
    {
        open[key] = isOpen;
        if (openEntry != null) openEntry.Value = string.Join(";", open.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value ? "1" : "0")));
    }

    // ---- the First-time box ----

    public static bool FirstTimeDismissed
    {
        get => firstTimeDone;
        set { firstTimeDone = value; if (firstTimeEntry != null) firstTimeEntry.Value = value; }
    }

    // ---- Swap sticks ----

    public static bool SwapSticks
    {
        get => swap;
        set { swap = value; VrInput.SwapSticks = value; if (swapEntry != null) swapEntry.Value = value; }
    }

    // ---- "Sure?" ----

    public static bool IsArmed(string rowId) => rowId != null && armed == rowId;
    public static void Arm(string rowId) { armed = rowId; VrUiMenu.MarkDirty(); }
    public static void Disarm() { if (armed != null) { armed = null; VrUiMenu.MarkDirty(); } }
}
