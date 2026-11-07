using System;
using System.Collections.Generic;

namespace VrUiKit;

// Rows the menu makes itself, read and written through delegates: Left-handed,
// Swap sticks, the layout and preset cyclers, the layout and preset buttons.
// They're VrSettings like any other, so one MenuRow type draws them all.

sealed class DelegateToggle : VrSetting, IToggleSetting
{
    readonly Func<bool> get;
    readonly Action<bool> set;
    public DelegateToggle(string section, string label, Func<bool> get, Action<bool> set) { Section = section; Label = label; this.get = get; this.set = set; }
    public override string Value => get() ? "On" : "Off";
    public override bool HasArrows => false;
    public override void Step(int direction) => set(!get());
    public override SettingKind Kind => SettingKind.Toggle;
    public bool IsOn => get();
    public void Set(bool on) => set(on);
}

// A cycler with no option list: < value > steps through whatever `step` does.
sealed class DelegateCycler : VrSetting
{
    readonly Func<string> get;
    readonly Action<int> step;
    public DelegateCycler(string section, string label, Func<string> get, Action<int> step) { Section = section; Label = label; this.get = get; this.step = step; Cycler = true; }
    public override string Value => get();
    public override void Step(int direction) => step(direction);
    public override SettingKind Kind => SettingKind.Choice;
}

sealed class DelegateAction : VrSetting
{
    readonly Func<string> text;
    readonly Action run;
    public DelegateAction(string section, string label, string buttonText, Action run) : this(section, label, () => buttonText, run) { }
    public DelegateAction(string section, string label, Func<string> text, Action run) { Section = section; Label = label; this.text = text; this.run = run; }
    public override string Value => text();
    public override bool HasArrows => false;
    public override void Step(int direction) => run();
    public override SettingKind Kind => SettingKind.Action;
}

// A line of text with no control (e.g. "Windows appear once you're in a level.").
sealed class TextLine : VrSetting
{
    public TextLine(string section, string text) { Section = section; Label = text; }
    public override string Value => "";
    public override bool HasArrows => false;
    public override void Step(int direction) { }
    public override void Activate() { }
    public override SettingKind Kind => SettingKind.Action;
}
