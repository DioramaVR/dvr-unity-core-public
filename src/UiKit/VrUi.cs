#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// What the laser is on. Frame: a window being edited that has nothing in it
// (it can be moved, but there is nothing to click).
enum UiHitKind { None, GameUi, Menu, Frame }

struct UiHit
{
    public UiHitKind Kind;
    public Vector2 Pixel;     // GameUi: pixel in the game's UI (y up), for the mouse
    public float Distance;    // along the ray, world units
    public string Window;     // GameUi: the window the laser is on (null: the full panel)
}

// The VR UI framework: how the game's UI is shown (the mode), the windows it
// is split into, saved layouts, and our own VR menu.
//
// A game registers its windows (RegisterWindow), any default layouts
// (RegisterLayout) and its settings (VrSettings.Add), then each frame calls
// Update and, for the laser, Raycast / Click and the grab calls.
static class VrUi
{
    // Where the game's UI goes: Panel (one screen) or Arranged (windows round the
    // table, moved while the VR menu is open; bead vr-jhx, 2026-11-07).
    // Free and Separate are kept only so mods that name them still build: both
    // mean Arranged now (Mode never returns them), and Offer(Separate) means
    // "offers Popout windows" (see Windows).
    public enum UiMode { Panel, Arranged, Free, Separate }

    // How Arranged / Free windows are drawn (user's names, 2026-11-06):
    // Popout: each window's own UI alone (VrPopout), nothing else shows through;
    // Cutout: cut from the one picture of the whole UI.
    public enum WindowDraw { Popout, Cutout }

    public sealed class ModeInfo
    {
        public UiMode Mode;
        public string Name, Description;
    }

    public static readonly ModeInfo[] Modes =
    {
        new() { Mode = UiMode.Panel, Name = "Panel", Description = "The whole UI on one floating screen" },
        new() { Mode = UiMode.Arranged, Name = "Arranged", Description = "Windows round the table: move them while this menu is open" },
    };

    // The UI modes this game offers, in the menu's order. Panel always; Arranged and
    // Free only when the game calls Offer (its windows have been tried that way). They
    // were on by default, so Timberborn (UI Toolkit, no uGUI windows) listed modes that
    // did nothing (2026-11-06).
    static readonly HashSet<UiMode> offered = new() { UiMode.Panel };
    static bool popoutOffered;

    // A game says which UI modes work for it (e.g. Offer(Panel) for a UI that can't be split)
    // and whether its windows can pop out (OfferPopout, or Separate here). Call at load.
    public static void Offer(params UiMode[] modes)
    {
        offered.Clear();
        offered.Add(UiMode.Panel);
        foreach (var m in modes)
        {
            if (m == UiMode.Separate) OfferPopout();
            else offered.Add(m == UiMode.Free ? UiMode.Arranged : m);
        }
        VrUiMenu.MarkDirty();
    }

    // This game's windows can be drawn as Popout (its windows have been tried that
    // way). Also readies the render pipeline for it, once, now (main menu): see VrPopout.PrepareLayers.
    public static void OfferPopout(bool on = true)
    {
        popoutOffered = on;
        if (on) VrPopout.PrepareLayers(VrCore.Info);
        VrUiMenu.MarkDirty();
    }

    public static bool IsAvailable(UiMode mode) => offered.Contains(mode);
    public static bool PopoutOffered => popoutOffered;

    // While true, the core may still widen the URP renderers for Popout (VrPopout.PrepareLayers,
    // retried each frame until the renderers can be read). Set it so it turns false once a level
    // is loaded (widening then can leave the level unlit: TPM). Null: no limit.
    public static Func<bool> PopoutPrepareWhile;

    // How Arranged / Free windows are drawn now: Popout where the game offers it and the user chose it.
    // Cutout too when Popout couldn't be prepared this session (VrPopout.GaveUp): never empty windows.
    public static WindowDraw Windows => popoutOffered && !VrPopout.GaveUp && windowsEntry?.Value != WindowDraw.Cutout ? WindowDraw.Popout : WindowDraw.Cutout;
    static ConfigEntry<WindowDraw> windowsEntry;

    // The old names for Arranged (a saved Free or Separate reads as Arranged).
    static UiMode Merged(UiMode m) => m is UiMode.Free or UiMode.Separate ? UiMode.Arranged : m;

    static readonly List<UiWindowDef> defs = new();
    static readonly List<UiWindow> windows = new();
    static readonly List<UiLayout> builtInLayouts = new();
    static List<UiLayout> layouts = new();
    static readonly HashSet<string> excludedNames = new();

    static ConfigEntry<UiMode> modeEntry;
    static ConfigEntry<string> layoutEntry;
    static string layoutDir;
    static IVrUiHost host;
    static float nextBounds;
    static bool loggedArranged;

    // A mode the game doesn't offer (any more) reads as Panel; the config keeps the user's choice.
    public static UiMode Mode => modeEntry == null ? UiMode.Panel
        : IsAvailable(Merged(modeEntry.Value)) ? Merged(modeEntry.Value) : UiMode.Panel;
    public static string LayoutName => CurrentLayout()?.Name ?? "-";
    public static bool LayoutIsBuiltIn => CurrentLayout()?.BuiltIn ?? true;
    public static bool MenuOpen => VrUiMenu.Open;
    public static string LastSaved { get; private set; }

