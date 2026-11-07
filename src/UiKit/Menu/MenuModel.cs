using System;
using System.Collections.Generic;
using System.Linq;

namespace VrUiKit;

// Builds the menu's pages from what the core and the game registered
// (VrSettings, VrModes, VrInput, VrUi, VrHotkeys, VrMenu). Call on each redraw:
// it reads the current state (mode, UI mode, handedness) and only makes objects.
//
// Where a row goes (README "How unseen game rows slot in"):
// - Today's tab picks the page: Mode -> View, UI -> Game UI, Controls,
//   Record -> Capture, Graphics; the Game tab -> the Graphics page (or the page a
//   game asked for with PutOnPage; a few known kinds of row go to Game UI or Controls).
// - The core's rows go to the README's sections (Placement below).
// - Anything else is the game's own: a section named after the game ("This game only").
// - Developer rows: a collapsed Developer section on the same page.
// - Rarely used rows: behind their section's More fold.
// - Two or more toggles in a section become tiles.
static class MenuModel
{
    // Set by the table rig: what Home's "Recentre table" does (a game's own
    // "Recentre table" row is used when it has one).
    public static Action RecentreTable;

    // A game can send a section of its own rows to a page.
    static readonly Dictionary<string, MenuPage> pageOf = new();
    public static void PutOnPage(string section, MenuPage page) => pageOf[section] = page;
    internal static void ClearPutOnPage() => pageOf.Clear();

    const string DefaultTitle = "Diorama VR";

    // ---- the rail ----

    public static List<MenuNavItem> Nav(IVrUiHost host)
    {
        var list = new List<MenuNavItem>();
        foreach (MenuPage p in Enum.GetValues(typeof(MenuPage)))
            if (Shows(p, host))
                list.Add(new MenuNavItem { Page = p, Label = MenuWording.PageTitle(p), Icon = MenuWording.PageIcon(p), Highlight = p == MenuPage.Help });
        return list;
    }

    // The game can hide a page with VrMenu.HideTab, by its new name or its old tab's.
    static string OldTab(MenuPage p) => p switch
    {
        MenuPage.Home => "Welcome",
        MenuPage.View => "Mode",
        MenuPage.GameUi => "UI",
        MenuPage.Capture => "Record",
        _ => p.ToString(),
    };

    static bool TabShown(string tab) => VrMenu.Shows(tab) && tab switch
    {
        "Mode" => VrMenu.Shows("View"),
        "UI" => VrMenu.Shows("Game UI") && VrMenu.Shows("GameUi"),
        "Record" => VrMenu.Shows("Capture"),
        _ => true,
    };

    static bool PageShown(MenuPage p) => p == MenuPage.Home || p == MenuPage.Help || TabShown(OldTab(p));

    // Pages with nothing on them are left out (Home, Game UI, Controls and Help always show).
    public static bool Shows(MenuPage page, IVrUiHost host)
    {
        if (!PageShown(page)) return false;
        if (page is MenuPage.Home or MenuPage.Help or MenuPage.GameUi or MenuPage.Controls) return true;
        var m = Page(page, host);
        return m.Heroes.Count > 0 || m.Sections.Any(s => !s.IsEmpty);
    }

    // ---- where each row goes ----

