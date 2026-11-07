using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using VrUiKit;

namespace Dvr
{
    /// <summary>
    /// A player's setting of the table rig: one field of <see cref="TableTuning"/>
    /// (or <see cref="EyeTuning"/>), its menu row and its range. RIG.md section 2.
    /// </summary>
    internal sealed class RigSettingDef
    {
        public string Field, Label, Help;
        /// <summary>Which modes have it (null: every mode). Ignored for shared settings.</summary>
        public RigMode[] Modes;
        public float Min, Max, Step;
        public string Format = "0.##";
        /// <summary>A menu row is shown only while this holds for the mode's current values (null: always).</summary>
        public Func<RigSettings, RigMode, bool> When;
        /// <summary>No menu row (config file only).</summary>
        public bool Hidden;

        public bool In(RigMode m) => Modes == null || Array.IndexOf(Modes, m) >= 0;
    }

    /// <summary>The eye cameras' settings, shared by every mode ([Dvr.Graphics], on the Graphics tab).</summary>
    internal sealed class EyeTuning
    {
        public bool DesktopView;
        public float EyeResolution = 1f;
        public bool EyeShadows = true;
        // The game camera's post-processing (colour grading, tonemapping, bloom, post AA) on
        // the eyes. It also runs over our menu and panels (TPM: a darker menu, flashing
        // roofs), so a game can default it off.
        public bool EyePostProcessing = true;
        public EyeTuning Clone() => (EyeTuning)MemberwiseClone();
    }

    /// <summary>An old config key a setting's value moves over from, the first time (see <see cref="RigSettings"/>).</summary>
    internal sealed class OldKey
    {
        public readonly string Field, Section, Key;
        public readonly object OldDefault;
        /// <param name="field">The new setting (a TableTuning / EyeTuning field name, e.g. "Scale").</param>
        /// <param name="oldDefault">The old key's default, of the old key's type (float, bool, int, string or an enum).</param>
        public OldKey(string field, string section, string key, object oldDefault)
        {
            Field = field; Section = section; Key = key; OldDefault = oldDefault;
        }

        /// <summary>Set by <see cref="Value"/>: the old value itself, read by the mod from wherever it was (timberborn's own config.ini).</summary>
        public bool HasValue { get; private set; }
        public object OldValue { get; private set; }

        /// <summary>
        /// An old value the mod read itself (not from the BepInEx config): it moves
        /// over once, on the same rule, if it differs from <paramref name="oldDefault"/>.
        /// </summary>
        public static OldKey Value(string field, object value, object oldDefault) =>
            new OldKey(field, "(mod)", field, oldDefault) { HasValue = true, OldValue = value };
    }

    /// <summary>
    /// The table rig's settings in the game's config file (RIG.md section 2):
    /// <c>[Dvr.Table]</c> (shared by every mode), <c>[Dvr.Diorama]</c>,
    /// <c>[Dvr.Turntable]</c>, <c>[Dvr.Orbit]</c> and <c>[Dvr.Graphics]</c>.
    ///
    /// A value comes from three layers, the last one present winning: the core's
    /// default (TableTuning's initial values), the game's default for that mode
    /// (what it passes to Offer), and what the player set (the file).
    /// "Reset to game defaults" drops the player's layer.
    ///
    /// Old keys move over once: the first time a section is bound, each old key
    /// given for it is read, and its value is taken only if it differs from that
    /// key's old default. BepInEx writes defaults into the file, so a value equal
    /// to the old default was most likely never chosen, and the player gets the
    /// game's new default instead (the lower table height, for one). A
    /// <c>FromOldKeys</c> flag in the section records that it's done.
    /// </summary>
    internal sealed class RigSettings
    {
        private static readonly RigMode[] PivotModes = { RigMode.Diorama, RigMode.Turntable };
        private static readonly RigMode[] TurnModes = { RigMode.Turntable };
        private static readonly RigMode[] OrbitOnly = { RigMode.Orbit };
        private static bool Smooth(RigSettings s, RigMode m) => s.Get<TurnStyle>(m, "Turn") == TurnStyle.Smooth;

