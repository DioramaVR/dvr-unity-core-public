#if DVR_NO_BEPINEX
// For mods without BepInEx (timberborn-vr: the game's own mod loader). The
// core logs through BepInEx.Logging and keeps its settings in
// BepInEx.Configuration; shipping BepInEx.dll there breaks the loader, which
// type-checks every DLL in the mod folder and trips over HarmonyX-only types.
// So under DVR_NO_BEPINEX the core brings the few pieces it uses, with the same
// names, internal to the mod. Files are written in BepInEx's .cfg format, so
// they read the same and a later move to BepInEx keeps them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BepInEx.Logging
{
    internal enum LogLevel { None = 0, Fatal = 1, Error = 2, Warning = 4, Message = 8, Info = 16, Debug = 32 }

    internal class ManualLogSource
    {
        /// <summary>Where every source's lines go. Unity's log by default; the mod can point it elsewhere.</summary>
        public static Action<LogLevel, string> Sink = (level, line) =>
        {
            if (level <= LogLevel.Error) UnityEngine.Debug.LogError(line);
            else if (level == LogLevel.Warning) UnityEngine.Debug.LogWarning(line);
            else UnityEngine.Debug.Log(line);
        };

        public string SourceName { get; }
        public ManualLogSource(string sourceName) { SourceName = sourceName; }

        public void Log(LogLevel level, object data) => Sink?.Invoke(level, $"[{level,-7}:{SourceName}] {data}");
        public void LogFatal(object data) => Log(LogLevel.Fatal, data);
        public void LogError(object data) => Log(LogLevel.Error, data);
        public void LogWarning(object data) => Log(LogLevel.Warning, data);
        public void LogMessage(object data) => Log(LogLevel.Message, data);
        public void LogInfo(object data) => Log(LogLevel.Info, data);
        public void LogDebug(object data) => Log(LogLevel.Debug, data);
    }
}

namespace BepInEx.Configuration
{
    internal sealed class ConfigDefinition
    {
        public string Section { get; }
        public string Key { get; }
        public ConfigDefinition(string section, string key) { Section = section; Key = key; }
        public override string ToString() => Section + "." + Key;
    }

    internal sealed class ConfigDescription
    {
        public string Description { get; }
        public ConfigDescription(string description) { Description = description ?? ""; }
    }

    internal abstract class ConfigEntryBase
    {
        public ConfigFile ConfigFile { get; }
        public ConfigDefinition Definition { get; }
        public ConfigDescription Description { get; }
        public abstract Type SettingType { get; }
        public abstract object BoxedValue { get; set; }
        public abstract object DefaultValue { get; }

        protected ConfigEntryBase(ConfigFile file, ConfigDefinition definition, ConfigDescription description)
        {
            ConfigFile = file;
            Definition = definition;
            Description = description;
        }

        public string GetSerializedValue() => ConfigFile.Serialize(BoxedValue, SettingType);
        public void SetSerializedValue(string text)
        {
            if (ConfigFile.TryParse(text, SettingType, out var v)) BoxedValue = v;
        }
    }

    internal sealed class ConfigEntry<T> : ConfigEntryBase
    {
        private T value;
        private readonly T defaultValue;
        public event EventHandler SettingChanged;

        internal ConfigEntry(ConfigFile file, ConfigDefinition definition, T defaultValue, ConfigDescription description)
            : base(file, definition, description)
        {
            this.defaultValue = defaultValue;
            value = defaultValue;
        }