    // The README's sections, by the label as shown (MenuWording.Label), in order.
    // "more:" puts a row behind the section's More fold.
    static readonly (MenuPage page, string section, string[] labels)[] Placement =
    {
        (MenuPage.View, "position", new[] { "Table height", "Table distance", "Scale" }),
        (MenuPage.View, "look", new[] { "Background", "Radius", "Edge", "Cut around the character", "Cut radius" }),
        (MenuPage.View, "turning", new[] { "Turn", "Turn speed", "Snap angle", "more:Turn ramp", "more:Spin coast", "more:Zoom speed", "more:Pan speed", "more:Twist gain", "more:Stick dead zone" }),
        (MenuPage.View, "orbit", new[] { "Orbit turn", "One grip", "more:Orbit height", "more:Orbit distance", "more:Orbit smoothing", "more:Orbit coast", "more:Follow speed" }),
        (MenuPage.View, "mode", new[] { "Follow the game camera", "Fling", "Reset to game defaults" }),
        (MenuPage.GameUi, "panel", new[] { "Panel width", "Panel height", "Panel distance", "Game UI size", "Game UI panel" }),
        (MenuPage.GameUi, "windows", new[] { "While this menu is open, grab a window to move it.", "Windows", "Layout", "Held windows", "Window names", "Start over from layout", "Save as new layout", "Save over layout", "Delete layout", "Windows appear once you're in a level." }),
        (MenuPage.GameUi, "menu", new[] { "Menu style" }),
        (MenuPage.Controls, "you", new[] { "Left-handed", "Swap sticks", "Laser angle" }),
        (MenuPage.Controls, "preset", new[] { "Preset", "Use preset", "Save as new preset", "Delete preset" }),
        (MenuPage.Capture, "spectator", new[] { "Spectator view on monitor", "Spectator field of view", "Spectator smoothing" }),
        (MenuPage.Capture, "recordings", new[] { "Show VR menu in recordings" }),
        (MenuPage.Graphics, "display", new[] { "Monitor shows the game", "Shadows", "Post-processing", "Resolution scale" }),
    };

    // Sections that take a whole core section by name.
    static readonly (string section, MenuPage page, string target)[] BySection =
    {
        ("Hotkey bars", MenuPage.GameUi, "bars"),
        ("UI pieces", MenuPage.GameUi, "pieces"),
    };

    static readonly string[] DestructiveLabels = { "Reset to game defaults", "Delete layout", "Delete preset" };
    static readonly string[] DeveloperLabels = { "Log UI pieces", "Screenshot for Claude" };

    internal sealed class Slot
    {
        public MenuPage Page;
        public string Section;     // a section id, "game", "developer", or null when Hidden
        public bool More;
        public bool Hidden;        // shown elsewhere (a tile the core makes, a quick action)
        public int Order = int.MaxValue;
        public override string ToString() => Hidden ? "hidden" : $"{Page}/{Section}{(More ? " (more)" : "")}";
    }

    static MenuPage PageOfTab(string tab) => tab switch
    {
        "Mode" => MenuPage.View,
        "UI" => MenuPage.GameUi,
        "Controls" => MenuPage.Controls,
        "Record" => MenuPage.Capture,
        "Graphics" => MenuPage.Graphics,
        // The Game tab (and any tab of a game's own): the README puts the game's section on Graphics.
        _ => MenuPage.Graphics,
    };

    internal static bool IsDeveloper(VrSetting s) =>
        s.Developer || DeveloperLabels.Any(d => (s.Label ?? "").StartsWith(d, StringComparison.Ordinal));

    // Where a row goes. tab: today's tab for it (VrSettings.TabOf; "Mode" for a view mode's options).
    internal static Slot Place(VrSetting s, string tab)
    {
        string shown = MenuWording.Label(s.Label);
        // Shown elsewhere: the core's Left-handed tile; Home's Recentre quick action.
        if (tab == "Controls" && shown == "Left-handed" && !(s is DelegateToggle)) return new Slot { Hidden = true };
        if (tab == "Game" && shown == "Recentre table") return new Slot { Hidden = true };

        MenuPage page = pageOf.TryGetValue(s.Section ?? "", out var asked) ? asked : PageOfTab(tab);
        if (tab == "Game" && !pageOf.ContainsKey(s.Section ?? ""))
        {
            // Game-tab rows that clearly belong elsewhere.
            if (shown.IndexOf("bubble", StringComparison.OrdinalIgnoreCase) >= 0) page = MenuPage.GameUi;
            else if (shown.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0 || shown.StartsWith("Interact", StringComparison.Ordinal)) page = MenuPage.Controls;
        }
        if (IsDeveloper(s))
            return new Slot { Page = tab == "Game" && !pageOf.ContainsKey(s.Section ?? "") ? MenuPage.Graphics : page, Section = "developer" };

        foreach (var (section, p, target) in BySection)
            if (s.Section == section) return new Slot { Page = p, Section = target, More = s.Rarely };

        // The core's own rows by label, on the page their tab maps to (panel rows from any tab).
        foreach (var (p, section, labels) in Placement)
        {
            if (p != page && section != "panel") continue;
            for (int i = 0; i < labels.Length; i++)
            {
                bool more = labels[i].StartsWith("more:", StringComparison.Ordinal);
                string l = more ? labels[i].Substring(5) : labels[i];
                if (l == shown)
                    return new Slot { Page = p, Section = section, More = more || s.Rarely, Order = i };
            }
        }
        return new Slot { Page = page, Section = "game", More = s.Rarely };
    }