        /// <summary>The shared settings ([Dvr.Table]).</summary>
        public static readonly RigSettingDef[] Shared =
        {
            new RigSettingDef { Field = "TableHeight", Label = "Table height (m)", Min = TableTuning.MinTableHeight, Max = TableTuning.MaxTableHeight, Step = 0.05f, Format = "0.00",
                Help = "Height of the table surface (the ground, or the character's feet) above your floor, metres. Lifting the table with one grip changes it too." },
            new RigSettingDef { Field = "TableDistance", Label = "Table distance (m)", Min = TableTuning.MinTableDistance, Max = TableTuning.MaxTableDistance, Step = 0.05f, Format = "0.00",
                Help = "How far in front of your standing spot the table centre is, metres." },
            new RigSettingDef { Field = "FollowGameCamera", Label = "Follow the game camera", Modes = PivotModes,
                Help = "Keep the table centred on the game camera's pivot, so the game's own panning moves the table." },
        };

        /// <summary>Each mode's settings ([Dvr.&lt;Mode&gt;]), in menu order.</summary>
        public static readonly RigSettingDef[] PerMode =
        {
            new RigSettingDef { Field = "Scale", Label = "Scale", Min = 1, Max = 5000, Step = 5, Format = "0",
                Help = "World units per real metre: bigger shows more of the world, smaller. Zooming and pinching change it." },
            new RigSettingDef { Field = "Turn", Label = "Turn",
                Help = "Smooth: the stick sets how fast the table spins, and it coasts when let go. Snap: one step a flick." },
            new RigSettingDef { Field = "TurnSpeed", Label = "Turn speed (deg/s)", Min = 30, Max = 360, Step = 10, Format = "0", When = Smooth,
                Help = "Smooth turning: the top spin speed at full stick, degrees a second." },
            new RigSettingDef { Field = "SnapAngle", Label = "Snap angle (deg)", Min = 15, Max = 90, Step = 15, Format = "0", When = (s, m) => !Smooth(s, m),
                Help = "Snap turning: degrees a flick." },
            new RigSettingDef { Field = "TurnRamp", Label = "Turn ramp (s)", Min = 0, Max = 1, Step = 0.05f, Format = "0.00", When = Smooth,
                Help = "Smooth turning: seconds to reach full speed." },
            new RigSettingDef { Field = "SpinCoast", Label = "Spin coast (s)", Min = 0, Max = 3, Step = 0.1f, Format = "0.0",
                Help = "How long a spin keeps going once let go: the time to lose about two thirds of its speed. 0 stops at once." },
            new RigSettingDef { Field = "ZoomSpeed", Label = "Zoom speed", Min = 0.3f, Max = 3, Step = 0.1f, Format = "0.0",
                Help = "Zoom rate at full stick." },
            new RigSettingDef { Field = "PanSpeed", Label = "Pan speed (m/s)", Min = 0.1f, Max = 2, Step = 0.1f, Format = "0.0", Modes = new[] { RigMode.Diorama },
                Help = "How fast the pan stick moves the world across the table, real metres a second (where the core pans; some games pan with their own keys)." },
            new RigSettingDef { Field = "TwistGain", Label = "Twist gain", Min = 0.5f, Max = 3, Step = 0.25f, Format = "0.00",
                Help = "Degrees the table turns per degree the two grips twist." },
            new RigSettingDef { Field = "DeadZone", Label = "Stick dead zone", Min = 0.1f, Max = 0.6f, Step = 0.05f, Format = "0.00",
                Help = "Stick travel ignored." },
            // Turntable.
            new RigSettingDef { Field = "Radius", Label = "Radius (m)", Min = 0.2f, Max = 1.5f, Step = 0.05f, Format = "0.00", Modes = TurnModes,
                Help = "Radius of the circle on the table, real metres." },
            new RigSettingDef { Field = "Edge", Label = "Edge", Modes = new[] { RigMode.Turntable, RigMode.Orbit },
                When = (s, m) => m != RigMode.Orbit || s.Get<bool>(m, "Cut"),
                Help = "Sharp, or Soft (fades out over the outer 15%). Needs the game's mask shader; without it the edge is rough." },
            new RigSettingDef { Field = "Background", Label = "Background", Modes = PivotModes,
                Help = "What is behind the world: Light, Dark, Passthrough (KeyColour, for a chroma key), or Game (the game camera's own sky). Outside Turntable's circle, Game shows as Light." },
            new RigSettingDef { Field = "KeyColour", Label = "Key colour", Modes = PivotModes, Hidden = true,
                Help = "Passthrough background colour as hex RRGGBB, to match the chroma key in Virtual Desktop or ALVR." },
            new RigSettingDef { Field = "Fling", Label = "Fling", Modes = TurnModes,
                Help = "Letting go of the rim while it turns flings the turntable." },
            // Orbit.
            new RigSettingDef { Field = "OrbitTurn", Label = "Orbit turn", Min = 0.2f, Max = 8, Step = 0.25f, Format = "0.00", Modes = OrbitOnly,
                Help = "How strongly a sideways grip circles round the character." },
            new RigSettingDef { Field = "OrbitHeight", Label = "Orbit height", Min = 0, Max = 1.5f, Step = 0.05f, Format = "0.00", Modes = OrbitOnly,
                Help = "Grip up / down: the share of the hand's movement that raises or lowers the table." },
            new RigSettingDef { Field = "OrbitDistance", Label = "Orbit distance", Min = 0, Max = 1.5f, Step = 0.05f, Format = "0.00", Modes = OrbitOnly,
                Help = "Grip forward / back: the share of the hand's movement that moves the table nearer or further." },
            new RigSettingDef { Field = "OrbitSmoothing", Label = "Orbit smoothing", Min = 4, Max = 30, Step = 1, Format = "0", Modes = OrbitOnly,
                Help = "How quickly the turn catches up with the hand (1/s)." },
            new RigSettingDef { Field = "OrbitCoast", Label = "Orbit coast", Min = 0.3f, Max = 10, Step = 0.1f, Format = "0.0", Modes = OrbitOnly,
                Help = "How quickly a thrown orbit slows (1/s; higher stops sooner)." },
            new RigSettingDef { Field = "FollowSpeed", Label = "Follow speed", Min = 0.5f, Max = 10, Step = 0.5f, Format = "0.0", Modes = OrbitOnly,
                Help = "How quickly the table re-centres on the character (1/s)." },
            new RigSettingDef { Field = "OneGrip", Label = "One grip", Modes = OrbitOnly,
                Help = "Orbit: circle round the character. Drag: move the table; it eases back to the character." },
            new RigSettingDef { Field = "Cut", Label = "Cut around the character", Modes = OrbitOnly,
                Help = "Hide everything outside a circle round the character (the headset only)." },
            new RigSettingDef { Field = "CutRadius", Label = "Cut radius (m)", Min = 0.2f, Max = 1.5f, Step = 0.05f, Format = "0.00", Modes = OrbitOnly,
                When = (s, m) => s.Get<bool>(m, "Cut"), Help = "Radius of the cut, real metres." },
        };