        public T Value
        {
            get => value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(this.value, value)) return;
                this.value = value;
                SettingChanged?.Invoke(this, EventArgs.Empty);
                ConfigFile.OnSettingChanged();
            }
        }

        public override Type SettingType => typeof(T);
        public override object BoxedValue { get => value; set => Value = (T)value; }
        public override object DefaultValue => defaultValue;
    }

    /// <summary>A .cfg file in BepInEx's format: [Section], "## description" lines, "Key = value".</summary>
    internal sealed class ConfigFile
    {
        public string ConfigFilePath { get; }
        public bool SaveOnConfigSet { get; set; } = true;
        private readonly Dictionary<string, ConfigEntryBase> entries = new Dictionary<string, ConfigEntryBase>();
        private readonly Dictionary<string, string> orphans = new Dictionary<string, string>();
        private readonly List<string> order = new List<string>();

        public ConfigFile(string configPath, bool saveOnInit)
        {
            ConfigFilePath = Path.GetFullPath(configPath);
            Reload();
            if (saveOnInit) Save();
        }

        private static string Id(string section, string key) => section + "\u0001" + key;

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) =>
            Bind(new ConfigDefinition(section, key), defaultValue, new ConfigDescription(description));

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description = null) =>
            Bind(new ConfigDefinition(section, key), defaultValue, description);

        public ConfigEntry<T> Bind<T>(ConfigDefinition definition, T defaultValue, ConfigDescription description = null)
        {
            string id = Id(definition.Section, definition.Key);
            if (entries.TryGetValue(id, out var existing)) return (ConfigEntry<T>)existing;
            var entry = new ConfigEntry<T>(this, definition, defaultValue, description ?? new ConfigDescription(""));
            if (orphans.TryGetValue(id, out var text))
            {
                if (TryParse(text, typeof(T), out var v)) entry.BoxedValue = v;
                orphans.Remove(id);
            }
            entries[id] = entry;
            order.Add(id);
            if (SaveOnConfigSet) Save();
            return entry;
        }

        internal void OnSettingChanged()
        {
            if (SaveOnConfigSet) Save();
        }

        public void Reload()
        {
            if (!File.Exists(ConfigFilePath)) return;
            string section = "";
            foreach (var raw in File.ReadAllLines(ConfigFilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), text = line.Substring(eq + 1).Trim();
                string id = Id(section, key);
                if (entries.TryGetValue(id, out var e)) e.SetSerializedValue(text);
                else orphans[id] = text;
            }
        }

        public void Save()
        {
            var sb = new StringBuilder();
            var bySection = new SortedDictionary<string, List<ConfigEntryBase>>(StringComparer.Ordinal);
            foreach (var id in order)
            {
                var e = entries[id];
                if (!bySection.TryGetValue(e.Definition.Section, out var list)) bySection[e.Definition.Section] = list = new List<ConfigEntryBase>();
                list.Add(e);
            }
            foreach (var kv in bySection)
            {
                sb.Append('[').Append(kv.Key).AppendLine("]").AppendLine();
                foreach (var e in kv.Value)
                {
                    if (e.Description.Description.Length > 0)
                        foreach (var d in e.Description.Description.Split('\n')) sb.Append("## ").AppendLine(d.TrimEnd('\r'));
                    sb.Append("# Setting type: ").AppendLine(e.SettingType.Name);
                    sb.Append("# Default value: ").AppendLine(Serialize(e.DefaultValue, e.SettingType)).AppendLine();
                    sb.Append(e.Definition.Key).Append(" = ").AppendLine(e.GetSerializedValue()).AppendLine();
                }
            }
            // Keys nobody has bound yet survive a save.
            foreach (var kv in orphans)
            {
                var parts = kv.Key.Split('\u0001');
                sb.Append('[').Append(parts[0]).AppendLine("]").Append(parts[1]).Append(" = ").AppendLine(kv.Value).AppendLine();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
            File.WriteAllText(ConfigFilePath, sb.ToString());
        }

        internal static string Serialize(object value, Type type)
        {
            if (value == null) return "";
            if (type == typeof(float)) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(double)) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return (bool)value ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static bool TryParse(string text, Type type, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(string)) value = text;
                else if (type.IsEnum) value = Enum.Parse(type, text, true);
                else if (type == typeof(bool)) value = bool.Parse(text);
                else value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
#endif