    // ---- rows ----

    internal static MenuRow Row(VrSetting s, float inlineWidth = RowKinds.InlineWidth, float lineWidth = RowKinds.ColumnWidth)
    {
        string shown = MenuWording.Label(s.Label);
        string label = MenuWording.PageLabel(shown);
        bool destructive = s.Destructive || DestructiveLabels.Contains(shown);
        var kind = s is TextLine ? RowKind.Text : RowKinds.Pick(s, inlineWidth, lineWidth);
        if (destructive && kind == RowKind.Action) kind = RowKind.DestructiveAction;
        return new MenuRow
        {
            Id = (s.Section ?? "") + "/" + s.Label,
            Kind = kind,
            Setting = s,
            Label = label,
            Unit = s.Unit ?? MenuWording.UnitOf(s.Label),
            Help = HelpText.Resolve(MenuWording.HelpFor(s, label) ?? MenuWording.HelpFor(s, shown)),
            Icon = MenuWording.IconFor(s, label),
            Rarely = s.Rarely,
            Developer = IsDeveloper(s),
            Destructive = destructive,
        };
    }

    static MenuRow BindingRow(VrAction a) => new()
    {
        Id = "bind:" + a.Id,
        Kind = RowKind.Binding,
        Action = a,
        Label = MenuWording.Label(a.Name),
    };

    // Every visible row, with today's tab: the current mode's options, then the registered rows.
    internal static IEnumerable<(VrSetting s, string tab)> Rows()
    {
        var mode = VrModes.Current;
        if (mode != null && TabShown("Mode"))
            foreach (var s in mode.Options)
                if (VrMenu.Shows(s)) yield return (s, "Mode");
        foreach (var s in VrSettings.All)
        {
            var tab = VrSettings.TabOf(s);
            if (TabShown(tab) && VrMenu.Shows(s)) yield return (s, tab);
        }
    }

    // ---- sections ----

    static string GameTitle => string.IsNullOrEmpty(VrMenu.Title) || VrMenu.Title == DefaultTitle ? "This game" : VrMenu.Title;

    static MenuSection Section(MenuPage page, string id, string title, string icon, MenuColumn column, bool collapsible = false, bool open = true, string note = null) =>
        new() { Page = page, Id = id, Title = title, Icon = icon, Column = column, Collapsible = collapsible, DefaultOpen = open, Note = note };

    static MenuSection GameSection(MenuPage page) =>
        new() { Page = page, Id = "game", Title = GameTitle, Icon = "gamepad-2", Note = "This game only", IsGame = true };

    static MenuSection DeveloperSection(MenuPage page) =>
        new() { Page = page, Id = "developer", Title = "Developer", Icon = "terminal", Collapsible = true, DefaultOpen = false, IsDeveloper = true };