        /// <summary>The eye settings ([Dvr.Graphics], on the Graphics tab).</summary>
        public static readonly RigSettingDef[] Graphics =
        {
            new RigSettingDef { Field = "DesktopView", Label = "Monitor shows the game",
                Help = "Keep drawing the game's own view on the monitor while VR shows the world. Costs frame rate." },
            new RigSettingDef { Field = "EyeResolution", Label = "Resolution scale", Min = 0.3f, Max = 1.5f, Step = 0.05f, Format = "0.00",
                Help = "Eye texture size as a share of SteamVR's recommended resolution. Lower = smoother frame rate." },
            new RigSettingDef { Field = "EyeShadows", Label = "Shadows", When = (s, m) => Works("EyeShadows"),
                Help = "Shadows in the headset. Off saves a lot of frame time." },
            new RigSettingDef { Field = "EyePostProcessing", Label = "Post-processing", When = (s, m) => Works("EyePostProcessing"),
                Help = "The game's colour grading, bloom and post anti-aliasing in the headset. It also tints the VR menu and panels." },
        };

        // The core switches shadows and post-processing per eye on URP only; the built-in
        // pipeline has no such switch (VrPipeline.SetShadows / SetPostProcessing do nothing).
        // There the row shows only if the mod applies the setting itself (HandledByMod).
        private static readonly HashSet<string> handledByMod = new HashSet<string>();
        private static bool Works(string field) => VrPipeline.Name == "URP" || handledByMod.Contains(field);

        /// <summary>
        /// The mod applies this EyeTuning setting itself (e.g. "EyePostProcessing" for a
        /// built-in game's PPv2 layer on the eyes), so its Graphics row is worth showing.
        /// On URP the core applies Shadows and Post-processing and the rows always show.
        /// </summary>
        public static void HandledByMod(string field) => handledByMod.Add(field);

        public const string TableSection = "Dvr.Table", GraphicsSection = "Dvr.Graphics";
        public static string SectionOf(RigMode m) => "Dvr." + m;