    // Binds the framework's own settings ([VrUi] in the game's config file).
    public static void Init(ConfigFile config, string layoutFolder)
    {
        modeEntry = config.Bind("VrUi", "Mode", UiMode.Panel, "How the game's UI is shown: Panel (one screen) or Arranged (windows round the table, moved while the VR menu is open). Changed from the VR menu.");
        layoutEntry = config.Bind("VrUi", "Layout", "Default", "The layout Arranged last loaded (files in BepInEx/config/" + layoutFolder + ").");
        layoutDir = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath), layoutFolder);
        windowsEntry = config.Bind("VrUi", "Windows", WindowDraw.Popout, "Arranged: Popout (each window drawn on its own, nothing shows through) or Cutout (cut from the one picture of the whole UI). Popout only in games that offer it.");
        VrSettings.Add(new ChoiceSetting("UI", "Windows",
            () => new[] { "Popout", "Cutout" },
            () => Windows.ToString(),
            v => { if (Enum.TryParse<WindowDraw>(v, out var d)) { windowsEntry.Value = d; host?.Log($"[vrui] windows {d}"); } })
        {
            When = () => popoutOffered && Mode == UiMode.Arranged,
        });
        // Mode = Separate (before Popout / Cutout): Free with Popout windows, keeping the arrangement made in it.
        if (modeEntry.Value == UiMode.Separate) MigrateSeparate();
        MigrateToOneArranged(config);
        ReloadLayouts();
        VrModes.Init(config);
        VrHotkeys.Init(config);
        VrPane.Init(config);
        VrPieces.Init(config);
        VrUiLog.Init();
        VrLabels.Init(config);
        VrUiMenu.InitMenu(config);
        heldFacesUser = config.Bind("VrUi", "HeldWindowsFaceYou", false, "A window or button bar being moved keeps facing you, instead of turning with the controller holding it.");
        VrSettings.Add(new ChoiceSetting("UI", "Held windows",
            () => new[] { "Turn with hand", "Face you" },
            () => heldFacesUser.Value ? "Face you" : "Turn with hand",
            v => heldFacesUser.Value = v == "Face you"));
    }

    // Windows on a wrist are always shown (user's decision, 2026-11-06, bead
    // vr-4j0.12): no toggle button, no menu row. The old toggle sat on the
    // off-hand trigger, half of the menu chord. Kept so mods that still call it
    // build; it does nothing.
    public static void WristButton(bool pressed, bool held) { }

    public static bool WristShown => true;

    public static void RegisterWindow(UiWindowDef def)
    {
        defs.Add(def);
        windows.Add(new UiWindow(def));
    }

    // A named piece of the game's UI: the objects that draw it, and what it is.
    // Screen pieces are ordinary windows (as RegisterWindow); World and Mouse
    // pieces get a sparse window that shows only what they draw (see UiWindowDef).
    public static void RegisterPiece(string id, string title, UiPieceKind kind, Func<IEnumerable<Transform>> roots) =>
        RegisterWindow(new UiWindowDef { Id = id, Title = title, Kind = kind, Roots = roots });

    // Roots for a piece by object name (or a prefix ending in "*"), wherever they
    // are on the UI canvases, as Exclude matches them. Only active objects.
    public static Func<IEnumerable<Transform>> Named(params string[] names)
    {
        var set = new HashSet<string>(names);
        return () => FindNamed(set);
    }

    static IEnumerable<Transform> FindNamed(HashSet<string> names)
    {
        if (host?.UiCanvases == null) yield break;
        foreach (var canvas in host.UiCanvases)
        {
            if (canvas == null || !canvas.isActiveAndEnabled) continue;
            foreach (var t in canvas.GetComponentsInChildren<Transform>())
                if (UiBounds.NameMatches(t.name, names)) yield return t;
        }
    }

    // UI objects (by name, or a prefix ending in "*") that no window shows, e.g. the game's own cursor or
    // world-space markers drawn through the screen UI.
    public static void Exclude(params string[] names)
    {
        foreach (var n in names) excludedNames.Add(n);
    }

    // UI objects found some other way (e.g. by component type) that no window shows.
    public static void Exclude(Func<IEnumerable<Transform>> roots) => excludedRoots.Add(roots);
    static readonly List<Func<IEnumerable<Transform>>> excludedRoots = new();

    public static void RegisterLayout(UiLayout layout)
    {
        layout.BuiltIn = true;
        builtInLayouts.Add(layout);
        ReloadLayouts();
    }

    // Built-in layouts first; a file with the same name overrides the built-in one.
    static void ReloadLayouts()
    {
        var files = layoutDir != null ? UiLayout.LoadAll(layoutDir) : new List<UiLayout>();
        layouts = builtInLayouts.Where(b => files.All(f => f.Name != b.Name)).Concat(files).ToList();
    }

    static UiLayout CurrentLayout() =>
        layouts.FirstOrDefault(l => l.Name == layoutEntry?.Value) ?? layouts.FirstOrDefault();

    public static void SetMode(UiMode mode)
    {
        mode = Merged(mode);
        if (!IsAvailable(mode) || modeEntry == null) return;
        EndGrab();
        modeEntry.Value = mode;
        loggedArranged = false;
        LastSaved = null;
        host?.Log($"[vrui] mode {mode}");
        VrUiMenu.MarkDirty();
    }

    // Picking a layout loads it: the windows go where it has them (the arrangement
    // you see is a working copy, moved while the menu is open and saved as a layout).
    public static void StepLayout(int direction)
    {
        if (layouts.Count == 0) return;
        int i = Math.Max(0, layouts.IndexOf(CurrentLayout()));
        SelectLayout(layouts[(i + direction + layouts.Count) % layouts.Count].Name);
    }

    public static void SelectLayout(string name)
    {
        if (layoutEntry == null || layouts.All(l => l.Name != name)) return;
        layoutEntry.Value = name;
        loggedArranged = false;
        ResetFree();
        host?.Log($"[vrui] layout {layoutEntry.Value}");
    }

    public static void ToggleMenu()
    {
        if (host != null) VrUiMenu.Toggle(host);
    }

    // ---- Arranged: a working copy of a layout, edited by grabbing windows while the menu is open ----

    // Kept in its own file (not a *.layout.txt) so it survives restarts without
    // showing up in the layout list until the user saves it as one. One for
    // Free, whether its windows are Popout or Cutout; loaded when first used.
    static UiLayout working;
    static bool loadedWorking;
    static UiLayout freeLayout
    {
        get
        {
            if (!loadedWorking) LoadFree();
            return working;
        }
        set => working = value;
    }
    static string FreePath => layoutDir != null ? Path.Combine(layoutDir, "free.txt") : null;

    static void LoadFree()
    {
        loadedWorking = true;
        try
        {
            working = FreePath != null && File.Exists(FreePath) ? UiLayout.Parse(File.ReadAllText(FreePath), "Free") : null;
        }
        catch (Exception) { working = null; }
    }

    // Separate (a mode for a few hours on 2026-11-06) became Free with Popout
    // windows. Its arrangement (separate.txt) is the newer one, so it becomes
    // Free's; the old Free arrangement is kept as free.before-popout.txt.
    static void MigrateSeparate()
    {
        modeEntry.Value = UiMode.Free;
        windowsEntry.Value = WindowDraw.Popout;
        try
        {
            var sep = Path.Combine(layoutDir, "separate.txt");
            var free = Path.Combine(layoutDir, "free.txt");
            if (File.Exists(sep))
            {
                if (File.Exists(free)) File.Copy(free, Path.Combine(layoutDir, "free.before-popout.txt"), true);
                File.Copy(sep, free, true);
                File.Delete(sep);
            }
        }
        catch (Exception e) { VrCore.Warn("[vrui] moving the Separate arrangement to Free: " + e.Message); }
        loadedWorking = false;
        VrCore.Info("[vrui] UI mode Separate is now Free with Popout windows");
    }

    // Arranged and Free became one mode (2026-11-07). Arranged used to show the
    // selected layout as it is, and Free its own working copy (free.txt). Now
    // Arranged always shows the working copy. So, once: someone in Arranged starts
    // from their layout (an old free.txt is kept as free.before-arranged.txt), and
    // someone in Free keeps their arrangement and is now in Arranged.
    static void MigrateToOneArranged(ConfigFile config)
    {
        var done = config.Bind("VrUi", "OneArranged", false, "Set once Arranged and Free were merged into Arranged (don't change).");
        if (done.Value) return;
        done.Value = true;
        if (modeEntry.Value is UiMode.Free or UiMode.Separate) modeEntry.Value = UiMode.Arranged;
        else if (modeEntry.Value == UiMode.Arranged && FreePath != null && File.Exists(FreePath))
        {
            try { File.Copy(FreePath, Path.Combine(layoutDir, "free.before-arranged.txt"), true); File.Delete(FreePath); }
            catch (Exception e) { VrCore.Warn("[vrui] keeping the old Free arrangement: " + e.Message); }
        }
        loadedWorking = false;
        VrCore.Info("[vrui] Arranged and Free are one mode now: Arranged, moved while the VR menu is open");
    }

    // Re-read the Free layout from its file (after something else changed it).
    public static void ReloadFree()
    {
        EndGrab();
        LoadFree();
    }

    static void SaveFree()
    {
        if (freeLayout == null || FreePath == null) return;
        Directory.CreateDirectory(layoutDir);
        File.WriteAllText(FreePath, freeLayout.Format());
    }

    static UiLayout FreeLayout()
    {
        if (freeLayout == null && CurrentLayout() != null)
        {
            freeLayout = CurrentLayout().Clone("Free");
            SaveFree();
        }
        // Windows the game registered after the file was written start where the layout has them.
        if (freeLayout != null && CurrentLayout() != null)
            foreach (var p in CurrentLayout().Placements)
                if (freeLayout.For(p.Window) == null) freeLayout.Placements.Add(p.Clone());
        return freeLayout;
    }

    // Start the arrangement over from the selected layout.
    public static void ResetFree()
    {
        EndGrab();
        freeLayout = null;
        FreeLayout();
        LastSaved = null;
        host?.Log($"[vrui] free layout reset from '{LayoutName}'");
    }

    // Save the Free arrangement as a new layout file (and select it).
    public static void SaveFreeAsNew()
    {
        if (FreeLayout() == null) return;
        int n = 1;
        while (layouts.Any(l => l.Name == $"Custom {n}")) n++;
        SaveFreeAs($"Custom {n}");
    }

    // Overwrite the selected layout with the Free arrangement (not built-in ones).
    public static void SaveFreeOverCurrent()
    {
        if (FreeLayout() == null || LayoutIsBuiltIn) return;
        SaveFreeAs(LayoutName);
    }

    static void SaveFreeAs(string name)
    {
        var copy = freeLayout.Clone(name);
        // Overwriting keeps the file it came from, whatever it is called.
        copy.SourcePath = layouts.FirstOrDefault(l => l.Name == name)?.SourcePath;
        copy.Save(layoutDir);
        ReloadLayouts();
        layoutEntry.Value = name;
        LastSaved = name;
        host?.Log($"[vrui] saved layout '{name}' to {copy.SourcePath ?? copy.FilePath(layoutDir)}");
    }

    // Deletes a layout file the user saved (built-in layouts stay; deleting a
    // file that overrides one brings the built-in one back).
    public static void DeleteCurrentLayout()
    {
        var current = CurrentLayout();
        if (current?.SourcePath == null) return;
        if (File.Exists(current.SourcePath)) File.Delete(current.SourcePath);
        ReloadLayouts();
        layoutEntry.Value = CurrentLayout()?.Name ?? "Default";
        LastSaved = null;
        host?.Log($"[vrui] deleted layout '{current.Name}'");
    }

    // Arranged shows the working copy (free.txt), made from the selected layout the first time.
    static UiLayout ActiveLayout() => FreeLayout();

    // A window's place in the Free layout; a sparse piece it doesn't have yet
    // joins it where it is now (over the panel), so it can be grabbed.
    static UiPlacement FreePlacement(UiWindow w)
    {
        var layout = FreeLayout();
        var p = layout.For(w.Def.Id);
        if (p == null && w.Def.Sparse)
        {
            p = PlacementOf(layout, w).Clone();
            layout.Placements.Add(p);
        }
        p?.DefaultPivot(w.Def.Pin);
        return p;
    }

    static bool Arranging => host != null && host.Active && host.CanArrange &&
        Mode == UiMode.Arranged && ActiveLayout() != null;

    // Windows move only while the VR menu is open; closed, they stay put.
    static bool Editing => Arranging && VrUiMenu.Open;
    static bool pieceRows;

    public static void Update(IVrUiHost h)
    {
        host = h;
        if (!pieceRows) { pieceRows = true; VrPieces.AddRows(windows); }
        VrUiLog.Update(h);
        VrWorldUi.Update(h);
        if (popoutOffered) VrPopout.PrepareLayers(h.Log); // once; retried here if the pipeline wasn't ready at Offer
        VrUiMenu.Update(h);
        VrHotbar.Update(h);
        VrPane.UpdateOutline(h, onPane);
        VrPieces.BeginFrame();
        try { UpdateWindows(h); }
        finally { VrPieces.EndFrame(); }
    }

    static void UpdateWindows(IVrUiHost h)
    {
        bool arranging = Arranging;
        h.ShowFullPanel(!arranging);
        if (!arranging || h.UiCamera == null)
        {
            EndGrab();
            VrPopout.Release(h, windows);
            if (h.UiCamera != null && h.Active && h.CanArrange && Mode == UiMode.Panel) UpdatePanelPieces(h);
            else foreach (var w in windows) w.Hide();
            return;
        }

        // Window contents change as menus open and close; re-measure a few times a second.
        if (Time.unscaledTime >= nextBounds)
        {
            nextBounds = Time.unscaledTime + 0.25f;
            MeasureWindows(h);
        }
        // Sparse windows' bits move with the world and the mouse: every frame.
        foreach (var w in windows)
            if (w.Def.Sparse) MeasureSparse(h, w);
        // Popout windows: each window's roots drawn alone into a clean picture (w.Own).
        if (Windows == WindowDraw.Popout) VrPopout.Update(h, windows);
        else VrPopout.Release(h, windows);

        var layout = ActiveLayout();
        bool editing = Editing;
        // The trigger on a pin dot pins that window there (it doesn't move).
        if (editing && hoverWindow != null && hoverPin is UiPin picked && VrInput.PointerTriggerPressed)
            SetPin(hoverWindow, picked);
        // Window names while they can be moved (VrLabels).
        bool names = editing && VrLabels.On && VrLabels.Ensure(h, windows);
        foreach (var w in windows)
        {
            // The piece's own choice first (UI tab): hidden, at the laser, in the world.
            var place = grab?.Window == w ? VrPieces.Place.Window : VrPieces.PlaceOf(w.Def);
            if (place == VrPieces.Place.Hidden) { w.Hide(); continue; }
            if (place == VrPieces.Place.AtLaser && VrPieces.ShowAtLaser(h, w)) continue;
            if (place == VrPieces.Place.World && VrPieces.ShowInWorld(h, w)) continue;
            var p = PlacementOf(layout, w);
            if (p == null) { w.Hide(); continue; }
            var highlight = !editing ? UiWindow.Highlight.None
                : grab?.Window == w ? (SnapTarget() != null ? UiWindow.Highlight.Snap : UiWindow.Highlight.Held)
                : hoverWindow == w ? UiWindow.Highlight.Hover
                : UiWindow.Highlight.Idle;
            w.ShowPinDots = editing && (grab?.Window == w || hoverWindow == w);
            w.HoverPin = hoverWindow == w ? hoverPin : null;
            w.Place(h, AnchorPose(h, p.Anchor), p, highlight);
            // Free: the window's name above it (VrLabels).
            if (names && VrLabels.Row(w.Def.Id, out var uv, out float aspect)) w.ShowLabel(h, VrLabels.Atlas, uv, aspect);
            else w.HideLabel();
        }
        if (!loggedArranged)
        {
            loggedArranged = true;
            h.Log($"[vrui] {Mode} with layout '{layout.Name}': " + string.Join(", ", windows.Select(w => $"{w.Def.Id} {w.Source}")));
        }
    }

    // Panel mode: the panel shows everything, except World / Mouse pieces set to
    // the world or the laser, which go there and are cut out of the panel
    // (VrPieces.PanelCuts, applied by ScreenUiPanel).
    static void UpdatePanelPieces(IVrUiHost h)
    {
        if (Time.unscaledTime >= nextBounds)
        {
            nextBounds = Time.unscaledTime + 0.25f;
            MeasureWindows(h);
        }
        foreach (var w in windows)
        {
            var place = w.Def.Sparse ? VrPieces.PlaceOf(w.Def) : VrPieces.Place.Window;
            bool shown = false;
            if (place is VrPieces.Place.AtLaser or VrPieces.Place.World)
            {
                MeasureSparse(h, w);
                shown = place == VrPieces.Place.AtLaser ? VrPieces.ShowAtLaser(h, w) : VrPieces.ShowInWorld(h, w);
            }
            if (!shown) w.Hide();
        }
    }

    static void MeasureWindows(IVrUiHost h)
    {
        var claimed = new HashSet<long>();
        foreach (var roots in excludedRoots)
            foreach (var r in roots())
                if (r != null) claimed.Add(VrCore.Key(r));
        var rootsOf = new Dictionary<UiWindow, List<Transform>>();
        foreach (var w in windows)
        {
            if (w.Def.Remainder) continue;
            var roots = w.Def.Roots().Where(r => r != null).ToList();
            rootsOf[w] = roots;
            foreach (var r in roots) claimed.Add(VrCore.Key(r));
            // Sparse windows draw their roots' parts; Popout draws every window's roots alone.
            w.Roots.Clear();
            w.Roots.AddRange(roots);
        }
        // A window doesn't count what another piece (or Exclude) has under its roots.
        foreach (var kv in rootsOf)
        {
            var w = kv.Key;
            if (w.Def.Sparse) continue;
            var others = new HashSet<long>(claimed);
            foreach (var r in kv.Value) others.Remove(VrCore.Key(r));
            w.Source = w.Def.OwnRect ? UiBounds.OfOwnRects(h.UiCamera, kv.Value) : UiBounds.Of(h.UiCamera, kv.Value, others, excludedNames);
        }
        foreach (var w in windows)
        {
            if (!w.Def.Remainder) continue;
            var trace = TraceRemainder ? new List<string>() : null;
            var before = w.Source;
            w.Source = UiBounds.Remainder(h.UiCamera, h.UiCanvases, claimed, excludedNames, trace);
            if (trace != null && (Math.Abs(before.width - w.Source.width) > 20 || Math.Abs(before.height - w.Source.height) > 20))
                h.Log($"[vrui] '{w.Def.Id}' now {w.Source}:\n  " + string.Join("\n  ", trace.Take(25)));
        }
    }

    static void MeasureSparse(IVrUiHost h, UiWindow w)
    {
        // Roots as last found (a few times a second): finding them can be slow.
        UiBounds.Parts(h.UiCamera, w.Roots, w.Parts);
        w.Source = w.Parts.Count > 0 ? new Rect(0, 0, Screen.width, Screen.height) : default;
    }

    // A window's place in a layout. Sparse pieces the layout doesn't mention sit
    // where the game's UI panel is, at its size, so they show over the panel's spot.
    static UiPlacement PlacementOf(UiLayout layout, UiWindow w)
    {
        var p = layout.For(w.Def.Id);
        p?.DefaultPivot(w.Def.Pin);
        if (p == null && w.Def.OpensFrom != null) return BesideOpener(layout, w);
        if (p != null || !w.Def.Sparse) return p;
        if (!panelPlaces.TryGetValue(w.Def.Id, out p)) panelPlaces[w.Def.Id] = p = new UiPlacement { Window = w.Def.Id };
        p.Anchor = UiAnchor.Room;
        // No panel placed yet (a mod without one): in front, at a panel's usual place and size.
        var pose = VrPane.Known ? VrPane.CarryPose : new Pose(new NVec(0, 1.4f, 1.4f), Pose.Pitch(10f));
        p.Position = pose.Position;
        p.SetRotation(pose.Rotation);
        p.Size = (VrPane.Known ? VrPane.Width : 1.6f) / Math.Max(1, Screen.width) * 1000f;
        p.PivotX = p.PivotY = 0.5f;
        return p;
    }
    static readonly Dictionary<string, UiPlacement> panelPlaces = new();

    // A window the game opens from another (ATS: the build list from the build bar)
    // and that the layout doesn't place: beside its opener, on the side
    // UiWindowDef.OpensSide says, at the opener's size, facing the same way.
    static UiPlacement BesideOpener(UiLayout layout, UiWindow w)
    {
        var opener = windows.FirstOrDefault(x => x.Def.Id == w.Def.OpensFrom);
        var op = opener != null ? layout.For(opener.Def.Id) : null;
        if (opener == null || op == null || !opener.Visible || opener.TrackingWidth <= 0) return null;
        if (!panelPlaces.TryGetValue(w.Def.Id, out var p)) panelPlaces[w.Def.Id] = p = new UiPlacement { Window = w.Def.Id };
        var centre = opener.TrackingCentre;
        float ownW = Math.Max(1, w.Source.width) / 1000f * op.Size, ownH = Math.Max(1, w.Source.height) / 1000f * op.Size;
        const float gap = 0.02f;
        var offset = w.Def.OpensSide switch
        {
            OpenSide.Left => new NVec(-(opener.TrackingWidth + ownW) * 0.5f - gap, 0, 0),
            OpenSide.Above => new NVec(0, (opener.TrackingHeight + ownH) * 0.5f + gap, 0),
            OpenSide.Below => new NVec(0, -(opener.TrackingHeight + ownH) * 0.5f - gap, 0),
            _ => new NVec((opener.TrackingWidth + ownW) * 0.5f + gap, 0, 0),
        };
        var pose = centre.Compose(new Pose(offset, NQuat.Identity));
        p.Anchor = UiAnchor.Room;
        p.Position = pose.Position;
        p.SetRotation(pose.Rotation);
        p.Size = op.Size;
        p.PivotX = p.PivotY = 0.5f;
        return p;
    }

    // Whether a UI object belongs to a window (is under one of its roots as last
    // measured): for the mod's clicks with Popout windows, where windows that overlap
    // on the game's screen are apart in VR, and a raycast can report both.
    public static bool Owns(string windowId, GameObject target)
    {
        var w = windows.FirstOrDefault(x => x.Def.Id == windowId);
        if (w == null || target == null) return false;
        for (var t = target.transform; t != null; t = t.parent)
            foreach (var r in w.Roots)
                if (r != null && VrCore.Same(r, t)) return true;
        return false;
    }

    // Log what the remainder windows pick up whenever they change size (diagnostics).
    public static bool TraceRemainder;

    static Pose AnchorPose(IVrUiHost h, UiAnchor anchor) => anchor switch
    {
        UiAnchor.Table => new Pose(new NVec(h.TableCentre.x, h.TableCentre.y, h.TableCentre.z), NQuat.Identity),
        UiAnchor.Head => h.Head,
        UiAnchor.OffHand => h.LeftHanded ? h.RightHand : h.LeftHand,
        UiAnchor.PointerHand => h.LeftHanded ? h.LeftHand : h.RightHand,
        _ => Pose.Identity,
    };

    // The window under the laser at the last Raycast, for grabbing and highlighting.
    static UiWindow hoverWindow;
    static UiPin? hoverPin; // the pin dot under the laser, on hoverWindow
    static Vector2 hoverPixel;
    static float hoverDistance;

    // The nearest thing the laser hits: our menu, then any UI window.
    // (The full panel, in Panel mode, is raycast by the game's mod as before.)
    public static UiHit Raycast(NVec origin, NVec dir)
    {
        hoverWindow = null;
        var best = new UiHit { Distance = float.MaxValue };
        if (host == null) return default;
        if (VrUiMenu.Raycast(host, origin, dir, out float md)) best = new UiHit { Kind = UiHitKind.Menu, Distance = md };
        // The hotkey bar counts as the menu: the game sends its clicks to Click() below.
        onHotbar = VrHotbar.Raycast(host, origin, dir, out float hd) && hd < best.Distance;
        if (onHotbar) best = new UiHit { Kind = UiHitKind.Menu, Distance = hd };
        if (Arranging)
            foreach (var w in windows)
                if (w.Raycast(origin, dir, out var px, out float d) && d < best.Distance)
                {
                    // On one of its pin dots (shown while arranging): ours, not the game's.
                    var pin = Editing ? w.PinAt(px) : null;
                    best = new UiHit { Kind = w.Empty || pin != null ? UiHitKind.Frame : UiHitKind.GameUi, Pixel = px, Distance = d, Window = w.Def.Id };
                    hoverWindow = w;
                    hoverPixel = px;
                    hoverDistance = d;
                    hoverPin = pin;
                }
        if (best.Kind == UiHitKind.Menu) hoverWindow = null;
        if (hoverWindow == null) hoverPin = null;
        onMenu = best.Kind == UiHitKind.Menu && !onHotbar;
        if (best.Kind != UiHitKind.Menu) onHotbar = false;
        // The game's UI pane is raycast by the mod for clicks; here only to know
        // the grip would pick it up (nothing of ours in front of it).
        onPane = VrPane.Raycast(host, origin, dir, out float pd) && pd < best.Distance;
        return best.Kind == UiHitKind.None ? default : best;
    }

    static bool onHotbar, onMenu, onPane;
    internal static IVrUiHost Host => host;

    // ---- Carrying the VR menu, the hotkey bar and the game's UI pane ----
    //
    // The same hands as Arranged windows: point the laser at one and hold the
    // grip, or touch it with the other hand and grip. Whichever hand holds it,
    // that hand's own stick (the physical one, whatever "swap sticks" says)
    // pushes it away / pulls it in (up / down) and makes it bigger / smaller
    // (right / left): the same in every game (STANDARDS.md). The hotkey bars and
    // the game's UI pane (VrPane, Panel mode) move while the VR menu is open, as
    // windows do, each outlined in pink (UiOutline). From VrInput.Update, before the
    // game reads its actions: the grip and stick doing the carrying are hidden
    // from the game's actions.

    enum Panel { None, Menu, Hotbar, Pane }
    static Panel carried;
    static HandDrag carry;

    public static bool CarryingPanel => carried != Panel.None;

    internal static void CarryPanels(IVrUiHost h)
    {
        if (carried != Panel.None)
        {
            bool can = carried switch { Panel.Menu => VrUiMenu.Shown, Panel.Hotbar => VrHotbar.CanCarry, _ => VrPane.CanCarry };
            if (!h.Active || !VrInput.GripHeld(carry.ByOffHand) || !can)
            {
                EndCarry(h);
                return;
            }
            {
                // The holding hand's own stick, by the laser or by the other hand.
                float dt = Time.unscaledDeltaTime;
                var (x, y) = VrInput.HandStick(carry.ByOffHand);
                carry.Push(y, dt);
                if (MathF.Abs(x) > StickDeadZone)
                {
                    float factor = MathF.Pow(SizeRate, x * dt);
                    if (carried == Panel.Menu) VrUiMenu.Resize(factor);
                    else if (carried == Panel.Hotbar) VrHotbar.Resize(factor);
                    else VrPane.Resize(factor);
                }
            }
            // The bars turn like a held window ('Held windows'); the menu and the pane face you.
            var pose = carry.Carry(h, carried == Panel.Hotbar && HeldTurnsWithHand);
            if (carried == Panel.Menu) VrUiMenu.CarryTo(pose);
            else if (carried == Panel.Hotbar) VrHotbar.CarryTo(pose);
            else VrPane.CarryTo(pose);
            return;
        }

        var target = Panel.None;
        bool byOff = false;
        if (VrInput.GripPressed(false))
            target = onHotbar && VrHotbar.CanCarry ? Panel.Hotbar : onMenu && VrUiMenu.Shown ? Panel.Menu
                : onPane && VrPane.CanCarry ? Panel.Pane : Panel.None;
        if (target == Panel.None && VrInput.GripPressed(true))
        {
            var hand = HandDrag.OffHand(h).Position;
            float dm = VrUiMenu.DistanceTo(hand), dh = VrHotbar.DistanceTo(hand), dp = VrPane.DistanceTo(hand);
            target = dh < HandReach && dh <= dm ? Panel.Hotbar : dm < HandReach ? Panel.Menu : dp < HandReach ? Panel.Pane : Panel.None;
            byOff = target != Panel.None;
        }
        if (target == Panel.None) return;

        carried = target;
        // The bar (of several) under the laser or nearest the hand is chosen as it's picked up.
        var start = target switch { Panel.Menu => VrUiMenu.CarryPose, Panel.Hotbar => VrHotbar.BeginCarry(h, byOff), _ => VrPane.CarryPose };
        carry.Begin(h, start, byOff);
        if (target == Panel.Pane) VrPane.BeginCarry(byOff);
        VrInput.Claim(byOff, true);
        h.Log($"[vrui] {(byOff ? "off hand" : "laser")} carrying the {target switch { Panel.Menu => "VR menu", Panel.Hotbar => "hotkey bar", _ => "game UI panel" }}");
    }

    static void EndCarry(IVrUiHost h)
    {
        if (carried == Panel.Menu) VrUiMenu.EndCarry(h);
        else if (carried == Panel.Hotbar) VrHotbar.EndCarry(h, carry.ByOffHand);
        else if (carried == Panel.Pane) VrPane.EndCarry(h, carry.ByOffHand);
        carried = Panel.None;
        carry.Active = false;
        VrInput.Unclaim();
    }

    public static void Click()
    {
        if (onHotbar) VrHotbar.Click(host);
        else VrUiMenu.Click();
    }

    // For scripted tests: the tracking-space centre of a hotkey bar button, by label.
    public static NVec? HotbarPointOf(string label) => VrHotbar.PointOf(label);

    // ---- Moving windows (Arranged, while the VR menu is open) ----
    //
    // The laser hand grabs a window by pointing at it: it stays on the laser at
    // the distance it was grabbed and keeps the grabbed spot under the laser.
    // The laser hand's stick pushes it away / pulls it in (up / down) and makes
    // it bigger / smaller (right / left).
    //
    // The off hand grabs a window by touching it (including one held on the
    // laser, which hands it over) and carries it. Either way it turns with the
    // controller holding it (or faces the user, a setting). Letting go of a window close
    // to the other hand's wrist puts it on that wrist, where it rides along
    // exactly as it was dropped; grabbing it again takes it off.

    sealed class GrabState
    {
        public UiWindow Window;
        public UiPlacement Placement;
        public bool ByHand;      // carried by the off hand, not on the laser
        public float Distance;   // laser: tracking metres along the laser
        public float OffX, OffY; // laser: from the grabbed spot to the window's pivot, metres in the window's plane
        public NVec HandOffset;  // hand: the window's pivot in the hand's space
        public NQuat Turn;       // the window's rotation relative to the laser / hand holding it
    }

    // While held, the window turns with the controller (twist the laser to roll
    // it, lay it along a wrist) unless the player chose to have it face them.
    static ConfigEntry<bool> heldFacesUser;

    // 'Held windows' is Turn with hand (the default): windows and button bars keep their turn on the hand.
    internal static bool HeldTurnsWithHand => heldFacesUser?.Value != true;

    static NQuat HeldRotation(Pose holder, NVec spot) =>
        heldFacesUser?.Value == true ? FacingRotation(spot) : NQuat.Normalize(holder.Rotation * grab.Turn);

    static NQuat FacingRotation(NVec spot)
    {
        var (yaw, pitch) = UiGeometry.Facing(spot, host.Head.Position);
        return NQuat.Normalize(Pose.Yaw(yaw) * Pose.Pitch(pitch));
    }
    static GrabState grab;

    const float StickDeadZone = 0.2f;
    const float DistanceRate = 2f;   // factor per second at full stick
    const float SizeRate = 1.8f;
    const float HandReach = 0.08f;   // metres: how close the off hand must be to grab a window
    internal const float WristBack = 0.08f;   // metres behind the controller's origin
    internal const float SnapReach = 0.12f;   // metres: window to wrist, to put it on the wrist

    // The wrist point on a hand, where windows and button bars snap on.
    internal static NVec WristPoint(Pose hand) => hand.Position - NVec.Normalize(hand.Forward) * WristBack;

    public static bool GrabbingWindow => grab != null;
    public static bool GrabbingWithLaser => grab != null && !grab.ByHand;
    public static bool CanGrab => Editing && hoverWindow != null;

    // A placement's pivot in tracking space.
    static Pose PlacementPose(UiPlacement p) => AnchorPose(host, p.Anchor).Compose(new Pose(p.Position, p.Rotation));

    // Put a placement's pivot at a tracking-space pose, stored in the given anchor's space.
    static void SetPlacement(UiPlacement p, UiAnchor anchor, Pose pivot)
    {
        p.Anchor = anchor;
        var a = AnchorPose(host, anchor);
        var inverse = NQuat.Inverse(a.Rotation);
        p.Position = NVec.Transform(pivot.Position - a.Position, inverse);
        p.SetRotation(NQuat.Normalize(inverse * pivot.Rotation));
        p.Hidden = false;
    }

    // Taking a window off a wrist (or out of the room) leaves it where it is,
    // stored relative to the table, so it follows table height changes like the built-in layouts.
    static void Detach(UiPlacement p)
    {
        if (p.OnHand || p.Anchor == UiAnchor.Room) SetPlacement(p, UiAnchor.Table, PlacementPose(p));
    }

    // aim: the laser in tracking space (its +Z is the ray).
    public static bool BeginGrab(Pose aim)
    {
        if (!CanGrab || host == null) return false;
        var p = FreePlacement(hoverWindow);
        if (p == null) return false;
        Detach(p);
        var shown = hoverWindow.Shown;
        float pivotX = shown.x + p.PivotX * shown.width, pivotY = shown.y + p.PivotY * shown.height;
        grab = new GrabState
        {
            Window = hoverWindow,
            Placement = p,
            Distance = hoverDistance / host.WorldScale,
            OffX = (pivotX - hoverPixel.x) / 1000f * p.Size,
            OffY = (pivotY - hoverPixel.y) / 1000f * p.Size,
            Turn = NQuat.Normalize(NQuat.Inverse(aim.Rotation) * PlacementPose(p).Rotation),
        };
        VrInput.ClaimStick(false);
        host.Log($"[vrui] grabbed '{p.Window}' at {grab.Distance:F2} m");
        return true;
    }

    // The off hand, every frame (before anything else uses its grip): grabs the
    // window it is touching, carries it, and lets go. Returns true while holding one.
    public static bool HandGrab(Pose offHand, bool pressed, bool held)
    {
        if (grab != null && grab.ByHand)
        {
            if (!held) EndGrab();
            else if (host != null)
            {
                // The holding hand's own stick: up / down pushes it out from the hand or pulls it in, right / left resizes it.
                float dt = Time.unscaledDeltaTime;
                var (sx, sy) = VrInput.HandStick(true);
                if (sy != 0)
                {
                    float d = grab.HandOffset.Length();
                    if (d > 1e-4f) grab.HandOffset *= Math.Clamp(d * MathF.Pow(DistanceRate, sy * dt), 0.05f, 1.5f) / d;
                }
                if (sx != 0) grab.Placement.Size = Math.Clamp(grab.Placement.Size * MathF.Pow(SizeRate, sx * dt), 0.15f, 2.5f);
                var spot = offHand.Position + NVec.Transform(grab.HandOffset, offHand.Rotation);
                var p = grab.Placement;
                SetPlacement(p, p.Anchor, new Pose(spot, HeldRotation(offHand, spot)));
            }
            return grab != null;
        }
        if (!pressed || !Editing || host == null) return false;

        UiWindow best = null;
        float bestDistance = HandReach;
        foreach (var w in windows)
        {
            var p = FreePlacement(w);
            // A window on this hand's own wrist stays put (that grip still moves the table).
            if (p == null || p.Anchor == UiAnchor.OffHand) continue;
            float d = w.DistanceTo(offHand.Position);
            if (d < bestDistance) { best = w; bestDistance = d; }
        }
        if (best == null) return false;

        var placement = FreePlacement(best);
        bool handover = grab?.Window == best;
        Detach(placement);
        var pivot = PlacementPose(placement);
        grab = new GrabState
        {
            Window = best,
            Placement = placement,
            ByHand = true,
            HandOffset = NVec.Transform(pivot.Position - offHand.Position, NQuat.Inverse(offHand.Rotation)),
            Turn = NQuat.Normalize(NQuat.Inverse(offHand.Rotation) * pivot.Rotation),
        };
        VrInput.ClaimStick(true);
        host.Log($"[vrui] off hand {(handover ? "took" : "grabbed")} '{placement.Window}' ({bestDistance:F2} m away)");
        return true;
    }

    // The wrist the held window would go on if let go now: the hand not holding it.
    static UiAnchor? SnapTarget()
    {
        if (grab == null || host == null) return null;
        var anchor = grab.ByHand ? UiAnchor.PointerHand : UiAnchor.OffHand;
        var hand = AnchorPose(host, anchor);
        return grab.Window.DistanceTo(WristPoint(hand)) < SnapReach ? anchor : null;
    }

    // stickX / stickY are ignored now (kept so mods build): the laser hand's own
    // stick moves and resizes the window it holds, the same in every game.
    public static void UpdateGrab(Pose aim, float stickX, float stickY, float deltaTime)
    {
        if (grab == null || grab.ByHand || host == null) return;
        (stickX, stickY) = VrInput.HandStick(false);
        var p = grab.Placement;
        if (MathF.Abs(stickY) > StickDeadZone)
            grab.Distance = Math.Clamp(grab.Distance * MathF.Pow(DistanceRate, stickY * deltaTime), 0.15f, 4f);
        if (MathF.Abs(stickX) > StickDeadZone)
        {
            float size = Math.Clamp(p.Size * MathF.Pow(SizeRate, stickX * deltaTime), 0.15f, 2.5f);
            float ratio = size / p.Size;
            p.Size = size;
            grab.OffX *= ratio;
            grab.OffY *= ratio;
        }

        var spot = aim.Position + NVec.Normalize(aim.Forward) * grab.Distance;
        var rotation = HeldRotation(aim, spot);
        var pivot = spot + NVec.Transform(new NVec(grab.OffX, grab.OffY, 0), rotation);
        SetPlacement(p, p.Anchor, new Pose(pivot, rotation));
    }

    public static void EndGrab()
    {
        if (grab == null) return;
        var p = grab.Placement;
        var wrist = SnapTarget();
        if (wrist != null) SetPlacement(p, wrist.Value, PlacementPose(p));
        grab = null;
        VrInput.UnclaimStick();
        SaveFree();
        LastSaved = null;
        VrUiMenu.MarkDirty();
        host?.Log($"[vrui] placed '{p.Window}' {(wrist != null ? "on the " + wrist + " wrist" : "")} at {p.Position} yaw {p.Yaw:F0} pitch {p.Pitch:F0} roll {p.Roll:F0} size {p.Size:F2}");
    }

    // Pin a window at another of its nine points: the one that stays put when it changes
    // size. It doesn't move now (the placement is re-expressed about the new point), a
    // window being held stays in the hand the same way, and the layout keeps it as pivot=.
    public static bool SetPin(string windowId, UiPin pin)
    {
        var w = windows.FirstOrDefault(x => x.Def.Id == windowId);
        return w != null && SetPin(w, pin);
    }

    static bool SetPin(UiWindow w, UiPin pin)
    {
        var p = FreePlacement(w);
        if (p == null || host == null) return false;
        var was = p.Pin;
        var shown = w.Shown.width >= 1 ? w.Shown : w.Source;
        float width = shown.width / 1000f * p.Size, height = shown.height / 1000f * p.Size;
        var (nx, ny) = pin.Point();
        float dx = (nx - p.PivotX) * width, dy = (ny - p.PivotY) * height;
        var before = PlacementPose(p);
        p.MovePivot(nx, ny, width, height);
        if (grab?.Window == w)
        {
            // The held spot stays where it is in the hand / on the laser.
            if (grab.ByHand)
            {
                var hand = AnchorPose(host, UiAnchor.OffHand);
                var shift = NVec.Transform(new NVec(dx, dy, 0), before.Rotation);
                grab.HandOffset += NVec.Transform(shift, NQuat.Inverse(hand.Rotation));
            }
            else { grab.OffX += dx; grab.OffY += dy; }
        }
        else SaveFree();
        LastSaved = null;
        VrUiMenu.MarkDirty();
        host.Log($"[vrui] '{p.Window}' pinned at its {pin.Name()} (was {was.Name()}): that point stays put when it changes size");
        return true;
    }

    // The point of a window that stays put when it changes size (its layout's pivot, or its UiWindowDef.Pin).
    public static UiPin? PinOf(string windowId)
    {
        var w = windows.FirstOrDefault(x => x.Def.Id == windowId);
        if (w == null) return null;
        var p = ActiveLayout()?.For(windowId);
        if (p == null) return w.Def.Pin;
        p.DefaultPivot(w.Def.Pin);
        return p.Pin;
    }

    // Laser length while a window is held (world units).
    public static float GrabDistanceWorld => grab != null && host != null ? grab.Distance * host.WorldScale : 0;

    // Tracking-space point at a window's UI pixel (for scripted tests).
    public static bool PixelToTracking(string windowId, Vector2 pixel, out NVec point)
    {
        point = default;
        var w = windows.FirstOrDefault(x => x.Def.Id == windowId);
        var p = ActiveLayout()?.For(windowId);
        if (w == null || p == null || host == null || w.Shown.width < 1) return false;
        var s = w.Shown;
        float u = (pixel.x - s.x) / s.width, v = (pixel.y - s.y) / s.height;
        float ww = s.width / 1000f * p.Size, hh = s.height / 1000f * p.Size;
        var local = PlacementPose(p);
        point = local.Compose(new Pose(new NVec((u - p.PivotX) * ww, (v - p.PivotY) * hh, 0), NQuat.Identity)).Position;
        return true;
    }

    // For scripted tests: what a window is attached to, where its centre is
    // (tracking space), whether it is drawn, and which wrist a held window would go on.
    public static UiAnchor? AnchorOf(string windowId) => ActiveLayout()?.For(windowId)?.Anchor;
    public static NVec WindowCentre(string windowId) => windows.FirstOrDefault(x => x.Def.Id == windowId)?.TrackingCentre.Position ?? default;
    public static NQuat WindowRotation(string windowId) => windows.FirstOrDefault(x => x.Def.Id == windowId)?.TrackingCentre.Rotation ?? NQuat.Identity;
    public static bool WindowVisible(string windowId) => windows.FirstOrDefault(x => x.Def.Id == windowId)?.Visible ?? false;
    public static UiAnchor? SnapPreview => SnapTarget();

    // Shown rect of a window (for scripted tests).
    public static Rect WindowRect(string windowId) => windows.FirstOrDefault(x => x.Def.Id == windowId)?.Shown ?? default;

    // Tracking-space point on the menu at canvas pixel (x, y from the top-left), for scripted tests.
    public static NVec MenuPointToTracking(float x, float y) => VrUiMenu.PointToTracking(x, y);

    // For scripted tests: the tracking-space centre of the menu control with this
    // label (a tab, a button's text, a setting row's "Label" for its value or
    // "Label -" / "Label +" for its arrows, "bind:<action id>" for a binding).
    // Null if it isn't on the menu as last drawn.
    public static NVec? MenuPointOf(string label) => VrUiMenu.CentreOf(label, out var x, out var y) ? VrUiMenu.PointToTracking(x, y) : null;
}