    // Fills the page's sections from the rows that land on it, then adds the
    // game and Developer sections where they have rows, and drops empty ones.
    static void Fill(MenuPageModel m, List<MenuSection> fixedSections, IEnumerable<(VrSetting s, string tab)> rows, IEnumerable<VrSetting> extra = null)
    {
        var game = GameSection(m.Page);
        var dev = DeveloperSection(m.Page);
        var placed = new List<(MenuSection sec, Slot slot, MenuRow row, int index)>();
        int n = 0;
        void Put(VrSetting s, string tab)
        {
            var slot = Place(s, tab);
            if (slot.Hidden || slot.Page != m.Page) return;
            var sec = slot.Section == "game" ? game : slot.Section == "developer" ? dev : fixedSections.FirstOrDefault(x => x.Id == slot.Section) ?? game;
            placed.Add((sec, slot, Row(s), n++));
        }
        if (extra != null) foreach (var s in extra) Put(s, OldTab(m.Page));
        foreach (var (s, tab) in rows) Put(s, tab);
        foreach (var g in placed.GroupBy(p => p.sec))
            foreach (var p in g.OrderBy(p => p.slot.Order).ThenBy(p => p.index))
                (p.slot.More ? p.sec.More : p.sec.Rows).Add(p.row);

        foreach (var s in fixedSections.Concat(new[] { game, dev })) RowKinds.TileToggles(s);
        foreach (var s in fixedSections) if (!s.IsEmpty) (s.Column == MenuColumn.Left ? m.Left : m.Right).Add(s);
        // The game's section and the Developer section go under the shorter column.
        foreach (var s in new[] { game, dev })
        {
            if (s.IsEmpty) continue;
            s.Column = Height(m.Left) <= Height(m.Right) ? MenuColumn.Left : MenuColumn.Right;
            (s.Column == MenuColumn.Left ? m.Left : m.Right).Add(s);
        }
    }

    static int Height(List<MenuSection> col) => col.Sum(s => 1 + (s.Tiles.Count + 1) / 2 + (s.Open ? s.Rows.Count : 0));

    static MenuPageModel NewPage(MenuPage p) =>
        new() { Page = p, Title = MenuWording.PageTitle(p), Subtitle = MenuWording.PageSubtitle(p) };

    // ---- pages ----

    // A page other than Home and Help (those have their own models).
    public static MenuPageModel Page(MenuPage page, IVrUiHost host)
    {
        var m = NewPage(page);
        switch (page)
        {
            case MenuPage.View: BuildView(m); break;
            case MenuPage.GameUi: BuildGameUi(m, host); break;
            case MenuPage.Controls: BuildControls(m, host); break;
            case MenuPage.Capture: BuildCapture(m); break;
            case MenuPage.Graphics: BuildGraphics(m); break;
        }
        return m;
    }

    // By the mode's id or name, in any case: a game's own modes have their own ids.
    internal static string ModeIcon(string id, string name)
    {
        foreach (var key in new[] { id, name })
        {
            var k = (key ?? "").ToLowerInvariant();
            if (k.Contains("turntable")) return "disc-3";
            if (k.Contains("orbit")) return "rotate-3d";
        }
        return "box";
    }

    internal static List<MenuHero> ModeHeroes() => VrModes.All.Select(mode =>
    {
        var id = mode.Id;
        return new MenuHero
        {
            Id = id, Name = mode.Name, Description = mode.Description, Icon = ModeIcon(id, mode.Name),
            IsSelected = () => VrModes.Current?.Id == id,
            OnSelect = () => VrModes.Select(id),
        };
    }).ToList();

    static void BuildView(MenuPageModel m)
    {
        m.Heroes.AddRange(ModeHeroes());
        var sections = new List<MenuSection>
        {
            Section(m.Page, "position", "Position", "move-3d", MenuColumn.Left, note: "All modes"),
            Section(m.Page, "look", "Look", "palette", MenuColumn.Left),
            Section(m.Page, "turning", "Turning", "rotate-3d", MenuColumn.Right),
            Section(m.Page, "orbit", "Orbit", "rotate-3d", MenuColumn.Right),
            Section(m.Page, "mode", "This mode", "sliders-horizontal", MenuColumn.Right),
        };
        Fill(m, sections, Rows());
        // "All modes" is true of table height and distance only; say so when only they are there.
        var pos = m.Section("position");
        if (pos != null && pos.AllRows.Any(r => r.Label == "Scale")) pos.Note = "Height and distance: all modes";
    }

    internal static string UiModeIcon(VrUi.UiMode m) => m switch
    {
        VrUi.UiMode.Arranged or VrUi.UiMode.Free or VrUi.UiMode.Separate => "layout-dashboard",
        _ => "app-window",
    };

