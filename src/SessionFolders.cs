using System;
using System.Collections.Generic;
using System.Text;
using Railroader.Modding;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Names and clean-up of session folders inside Output.
    ///
    /// While recording a session is "2026-11-20 183402 recording"; when it stops it becomes
    /// "2026-11-20 1834 bushnell-whittier Seasons off" (date, time, map, the player's label).
    /// Only Performance Analyzer's own folders are ever deleted (name starts with a date, Performance Analyzer's system.json inside),
    /// and sessions with a label are never deleted automatically.
    /// </summary>
    internal static class SessionFolders
    {
        public const string MarkerFile = "system.json";
        public const string SessionInfoFile = "session.json";
        private const int MaxNameLength = 90;

        public static string RecordingName(OutputStore store, DateTime started) =>
            Unique(store, started.ToString("yyyy-MM-dd HHmmss", Text.Inv) + " recording");

        public static string FinalName(OutputStore store, string currentName, DateTime started, string map, string label)
        {
            var sb = new StringBuilder(started.ToString("yyyy-MM-dd HHmm", Text.Inv));
            string m = Clean(map), l = Clean(label);
            if (m.Length > 0) sb.Append(' ').Append(m);
            if (l.Length > 0) sb.Append(' ').Append(l);
            string name = sb.ToString();
            if (name.Length > MaxNameLength)
            {
                name = name.Substring(0, MaxNameLength).TrimEnd(' ', '.', '-');
            }
            return name == currentName ? name : Unique(store, name);
        }

        /// <summary>session.json: what the player and the summary need to know about the session.</summary>
        [Serializable]
        internal sealed class SessionInfo
        {
            public string label = "";
            public string map = "";
            public string startedLocal = "";
            public string endedLocal = "";
        }

        public static string SessionInfoJson(string label, string map, DateTime started, DateTime ended) =>
            UnityEngine.JsonUtility.ToJson(new SessionInfo
            {
                label = label ?? "",
                map = map ?? "",
                startedLocal = started.ToString("yyyy-MM-dd HH:mm:ss", Text.Inv),
                endedLocal = ended.ToString("yyyy-MM-dd HH:mm:ss", Text.Inv),
            }, true) + "\n";

        /// <summary>Every Performance Analyzer session folder in Output (name starts with a date, Performance Analyzer's system.json inside).</summary>
        public static List<string> All(OutputStore store)
        {
            var list = new List<string>();
            foreach (string name in store.ListDirectories())
            {
                if (StartsWithDate(name) && store.FileExists(name + "\\" + MarkerFile))
                {
                    list.Add(name);
                }
            }
            return list;
        }

        /// <summary>Newest Performance Analyzer session folder in Output, or null.</summary>
        public static string? Latest(OutputStore store)
        {
            string? latest = null;
            foreach (string name in store.ListDirectories())
            {
                if (StartsWithDate(name) && store.FileExists(name + "\\" + MarkerFile) && (latest == null || string.CompareOrdinal(name, latest) > 0))
                {
                    latest = name;
                }
            }
            return latest;
        }

        /// <summary>
        /// Before a new session: deletes the oldest unlabelled Performance Analyzer sessions so that at most keepOld remain.
        /// </summary>
        public static void Prune(OutputStore store, int keepOld, IModLogger log)
        {
            try
            {
                var candidates = new List<string>();
                foreach (string name in store.ListDirectories())
                {
                    if (!StartsWithDate(name) || !store.FileExists(name + "\\" + MarkerFile) || HasLabel(store, name))
                    {
                        continue;
                    }
                    candidates.Add(name);
                }
                candidates.Sort(StringComparer.Ordinal); // The name starts with the date and time, so this is oldest first.
                for (int i = 0; i < candidates.Count - Math.Max(0, keepOld); i++)
                {
                    store.DeleteSessionDirectory(candidates[i]);
                }
            }
            catch (Exception ex)
            {
                log.Warning("MH Performance Analyzer: old sessions could not be cleaned up (" + ex.GetType().Name + ").");
            }
        }

        private static bool HasLabel(OutputStore store, string folder)
        {
            string? json = store.ReadText(folder + "\\" + SessionInfoFile);
            if (json == null)
            {
                return false;
            }
            try
            {
                var info = UnityEngine.JsonUtility.FromJson<SessionInfo>(json);
                return info != null && !string.IsNullOrEmpty(info.label);
            }
            catch (Exception)
            {
                return true; // Unreadable: keep it rather than risk deleting a labelled session.
            }
        }

        /// <summary>"2026-11-20 ..." : four digits, dash, two digits, dash, two digits, space.</summary>
        private static bool StartsWithDate(string name)
        {
            if (name.Length < 11 || name[4] != '-' || name[7] != '-' || name[10] != ' ')
            {
                return false;
            }
            for (int i = 0; i < 10; i++)
            {
                if (i != 4 && i != 7 && !char.IsDigit(name[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Keeps letters, digits, space, dash, underscore and brackets; everything else becomes a dash.</summary>
        public static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var sb = new StringBuilder(text!.Length);
            foreach (char c in text)
            {
                bool ok = char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')';
                char add = ok ? c : '-';
                if ((add == '-' || add == ' ') && sb.Length > 0 && sb[sb.Length - 1] == add)
                {
                    continue;
                }
                sb.Append(add);
            }
            return sb.ToString().Trim(' ', '-');
        }

        private static string Unique(OutputStore store, string name)
        {
            string candidate = name;
            for (int i = 2; store.DirectoryExists(candidate); i++)
            {
                candidate = name + " (" + i + ")";
            }
            return candidate;
        }
    }
}
