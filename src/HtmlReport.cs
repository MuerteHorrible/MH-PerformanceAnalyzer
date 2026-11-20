using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Builds the offline HTML reports: report.html inside a session folder (that session only) and
    /// Output\index.html (every kept session, newest first, with the compare view). The page template is built
    /// into the DLL; each session's files are read from Output (through OutputStore) and stored inside the page as
    /// data, so the page itself never reads anything. Same files in, same page out.
    /// </summary>
    internal static class HtmlReport
    {
        public const string IndexFile = "index.html";
        public const string SessionFile = "report.html";
        private const string Placeholder = "/*ANALYZER_DATA*/null";
        private const int MaxSessionsInIndex = 30;

        private static string? template;

        private static string Template()
        {
            if (template == null)
            {
                using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MHPerformanceAnalyzer.report-template.html");
                if (s == null)
                {
                    throw new InvalidOperationException("Report template missing from the DLL.");
                }
                using var reader = new StreamReader(s, Encoding.UTF8);
                template = reader.ReadToEnd();
            }
            return template;
        }

        public static void WriteSessionReport(OutputStore store, string folder)
        {
            string? data = SessionJson(store, folder);
            if (data != null)
            {
                store.WriteText(folder + "\\" + SessionFile, Page("[" + data + "]"));
            }
        }

        public static void WriteIndex(OutputStore store)
        {
            var folders = SessionFolders.All(store);
            folders.Sort(StringComparer.Ordinal);
            folders.Reverse(); // Newest first.
            var parts = new List<string>();
            foreach (string folder in folders)
            {
                if (parts.Count >= MaxSessionsInIndex)
                {
                    break;
                }
                string? data = SessionJson(store, folder);
                if (data != null)
                {
                    parts.Add(data);
                }
            }
            store.WriteText(IndexFile, Page("[" + string.Join(",", parts) + "]"));
        }

        private static string Page(string sessionsArray)
        {
            // "</" is written as "<\/" so no value can close the script element early.
            string data = ("{\"sessions\":" + sessionsArray + "}").Replace("</", "<\\/");
            return Template().Replace(Placeholder, data);
        }

        /// <summary>
        /// One session as a JSON object, or null when it has no per-second data. A session without report.json
        /// (recorded by an earlier version, or interrupted) is still included, so it can be charted and compared.
        /// </summary>
        private static string? SessionJson(OutputStore store, string folder)
        {
            string? frames = store.ReadText(folder + "\\frames.csv");
            if (frames == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(Text.Json(folder));
            sb.Append(",\"report\":").Append(RawOrNull(store.ReadText(folder + "\\report.json")));
            sb.Append(",\"sessionInfo\":").Append(RawOrNull(store.ReadText(folder + "\\" + SessionFolders.SessionInfoFile)));
            sb.Append(",\"system\":").Append(RawOrNull(store.ReadText(folder + "\\system.json")));
            sb.Append(",\"mods\":").Append(RawOrNull(store.ReadText(folder + "\\mods.json")));
            sb.Append(",\"frames\":").Append(Text.Json(frames));
            sb.Append(",\"spikes\":").Append(Text.Json(store.ReadText(folder + "\\spikes.jsonl")));
            sb.Append(",\"timing\":").Append(Text.Json(store.ReadText(folder + "\\mod-timing.csv")));
            sb.Append(",\"summary\":").Append(Text.Json(store.ReadText(folder + "\\summary.md")));
            sb.Append(",\"ai\":").Append(Text.Json(store.ReadText(folder + "\\ai-report.md")));
            sb.Append('}');
            return sb.ToString();
        }

        private static string RawOrNull(string? json) => string.IsNullOrWhiteSpace(json) ? "null" : json!.Trim();
    }
}