    static void BuildGameUi(MenuPageModel m, IVrUiHost host)
    {
        foreach (var info in VrUi.Modes)
        {
            var mode = info.Mode;
            string icon = UiModeIcon(mode);
            if (!VrUi.IsAvailable(mode)) continue;
            m.Heroes.Add(new MenuHero
            {
                Id = mode.ToString(), Name = info.Name, Description = info.Description, Icon = icon,
                IsSelected = () => VrUi.Mode == mode,
                OnSelect = () => VrUi.SetMode(mode),
            });
        }
        var ui = VrUi.Mode;
        bool panel = ui == VrUi.UiMode.Panel;
        var sections = new List<MenuSection>
        {
            Section(m.Page, "panel", "Panel", "app-window", MenuColumn.Left),
            Section(m.Page, "windows", "Windows", "layout-dashboard", MenuColumn.Left),
            Section(m.Page, "bars", "Button bars", "keyboard", MenuColumn.Right),
            Section(m.Page, "pieces", "UI pieces", "puzzle", MenuColumn.Right, collapsible: true, open: false),
            // The core's rows about this menu itself (Menu style: Classic / New), always findable.
            Section(m.Page, "menu", "This menu", "app-window", MenuColumn.Right),
        };
        var extra = new List<VrSetting>();
        if (!panel)
        {
            extra.Add(new TextLine("UI", "While this menu is open, grab a window to move it."));
            extra.Add(new DelegateCycler("UI", "Layout", () => VrUi.LayoutName, d => VrUi.StepLayout(d)));
            extra.Add(new DelegateAction("UI", "Start over from layout", "Start over", VrUi.ResetFree));
            extra.Add(new DelegateAction("UI", "Save as new layout", "Save", VrUi.SaveFreeAsNew));
            if (!VrUi.LayoutIsBuiltIn) extra.Add(new DelegateAction("UI", "Save over layout", () => "Save over " + VrUi.LayoutName, VrUi.SaveFreeOverCurrent));
            if (!VrUi.LayoutIsBuiltIn) extra.Add(new DelegateAction("UI", "Delete layout", "Delete", VrUi.DeleteCurrentLayout));
            if (host != null && !host.CanArrange) extra.Add(new TextLine("UI", "Windows appear once you're in a level."));
        }
        // The panel's rows only in Panel mode; the windows' only in Arranged / Free.
        var rows = Rows().Where(r =>
        {
            var slot = Place(r.s, r.tab);
            if (slot.Page != MenuPage.GameUi) return true;
            if (slot.Section == "panel") return panel;
            // 'Held windows' also turns carried button bars, so Panel mode keeps it (under the bars, below).
            if (slot.Section == "windows") return !panel || MenuWording.Label(r.s.Label) == "Held windows";
            return true;
        });
        Fill(m, sections, rows, extra);
        var bars = m.Section("bars");
        if (panel) MoveHeldToBars(m, bars);
        if (bars != null) bars.Note = null;
        var pieces = m.Section("pieces");
        if (pieces != null) pieces.Note = pieces.AllRows.Count(r => r.Label != "Size in the world").ToString();
    }

    // Panel mode has no windows to carry, only button bars: 'Held windows' goes in their section
    // (or away, if the game has none), and the Windows section with it.
    static void MoveHeldToBars(MenuPageModel m, MenuSection bars)
    {
        var windows = m.Section("windows");
        if (windows == null) return;
        var held = windows.Rows.FirstOrDefault(r => r.Setting != null && MenuWording.Label(r.Setting.Label) == "Held windows");
        if (held != null)
        {
            windows.Rows.Remove(held);
            bars?.Rows.Add(held);
        }
        if (windows.IsEmpty) { m.Left.Remove(windows); m.Right.Remove(windows); }
    }

    static string BindIcon(string section) => section switch
    {
        "Laser" => "mouse-pointer-click",
        "Game" => "gamepad",
        "Table" => "box",
        "Placing items" => "package",
        "Free mode windows" => "move",
        _ => "gamepad",
    };

