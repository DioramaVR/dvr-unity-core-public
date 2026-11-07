using System;
using System.Collections.Generic;
using System.Linq;

namespace VrUiKit;

// The VR menu's data model (docs/menu-design/README.md): which pages there are,
// what sections and rows each has, which control each row gets, and what a
// click does. No drawing and no UnityEngine.UI: VrUiMenu draws it and calls
// the plain methods here (Activate, Step, Select...) when the laser clicks.
// Build a page fresh whenever the menu redraws (MenuModel.Page); the objects
// read live values, so they can also be kept for a frame or two.

enum MenuPage { Home, View, GameUi, Controls, Capture, Graphics, Help }

// The control a row gets (README "Row kinds").
enum RowKind
{
    Toggle,            // a switch on the right
    ToggleTile,        // one of a section's 2-column toggle tiles (MenuSection.Tiles)
    Number,            // − value + stepper
    ChoiceInline,      // a segmented control on the right (2 short options)
    ChoiceStacked,     // a segmented control on its own line under the label (3+ short options)
    Cycler,            // < value > (many or long options)
    Action,            // a button
    DestructiveAction, // a button that asks "Sure?" first
    Binding,           // a key button plus "+"
    Text,              // read-only text (no control)
}

enum MenuColumn { Left, Right }

// One row. Read its properties each time it's drawn; they're live.
sealed class MenuRow
{
    // Stable id: "<section>/<label>" for settings, "bind:<action id>" for bindings.
    public string Id;
    public RowKind Kind;
    public string Label, Icon, Unit;
    // The help line as written (under the label, always shown). See HelpLine.
    public string Help;
    public bool Rarely, Developer, Destructive;
    // What it is: a setting, or (Kind == Binding) an action.
    public VrSetting Setting;
    public VrAction Action;
    // Actions: a primary (pink) button rather than a ghost one.
    public bool Primary;

    // ---- live state ----

    // Toggles.
    public bool IsOn => Setting is IToggleSetting t && t.IsOn;
    // Choices: the options in order, and the selected one.
    public IReadOnlyList<string> Options => Setting is IChoiceSetting c ? c.Options : Array.Empty<string>();
    public string Selected => Setting?.Value;
    // Numbers: the value with its unit ("0.75 m"); choices: the selected option;
    // actions: the button's text ("Sure?" while armed); toggles: On / Off;
    // bindings: the keys ("L stick ←", "Not set", "Press a button...").
    public string ValueText => MenuText.Value(this);
    public bool CanStepDown => !(Setting is INumberSetting n) || n.Number > n.Min + 1e-5f;
    public bool CanStepUp => !(Setting is INumberSetting n) || n.Number < n.Max - 1e-5f;

    // Destructive rows: the first click arms it ("Sure?"); the second acts.
    public bool Armed => MenuState.IsArmed(Id);
    // Bindings: the other action that uses one of these keys at the same time.
    public VrAction ClashWith => Action != null ? MenuBindings.ClashOf(Action) : null;
    public bool Clash => ClashWith != null;
    public bool Capturing => Action != null && VrInput.Capturing == Action;
    // The line to draw under the label: the confirm prompt, the clash, or the help.
    public string HelpLine =>
        Armed ? "Click again to confirm."
        : Clash ? "Also used by " + MenuWording.Label(ClashWith.Name) + "."
        : Help;

    // ---- what a click does ----

    // The main control: a toggle flips, a choice steps on, an action runs (a
    // destructive one only on the second click), a binding waits for a button.
    public void Activate()
    {
        if (Action != null) { VrInput.BeginCapture(Action, false); MenuState.Disarm(); return; }
        if (Setting == null) return;
        if (Kind == RowKind.DestructiveAction && !Armed) { MenuState.Arm(Id); return; }
        MenuState.Disarm();
        Setting.Activate();
        VrUiMenu.MarkDirty();
    }

    // Stepper − / + (numbers), cycler < / > (choices).
    public void Step(int direction)
    {
        MenuState.Disarm();
        Setting?.Step(direction < 0 ? -1 : 1);
        VrUiMenu.MarkDirty();
    }

    // A segment of a segmented control.
    public void Select(string option)
    {
        MenuState.Disarm();
        if (Setting is IChoiceSetting c) c.Select(option);
        VrUiMenu.MarkDirty();
    }

    // Bindings: the "+" button (add another key rather than replace).
    public void AddBinding()
    {
        MenuState.Disarm();
        if (Action != null) VrInput.BeginCapture(Action, true);
    }

    public override string ToString() => $"{Kind} {Label}";
}

// A card of rows with a header.
sealed class MenuSection
{
    // Stable within its page ("position", "game", "developer", "bind:Table"...).
    public string Id;
    public MenuPage Page;
    public string Title, Icon;
    // The header's right-hand note ("All modes", "This game only", a count).
    public string Note;
    public MenuColumn Column;
    public bool Collapsible;
    public bool DefaultOpen = true;
    // The game's own rows ("This game only") / the collapsed Developer section.
    public bool IsGame, IsDeveloper;
    // Toggles shown as 2-column tiles under the header, then the rows, then the More fold's rows.
    public readonly List<MenuRow> Tiles = new();
    public readonly List<MenuRow> Rows = new();
    public readonly List<MenuRow> More = new();