        private readonly ConfigFile config;
        private readonly Dictionary<string, ConfigEntryBase> entries = new Dictionary<string, ConfigEntryBase>();
        private readonly Dictionary<RigMode, TableTuning> gameDefaults = new Dictionary<RigMode, TableTuning>();
        private TableTuning tableDefaults = new TableTuning();
        private EyeTuning eyeDefaults = new EyeTuning();
        private readonly List<string> log = new List<string>();

        /// <summary>What migration did (for the log and tests).</summary>
        public IReadOnlyList<string> Log => log;

        public RigSettings(ConfigFile config) { this.config = config; }

        public bool Has(RigMode m) => gameDefaults.ContainsKey(m);
        public IEnumerable<RigMode> Modes => gameDefaults.Keys;
        public TableTuning GameDefaults(RigMode m) => gameDefaults[m];

        /// <summary>The shared table settings and the eye settings, with the game's defaults and old keys. Call once, before the first mode.</summary>
        public void BindShared(TableTuning table, EyeTuning eyes, IEnumerable<OldKey> old)
        {
            tableDefaults = table ?? new TableTuning();
            eyeDefaults = eyes ?? new EyeTuning();
            var oldList = old?.ToList() ?? new List<OldKey>();
            BindSection(TableSection, Shared, tableDefaults, oldList.Where(o => Shared.Any(d => d.Field == o.Field)));
            BindSection(GraphicsSection, Graphics, eyeDefaults, oldList.Where(o => Graphics.Any(d => d.Field == o.Field)));
        }

        /// <summary>One mode's settings, with the game's defaults for it and its old keys.</summary>
        public void BindMode(RigMode mode, TableTuning defaults, IEnumerable<OldKey> old)
        {
            if (!entries.Keys.Any(k => k.StartsWith(TableSection + "/"))) BindShared(null, null, null);
            gameDefaults[mode] = defaults;
            BindSection(SectionOf(mode), PerMode.Where(d => d.In(mode)).ToArray(), defaults, old);
        }

        private void BindSection(string section, RigSettingDef[] defs, object defaults, IEnumerable<OldKey> old)
        {
            foreach (var d in defs)
            {
                var f = FieldOf(defaults.GetType(), d.Field);
                var value = f.GetValue(defaults);
                string help = d.Help ?? d.Label;
                ConfigEntryBase e;
                if (f.FieldType == typeof(float)) e = config.Bind(section, d.Field, (float)value, help);
                else if (f.FieldType == typeof(bool)) e = config.Bind(section, d.Field, (bool)value, help);
                else if (f.FieldType == typeof(string)) e = config.Bind(section, d.Field, (string)value, help);
                else if (f.FieldType.IsEnum) e = config.Bind(section, d.Field, value.ToString(), help + " (" + string.Join(", ", Enum.GetNames(f.FieldType)) + ")");
                else throw new InvalidOperationException($"RigSettings: {d.Field} has an unsupported type {f.FieldType.Name}");
                entries[section + "/" + d.Field] = e;
            }
            Migrate(section, defs, defaults.GetType(), old);
        }

        private void Migrate(string section, RigSettingDef[] defs, Type owner, IEnumerable<OldKey> old)
        {
            var list = old?.Where(o => defs.Any(d => d.Field == o.Field)).ToList();
            if (list == null || list.Count == 0) return;
            var done = config.Bind(section, "FromOldKeys", false, "Set by the mod once the old settings have been moved over.");
            if (done.Value) return;
            int first = log.Count;   // print only this section's lines (the list keeps every section's)
            foreach (var o in list)
            {
                object v;
                if (o.HasValue) v = o.OldValue;
                else
                {
                    var oldEntry = BindOld(o);
                    if (oldEntry == null) continue;
                    v = oldEntry.BoxedValue;
                }
                if (v == null) continue;
                if (SameValue(v, o.OldDefault))
                {
                    log.Add($"{o.Section}.{o.Key} = {Text(v)}: the old default, not moved (the new default stays)");
                    continue;
                }
                var fieldType = FieldOf(owner, o.Field).FieldType;
                if (!TryConvert(v, fieldType, out var converted))
                {
                    log.Add($"{o.Section}.{o.Key} = {Text(v)}: can't be read as {fieldType.Name}, not moved");
                    continue;
                }
                entries[section + "/" + o.Field].BoxedValue = fieldType.IsEnum ? converted.ToString() : converted;
                log.Add($"{o.Section}.{o.Key} = {Text(v)} -> [{section}] {o.Field}");
            }
            done.Value = true;
            for (int i = first; i < log.Count; i++) VrCore.Info("[rig] settings: " + log[i]);
        }