    static readonly string[] LeftBindingSections = { "Laser", "Game" };

    static void BuildControls(MenuPageModel m, IVrUiHost host)
    {
        var mode = VrModes.Current;
        if (mode != null) m.ModePill = mode.Name;
        var you = Section(m.Page, "you", "You", "user", MenuColumn.Left);
        var preset = Section(m.Page, "preset", "Preset", "bookmark", MenuColumn.Left, collapsible: true, open: false);
        var extra = new List<VrSetting>
        {
            new DelegateToggle("Controls", "Left-handed", () => host != null && host.LeftHanded, v => { if (host != null) host.LeftHanded = v; }),
            new DelegateToggle("Controls", "Swap sticks", () => MenuState.SwapSticks, v => MenuState.SwapSticks = v),
            new DelegateCycler("Controls", "Preset", () => VrInput.Preset, d => { try { VrInput.StepPreset(d); } catch (NullReferenceException) { /* no presets before VrInput.Init */ } }),
            new DelegateAction("Controls", "Use preset", "Use", VrInput.UsePreset),
            new DelegateAction("Controls", "Save as new preset", "Save", () => VrInput.SaveAsNewPreset()),
        };
        if (VrInput.Preset != "Default" && VrInput.Presets().Contains(VrInput.Preset))
            extra.Add(new DelegateAction("Controls", "Delete preset", "Delete", VrInput.DeletePreset));
        Fill(m, new List<MenuSection> { you, preset }, Rows(), extra);

        // The bindings, one section per action section, in the order they were registered.
        var bySection = new List<(string name, List<VrAction> actions)>();
        foreach (var a in MenuBindings.Shown())
        {
            int i = bySection.FindIndex(x => x.name == a.Section);
            if (i < 0) { bySection.Add((a.Section, new List<VrAction>())); i = bySection.Count - 1; }
            bySection[i].actions.Add(a);
        }
        foreach (var (name, actions) in bySection)
        {
            var sec = Section(m.Page, "bind:" + name, MenuWording.Label(name ?? "Other"), BindIcon(name), LeftBindingSections.Contains(name) ? MenuColumn.Left : MenuColumn.Right,
                collapsible: true, open: name != "Free mode windows", note: actions.Count.ToString());
            sec.Rows.AddRange(actions.Select(BindingRow));
            (sec.Column == MenuColumn.Left ? m.Left : m.Right).Add(sec);
        }
    }

    // ---- Capture ----

    static VrSetting Find(string tab, string label) =>
        Rows().Where(r => r.tab == tab && r.s.Label == label).Select(r => r.s).FirstOrDefault();

    internal static VrSetting PhotoRow => Find("Record", "Photo");
    internal static VrSetting VideoRow => Find("Record", "Video (needs ffmpeg)");

    // "Saved 12:01:02" -> "Photo saved"; "..." -> "Saving..."; else "Take photo".
    internal static string PhotoText(VrSetting s)
    {
        var v = s?.Value ?? "";
        if (v.StartsWith("Saved", StringComparison.Ordinal)) return "Photo saved";
        if (v == "...") return "Saving...";
        return "Take photo";
    }

    // "Stop  0:12" -> "Recording · 0:12"; else "Record video".
    internal static string VideoText(VrSetting s)
    {
        var v = s?.Value ?? "";
        if (v.StartsWith("Stop", StringComparison.Ordinal)) return "Recording · " + v.Substring(4).Trim();
        return "Record video";
    }

    internal static bool IsRecording(VrSetting s) => (s?.Value ?? "").StartsWith("Stop", StringComparison.Ordinal);