    public string Key => MenuState.SectionKey(Page, Id);
    public bool Open => !Collapsible || MenuState.IsOpen(Key, DefaultOpen);
    public void ToggleOpen() { if (Collapsible) MenuState.SetOpen(Key, !Open); VrUiMenu.MarkDirty(); }

    public int MoreCount => More.Count;
    public bool MoreOpen => MenuState.IsOpen(MenuState.MoreKey(Page, Id), false);
    // "More settings · N" / "Fewer settings".
    public string MoreText => MoreOpen ? "Fewer settings" : $"More settings · {More.Count}";
    public void ToggleMore() { MenuState.SetOpen(MenuState.MoreKey(Page, Id), !MoreOpen); VrUiMenu.MarkDirty(); }

    // Every row, in drawing order (tiles first), including folded ones.
    public IEnumerable<MenuRow> AllRows => Tiles.Concat(Rows).Concat(More);
    // What to draw now: the rows, and the folded ones while the fold is open.
    public IEnumerable<MenuRow> ShownRows => MoreOpen ? Rows.Concat(More) : Rows;
    public bool IsEmpty => Tiles.Count == 0 && Rows.Count == 0 && More.Count == 0;

    public override string ToString() => $"{Page}/{Id} '{Title}' ({Tiles.Count} tiles, {Rows.Count} rows, {More.Count} more)";
}

// A big card at the top of a page: a view mode, a UI mode, photo / video.
sealed class MenuHero
{
    public string Id, Name, Description, Icon;
    public Func<bool> IsSelected = () => false;
    public Action OnSelect;
    // Capture's cards: the text changes ("Photo saved", "Recording · 0:12").
    public Func<string> LiveName;
    public bool Selected => IsSelected();
    public string Title => LiveName?.Invoke() ?? Name;
    public void Select() { MenuState.Disarm(); OnSelect?.Invoke(); VrUiMenu.MarkDirty(); }
    public override string ToString() => Name;
}

// One page (every page but Home and Help).
sealed class MenuPageModel
{
    public MenuPage Page;
    public string Title, Subtitle;
    // Controls: the mode the bindings are for ("Diorama"); null for no pill.
    public string ModePill;
    public readonly List<MenuHero> Heroes = new();
    public readonly List<MenuSection> Left = new(), Right = new();
    public IEnumerable<MenuSection> Sections => Left.Concat(Right);
    public MenuSection Section(string id) => Sections.FirstOrDefault(s => s.Id == id);
    public IEnumerable<MenuRow> AllRows
    {
        get { foreach (var s in Sections) foreach (var r in s.AllRows) yield return r; }
    }
}

// One line of Home's cheat sheet: "R  Click / pick up  Trigger".
sealed class CheatRow
{
    public char Hand;        // 'R' or 'L' (the side, after handedness)
    public string Action;    // "Turn table"
    public string Keys;      // "Stick ←→"
    public override string ToString() => $"{Hand} {Action} {Keys}";
}

// Home's quick-action buttons.
sealed class QuickAction
{
    public string Id, Label, Icon;
    public bool Primary;
    // False: draw it disabled (e.g. no recording in this game).
    public Func<bool> IsAvailable = () => true;
    public Func<string> LiveLabel;
    public Action OnRun;
    public bool Available => IsAvailable();
    public string Text => LiveLabel?.Invoke() ?? Label;
    public void Run() { if (Available) { OnRun?.Invoke(); VrUiMenu.MarkDirty(); } }
    public override string ToString() => Label;
}

sealed class HomeModel
{
    public string Title, Version;
    // "First time here?" until the player clicks "Got it".
    public bool ShowFirstTime;
    public string FirstTimeTitle = "First time here?", FirstTimeText;
    public void DismissFirstTime() { MenuState.FirstTimeDismissed = true; VrUiMenu.MarkDirty(); }
    // Your view: the mode cards and three rows (Table height; Scale or Radius; Background).
    public readonly List<MenuHero> Modes = new();
    public readonly List<MenuRow> ViewRows = new();
    public readonly List<QuickAction> QuickActions = new();
    // The right column.
    public string ControlsSubtitle;          // "For Diorama · right-handed"
    public readonly List<CheatRow> Cheat = new();
    public int TotalControls;                // "All 22 controls"
    public string AllControlsText => $"All {TotalControls} controls";
    public readonly List<MenuRow> ButtonBars = new(); // tiles, one per hotkey bar
}

sealed class FaqItem
{
    public string Id, Question, Answer;
    public string Heading; // a group heading drawn above this item (the game's name, "Every game"), or null
    public bool DefaultOpen;
    public bool Open => MenuState.IsOpen(MenuState.FaqKey(Id), DefaultOpen);
    public void Toggle() { MenuState.SetOpen(MenuState.FaqKey(Id), !Open); VrUiMenu.MarkDirty(); }
    public override string ToString() => Question;
}

sealed class HelpModel
{
    public string Title = "Help", Subtitle = "Questions players ask";
    public readonly List<FaqItem> Items = new();
}

// One entry of the left rail.
sealed class MenuNavItem
{
    public MenuPage Page;
    public string Label, Icon;
    public bool Highlight; // Help: the lilac one, pinned to the bottom
    public bool Selected => MenuState.Page == Page;
    public void Select() => MenuState.Page = Page;
}