        /// <summary>
        /// The mode a game kept in its own old setting (tpm's [Diorama] View), as a
        /// mode id to select, once: the player's choice is mapped whether or not it
        /// was the old default. Null when there's nothing to move (done, missing,
        /// or a value the map doesn't name). oldDefault has the old key's type.
        /// </summary>
        public string MigrateMode(string section, string key, object oldDefault, IEnumerable<(string from, RigMode to)> map)
        {
            var done = config.Bind(TableSection, "ModeFromOldKey", false, "Set by the mod once the old view-mode setting has been moved over.");
            if (done.Value) return null;
            done.Value = true;
            var e = BindOld(new OldKey("mode", section, key, oldDefault));
            if (e == null) return null;
            string v = e.BoxedValue?.ToString();
            foreach (var (from, to) in map)
                if (string.Equals(from, v, StringComparison.OrdinalIgnoreCase))
                {
                    log.Add($"{section}.{key} = {v} -> mode {to}");
                    VrCore.Info($"[rig] settings: {section}.{key} = {v} -> mode {to}");
                    return to.ToString();
                }
            log.Add($"{section}.{key} = {v}: no mode for it, not moved");
            return null;
        }

        // ConfigFile.Bind<T> for the old key's own type (so a mod that still binds it gets the same entry).
        private ConfigEntryBase BindOld(OldKey o)
        {
            var type = o.OldDefault?.GetType();
            if (type == null) return null;
            var bind = typeof(ConfigFile).GetMethods().FirstOrDefault(m =>
            {
                if (m.Name != "Bind" || !m.IsGenericMethodDefinition) return false;
                var p = m.GetParameters();
                return p.Length == 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string) && p[3].ParameterType == typeof(string);
            });
            if (bind == null) return null;
            try { return (ConfigEntryBase)bind.MakeGenericMethod(type).Invoke(config, new[] { o.Section, o.Key, o.OldDefault, "" }); }
            catch (Exception e)
            {
                log.Add($"{o.Section}.{o.Key}: {(e.InnerException ?? e).Message}");
                return null;
            }
        }

        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (IsNumber(a) && IsNumber(b)) return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-4;
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumber(object o) => o is float || o is double || o is int || o is long;