    static void BuildCapture(MenuPageModel m)
    {
        var photo = PhotoRow;
        var video = VideoRow;
        if (photo != null)
            m.Heroes.Add(new MenuHero { Id = "photo", Name = "Take photo", Description = "What the headset sees, saved as a picture", Icon = "camera",
                LiveName = () => PhotoText(photo), OnSelect = photo.Activate });
        if (video != null)
            m.Heroes.Add(new MenuHero { Id = "video", Name = "Record video", Description = "Needs ffmpeg; click again to stop", Icon = "video",
                LiveName = () => VideoText(video), IsSelected = () => IsRecording(video), OnSelect = video.Activate });
        var sections = new List<MenuSection>
        {
            Section(m.Page, "spectator", "Spectator view", "video", MenuColumn.Left),
            Section(m.Page, "recordings", "Recordings", "film", MenuColumn.Right),
        };
        Fill(m, sections, Rows().Where(r => r.s != photo && r.s != video));
    }

    static void BuildGraphics(MenuPageModel m)
    {
        Fill(m, new List<MenuSection> { Section(m.Page, "display", "Display", "monitor", MenuColumn.Left) }, Rows());
    }

    // ---- Home ----

    public static HomeModel Home(IVrUiHost host)
    {
        bool left = host != null && host.LeftHanded;
        var h = new HomeModel
        {
            Title = string.IsNullOrEmpty(VrMenu.Title) ? DefaultTitle : VrMenu.Title,
            Version = VrMenu.Version,
            ShowFirstTime = !MenuState.FirstTimeDismissed,
            FirstTimeText = HelpText.Resolve(VrMenu.Tips.Count > 0 ? VrMenu.Tips[0] : "Hold {bind:table.move} and move your hand to drag the table; with both hands, pull apart to scale it and twist to turn it.")
                + " Your controls are on the right.",
        };
        h.Modes.AddRange(ModeHeroes());

        var mode = VrModes.Current;
        if (mode != null)
        {
            string second = mode.Id == "Turntable" ? "Radius" : "Scale";
            foreach (var want in new[] { "Table height", second, "Background" })
            {
                var s = mode.Options.FirstOrDefault(o => VrMenu.Shows(o) && MenuWording.Label(o.Label) == want);
                if (s != null) h.ViewRows.Add(Row(s, RowKinds.HomeInlineWidth, RowKinds.HomeWidth));
            }
        }

        var photo = PhotoRow;
        var video = VideoRow;
        var recentre = Rows().FirstOrDefault(r => r.tab == "Game" && MenuWording.Label(r.s.Label) == "Recentre table").s
            ?? VrSettings.All.FirstOrDefault(s => VrSettings.TabOf(s) == "Game" && MenuWording.Label(s.Label) == "Recentre table");
        h.QuickActions.Add(new QuickAction { Id = "photo", Label = "Take photo", Icon = "camera", Primary = true,
            IsAvailable = () => photo != null, LiveLabel = () => photo != null ? PhotoText(photo) : "Take photo", OnRun = () => photo?.Activate() });
        h.QuickActions.Add(new QuickAction { Id = "video", Label = "Record video", Icon = "circle-dot",
            IsAvailable = () => video != null, LiveLabel = () => video != null ? VideoText(video) : "Record video", OnRun = () => video?.Activate() });
        h.QuickActions.Add(new QuickAction { Id = "recentre", Label = "Recentre table", Icon = "locate-fixed",
            IsAvailable = () => recentre != null || RecentreTable != null, OnRun = () => { if (recentre != null) recentre.Activate(); else RecentreTable?.Invoke(); } });
        h.QuickActions.Add(new QuickAction { Id = "help", Label = "Help & FAQ", Icon = "circle-help", OnRun = () => MenuState.Page = MenuPage.Help });

        h.ControlsSubtitle = (mode != null ? "For " + mode.Name + " · " : "") + (left ? "left-handed" : "right-handed");
        h.Cheat.AddRange(MenuBindings.CheatSheet(left));
        h.TotalControls = MenuBindings.Shown().Count();

        foreach (var (s, tab) in Rows())
            if (s.Section == "Hotkey bars" && s.Label != "Group names" && s is IToggleSetting)
            {
                var r = Row(s);
                r.Kind = RowKind.ToggleTile;
                h.ButtonBars.Add(r);
            }
        return h;
    }

    // ---- Help ----

    public static HelpModel Help()
    {
        var m = new HelpModel();
        m.Items.AddRange(MenuFaq.Items(GameTitle));
        return m;
    }
}
