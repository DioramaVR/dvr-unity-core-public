using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Dvr
{
    /// <summary>
    /// The self-test's findings (VrSelfTest): one line per check, grouped by pass ("menu",
    /// "level"), written as report.json and report.html (pictures beside them) in the run's
    /// folder. Plain C#, so it is unit-tested.
    /// </summary>
    internal sealed class SelfTestReport
    {
        internal sealed class Entry
        {
            public string Pass, Check, Detail, Image;
            public SelfTestChecks.Verdict Verdict;
        }

        public readonly string Game, Started;
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<string> Facts = new List<string>();       // renderers, layers, mode...
        public readonly List<string> Errors = new List<string>();      // log errors and exceptions seen
        public int ErrorCount;
        public string Ended = "";                                       // "finished", "timed out after 300 s", ...

        public SelfTestReport(string game, DateTime started)
        {
            Game = game;
            Started = started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public Entry Add(string pass, string check, SelfTestChecks.Verdict verdict, string detail, string image = null)
        {
            var e = new Entry { Pass = pass, Check = check, Verdict = verdict, Detail = detail, Image = image };
            Entries.Add(e);
            return e;
        }

        public int Count(SelfTestChecks.Verdict v) => Entries.Count(e => e.Verdict == v);

        /// <summary>FAIL if any check failed, else WARN if any warned, else PASS.</summary>
        public string Overall => Count(SelfTestChecks.Verdict.Fail) > 0 ? "FAIL" : Count(SelfTestChecks.Verdict.Warn) > 0 ? "WARN" : "PASS";

        public static string Word(SelfTestChecks.Verdict v) => v switch
        {
            SelfTestChecks.Verdict.Pass => "PASS",
            SelfTestChecks.Verdict.Warn => "WARN",
            SelfTestChecks.Verdict.Fail => "FAIL",
            _ => "INFO",
        };

        /// <summary>Notes a log error or exception (the first 40 kept, all counted).</summary>
        public void Error(string message)
        {
            ErrorCount++;
            if (Errors.Count < 40) Errors.Add(message);
        }

        // ---- JSON ----

        public string Json()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            Field(sb, "game", Game); Field(sb, "started", Started); Field(sb, "ended", Ended); Field(sb, "overall", Overall);
            sb.Append("  \"counts\": {");
            sb.Append($"\"pass\": {Count(SelfTestChecks.Verdict.Pass)}, \"warn\": {Count(SelfTestChecks.Verdict.Warn)}, \"fail\": {Count(SelfTestChecks.Verdict.Fail)}, \"errors\": {ErrorCount}");
            sb.Append("},\n");
            sb.Append("  \"facts\": [").Append(string.Join(", ", Facts.Select(Quote))).Append("],\n");
            sb.Append("  \"checks\": [\n");
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                sb.Append("    {\"pass\": ").Append(Quote(e.Pass)).Append(", \"check\": ").Append(Quote(e.Check))
                  .Append(", \"verdict\": ").Append(Quote(Word(e.Verdict))).Append(", \"detail\": ").Append(Quote(e.Detail ?? ""));
                if (e.Image != null) sb.Append(", \"image\": ").Append(Quote(e.Image));
                sb.Append(i + 1 < Entries.Count ? "},\n" : "}\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"errors\": [").Append(string.Join(", ", Errors.Select(Quote))).Append("]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static void Field(StringBuilder sb, string name, string value) => sb.Append("  ").Append(Quote(name)).Append(": ").Append(Quote(value)).Append(",\n");

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ---- HTML ----

        public string Html()
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(H(Game)).Append(" self-test</title>\n<style>\n");
            sb.Append(":root{--bg:#f7f5f0;--ink:#1b1d26;--muted:#6b6f7d;--line:#ddd8cc;--pass:#2f7d4f;--warn:#a46a00;--fail:#b3261e;--card:#fff}\n");
            sb.Append("@media (prefers-color-scheme: dark){:root{--bg:#181a25;--ink:#f5f0e8;--muted:#a3a6b3;--line:#2e3142;--pass:#5cc58a;--warn:#e8b04a;--fail:#ff7a70;--card:#20232f}}\n");
            sb.Append("body{margin:0;padding:24px 16px;background:var(--bg);color:var(--ink);font:15px/1.45 system-ui,sans-serif}main{max-width:1100px;margin:0 auto}\n");
            sb.Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}.muted{color:var(--muted)}\n");
            sb.Append(".v{font-weight:700;font-size:12px;letter-spacing:.04em;padding:2px 7px;border-radius:4px;border:1px solid currentColor}\n");
            sb.Append(".PASS{color:var(--pass)}.WARN{color:var(--warn)}.FAIL{color:var(--fail)}.INFO{color:var(--muted)}\n");
            sb.Append(".row{display:grid;grid-template-columns:56px 1fr;gap:10px;padding:10px 0;border-top:1px solid var(--line)}\n");
            sb.Append(".row img{display:block;max-width:100%;max-height:360px;margin-top:8px;border:1px solid var(--line);background:repeating-conic-gradient(#8884 0 25%,transparent 0 50%) 0 0/16px 16px}\n");
            sb.Append("code,pre{font:13px ui-monospace,monospace}pre{white-space:pre-wrap;background:var(--card);border:1px solid var(--line);padding:10px;border-radius:6px}\n");
            sb.Append("</style></head><body><main>\n");
            sb.Append("<h1>").Append(H(Game)).Append(" self-test <span class=\"v ").Append(Overall).Append("\">").Append(Overall).Append("</span></h1>\n");
            sb.Append("<p class=\"muted\">Started ").Append(H(Started)).Append(" &middot; ").Append(H(Ended)).Append(" &middot; ")
              .Append(Count(SelfTestChecks.Verdict.Pass)).Append(" pass, ").Append(Count(SelfTestChecks.Verdict.Warn)).Append(" warn, ")
              .Append(Count(SelfTestChecks.Verdict.Fail)).Append(" fail, ").Append(ErrorCount).Append(" log errors</p>\n");
            foreach (var pass in Entries.Select(e => e.Pass).Distinct())
            {
                sb.Append("<h2>").Append(H(pass)).Append("</h2>\n");
                foreach (var e in Entries.Where(e => e.Pass == pass))
                {
                    string w = Word(e.Verdict);
                    sb.Append("<div class=\"row\"><div><span class=\"v ").Append(w).Append("\">").Append(w).Append("</span></div><div><strong>")
                      .Append(H(e.Check)).Append("</strong> <span class=\"muted\">").Append(H(e.Detail)).Append("</span>");
                    if (e.Image != null) sb.Append("<a href=\"").Append(H(e.Image)).Append("\"><img loading=\"lazy\" src=\"").Append(H(e.Image)).Append("\" alt=\"").Append(H(e.Check)).Append("\"></a>");
                    sb.Append("</div></div>\n");
                }
            }
            sb.Append("<h2>Setup</h2>\n<pre>").Append(H(string.Join("\n", Facts))).Append("</pre>\n");
            sb.Append("<h2>Log errors (").Append(ErrorCount).Append(")</h2>\n<pre>").Append(H(Errors.Count > 0 ? string.Join("\n", Errors) : "none")).Append("</pre>\n");
            sb.Append("</main></body></html>\n");
            return sb.ToString();
        }

        static string H(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        public void Write(string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.json"), Json());
            File.WriteAllText(Path.Combine(folder, "report.html"), Html());
        }

        /// <summary>A folder name for the game: letters, digits, '-' and '_' only.</summary>
        public static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            var s = sb.ToString().Trim('_');
            return s.Length > 0 ? s : "game";
        }
    }
}