        private static bool TryConvert(object v, Type to, out object result)
        {
            result = null;
            try
            {
                if (to == typeof(float)) { if (!IsNumber(v)) return false; result = Convert.ToSingle(v, CultureInfo.InvariantCulture); return true; }
                if (to == typeof(bool)) { if (!(v is bool)) return false; result = v; return true; }
                if (to == typeof(string)) { result = v.ToString(); return true; }
                if (to.IsEnum)
                {
                    string name = v.ToString();
                    var match = Enum.GetNames(to).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    if (match == null) return false;
                    result = Enum.Parse(to, match);
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static string Text(object v) => v is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : v?.ToString() ?? "null";

        private static FieldInfo FieldOf(Type t, string name) =>
            t.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException($"RigSettings: no setting {name} in {t.Name}");

        // ---- reading and writing ----

        private ConfigEntryBase Entry(RigMode? mode, string field)
        {
            if (mode != null && entries.TryGetValue(SectionOf(mode.Value) + "/" + field, out var e)) return e;
            if (entries.TryGetValue(TableSection + "/" + field, out e)) return e;
            if (entries.TryGetValue(GraphicsSection + "/" + field, out e)) return e;
            return null;
        }

        /// <summary>A setting's current value (the player's, else the game's default).</summary>
        public T Get<T>(RigMode mode, string field)
        {
            var e = Entry(mode, field);
            if (e == null) return (T)FieldOf(typeof(TableTuning), field).GetValue(gameDefaults.TryGetValue(mode, out var d) ? d : tableDefaults);
            object v = e.BoxedValue;
            if (typeof(T).IsEnum) return TryConvert(v, typeof(T), out var r) ? (T)r : (T)FieldOf(typeof(TableTuning), field).GetValue(gameDefaults[mode]);
            return (T)v;
        }

        public void Set(RigMode? mode, string field, object value)
        {
            var e = Entry(mode, field) ?? throw new InvalidOperationException($"RigSettings: {field} isn't bound");
            e.BoxedValue = value is Enum ? value.ToString() : value;
        }

        /// <summary>Everything for this mode: the game's defaults (and its game-only limits), then the player's values over them.</summary>
        public void Fill(RigMode mode, TableTuning into)
        {
            var src = gameDefaults.TryGetValue(mode, out var d) ? d : tableDefaults;
            foreach (var f in typeof(TableTuning).GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(into, f.GetValue(src));
            foreach (var def in Shared.Concat(PerMode))
            {
                var e = Entry(mode, def.Field);
                if (e == null) continue;
                var f = FieldOf(typeof(TableTuning), def.Field);
                if (f.FieldType.IsEnum) { if (TryConvert(e.BoxedValue, f.FieldType, out var r)) f.SetValue(into, r); }
                else f.SetValue(into, e.BoxedValue);
            }
        }

        public EyeTuning Eyes()
        {
            var t = eyeDefaults.Clone();
            foreach (var def in Graphics)
            {
                var e = Entry(null, def.Field);
                if (e != null) FieldOf(typeof(EyeTuning), def.Field).SetValue(t, e.BoxedValue);
            }
            return t;
        }

        /// <summary>The player's values for this mode go; the game's defaults come back. Shared settings stay.</summary>
        public void ResetToGameDefaults(RigMode mode)
        {
            foreach (var def in PerMode.Where(d => d.In(mode)))
            {
                var e = Entry(mode, def.Field);
                if (e != null) e.BoxedValue = e.DefaultValue;
            }
        }

        // ---- the Mode tab ----

        /// <summary>
        /// The rows under a mode on the Mode tab: the shared table rows, then the
        /// mode's own, then "Reset to game defaults". maskShader says whether the
        /// cut can have a sharp or soft edge (Turntable's Edge row).
        /// </summary>
        public List<VrSetting> Rows(RigMode mode, Func<bool> maskShader)
        {
            var rows = new List<VrSetting>();
            foreach (var def in Shared.Where(d => d.In(mode))) Add(rows, "Table", mode, def, typeof(TableTuning));
            foreach (var def in PerMode.Where(d => d.In(mode) && !d.Hidden))
            {
                if (def.Field == "Edge" && maskShader != null)
                {
                    var e = (ConfigEntry<string>)Entry(mode, "Edge");
                    rows.Add(new ChoiceSetting(mode.ToString(), def.Label,
                        () => maskShader() ? Enum.GetNames(typeof(EdgeStyle)) : new[] { RoughEdge },
                        () => maskShader() ? e.Value : RoughEdge,
                        v => { if (v != RoughEdge) e.Value = v; }));
                    if (def.When != null) { var when = def.When; rows[rows.Count - 1].When = () => when(this, mode); }
                    continue;
                }
                Add(rows, mode.ToString(), mode, def, typeof(TableTuning));
            }
            rows.Add(new ActionSetting(mode.ToString(), "Reset to game defaults", () => "Reset", () => ResetToGameDefaults(mode)));
            return rows;
        }

        public const string RoughEdge = "Rough (no mask shader yet)";

        /// <summary>The Graphics tab rows for the eyes.</summary>
        public List<VrSetting> GraphicsRows()
        {
            var rows = new List<VrSetting>();
            foreach (var def in Graphics) Add(rows, "Graphics", RigMode.Diorama, def, typeof(EyeTuning));
            return rows;
        }

        private void Add(List<VrSetting> rows, string section, RigMode mode, RigSettingDef def, Type owner)
        {
            var e = Entry(mode, def.Field);
            if (e == null) return;
            var type = FieldOf(owner, def.Field).FieldType;
            VrSetting row;
            if (type == typeof(float)) row = new NumberSetting(section, def.Label, (ConfigEntry<float>)e, def.Min, def.Max, def.Step, def.Format);
            else if (type == typeof(bool)) row = new ToggleSetting(section, def.Label, (ConfigEntry<bool>)e);
            else if (type.IsEnum)
            {
                var s = (ConfigEntry<string>)e;
                var names = Enum.GetNames(type);
                row = new ChoiceSetting(section, def.Label, () => names, () => s.Value, v => s.Value = v);
            }
            else return;
            if (def.When != null) { var when = def.When; row.When = () => when(this, mode); }
            rows.Add(row);
        }
    }
}
