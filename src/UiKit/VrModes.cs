using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace VrUiKit;

// A view mode: how the game is seen and played (a whole-world diorama, a
// turntable, a camera following a character...). Each game registers the modes
// it has, in menu order; the Mode tab lists them and shows the chosen one's
// options under it. See docs/MENU.md: games start with interim modes for their
// current schemes, and standard ones move into the core later.
sealed class VrMode
{
    public string Id, Name, Description;
    // Rows shown under this mode on the Mode tab, only while it's chosen.
    public readonly List<VrSetting> Options = new();
    // Action sections (VrInput.Register's "section") this mode uses; null = all.
    // The Controls tab and the Welcome cheat sheet show only these.
    public string[] ActionSections;

    public VrMode Option(VrSetting setting) { Options.Add(setting); return this; }
    public VrMode Controls(params string[] sections) { ActionSections = sections; return this; }
}

static class VrModes
{
    static readonly List<VrMode> modes = new();
    static ConfigFile config;
    static ConfigEntry<string> entry;
    static Func<string> get;
    static Action<string> set;
    static string chosen;

    public static IReadOnlyList<VrMode> All => modes;

    // False when there's no world on the table (main menu, map, loading): VR
    // shows the game on a big flat screen then (Cinema), whatever the mode.
    public static Func<bool> InWorld = () => true;

    // Raised after the player picks another mode.
    public static event Action<VrMode> Changed;

    // VrUi.Init passes the game's config; the choice is bound there ([VrUi] ViewMode)
    // the first time a mode is added, unless the game keeps it itself (Store).
    internal static void Init(ConfigFile file) => config = file;

    // Keep the chosen mode in the game's own setting (ids are what get/set exchange),
    // so a mode that existed before this menu keeps the player's choice.
    public static void Store(Func<string> getter, Action<string> setter)
    {
        get = getter;
        set = setter;
    }

    public static VrMode Add(string id, string name, string description)
    {
        if (get == null && entry == null && config != null)
            entry = config.Bind("VrUi", "ViewMode", id, "View mode, by id (changed from the VR menu's Mode tab).");
        var mode = new VrMode { Id = id, Name = name, Description = description };
        modes.Add(mode);
        VrUiMenu.MarkDirty();
        return mode;
    }

    static string CurrentId => get != null ? get() : entry != null ? entry.Value : chosen;

    // The chosen mode, or the first if the saved id isn't one of them; null with no modes.
    public static VrMode Current
    {
        get
        {
            var id = CurrentId;
            return modes.FirstOrDefault(m => m.Id == id) ?? modes.FirstOrDefault();
        }
    }

    public static void Select(string id)
    {
        var mode = modes.FirstOrDefault(m => m.Id == id);
        if (mode == null || mode == Current && CurrentId == id) return;
        if (set != null) set(id);
        else if (entry != null) entry.Value = id;
        else chosen = id;
        VrUiMenu.MarkDirty();
        Changed?.Invoke(mode);
    }

    // The current mode uses this action: no modes, or a mode without a list, uses all.
    // Otherwise a section is hidden only when it belongs to ANOTHER mode (some mode
    // lists it, the current one doesn't). Sections no mode lists are shared (the
    // laser, back, window grabs) and always shown. Before, everything a mode didn't
    // list was hidden, so ats's laser and window bindings vanished from the
    // Controls tab and the cheat sheet (2026-11-06).
    public static bool Uses(VrAction action)
    {
        var sections = Current?.ActionSections;
        if (sections == null || sections.Contains(action.Section)) return true;
        foreach (var m in modes)
            if (m.ActionSections != null && m.ActionSections.Contains(action.Section)) return false;
        return true;
    }
}
