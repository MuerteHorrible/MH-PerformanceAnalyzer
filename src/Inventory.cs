using System;
using System.Collections.Generic;
using System.Text;
using Model.Database;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Builds system.json and mods.json. Only facts that help explain the game's performance are recorded:
    /// game and engine version, CPU thread count, video and system memory, graphics API, refresh rate,
    /// frame cap, quality level, whether the game's HTTP Server is on, and the mod list. No CPU or GPU model, screen resolution, user name, computer name, operating system or file paths.
    /// </summary>
    internal static class Inventory
    {
        public static string SystemJson(Config config)
        {
            var sb = new StringBuilder();
            int q = QualitySettings.GetQualityLevel();
            string qName = q >= 0 && q < QualitySettings.names.Length ? QualitySettings.names[q] : q.ToString(Text.Inv);
            sb.Append("{\n");
            Field(sb, "analyzerVersion", ModEntryPoint.Version);
            Field(sb, "sessionStartLocal", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Text.Inv));
            Field(sb, "gameVersion", Application.version);
            Field(sb, "unityVersion", Application.unityVersion);
            Raw(sb, "cpuThreads", SystemInfo.processorCount.ToString(Text.Inv));
            Raw(sb, "gpuMemoryMB", SystemInfo.graphicsMemorySize.ToString(Text.Inv));
            Field(sb, "graphicsApi", SystemInfo.graphicsDeviceType.ToString());
            Raw(sb, "ramMB", SystemInfo.systemMemorySize.ToString(Text.Inv));
            Raw(sb, "refreshHz", Text.F(Screen.currentResolution.refreshRateRatio.value, "0.##"));
            Field(sb, "fullScreenMode", Screen.fullScreenMode.ToString());
            Field(sb, "quality", qName);
            Raw(sb, "vSyncCount", QualitySettings.vSyncCount.ToString(Text.Inv));
            Raw(sb, "targetFrameRate", Application.targetFrameRate.ToString(Text.Inv));
            Raw(sb, "incrementalGc", UnityEngine.Scripting.GarbageCollector.isIncremental ? "true" : "false");
            Raw(sb, "httpServerEnabled", GameHttpServerEnabled() ? "true" : "false");
            var settings = new List<string>();
            foreach (var s in GameSettings.Read())
            {
                settings.Add(Text.Json(s.name) + ": " + Text.Json(s.value));
            }
            Raw(sb, "gameSettings", "{" + string.Join(", ", settings) + "}");
            Raw(sb, "config", JsonUtility.ToJson(config), last: true);
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The same facts as system.json, for report.json.</summary>
        public static void FillSystem(ReportData.SystemSummary s)
        {
            int q = QualitySettings.GetQualityLevel();
            s.gameVersion = Application.version;
            s.unityVersion = Application.unityVersion;
            s.cpuThreads = SystemInfo.processorCount;
            s.gpuMemoryMB = SystemInfo.graphicsMemorySize;
            s.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            s.ramMB = SystemInfo.systemMemorySize;
            s.refreshHz = Math.Round(Screen.currentResolution.refreshRateRatio.value, 2);
            s.quality = q >= 0 && q < QualitySettings.names.Length ? QualitySettings.names[q] : q.ToString(Text.Inv);
            s.vSyncCount = QualitySettings.vSyncCount;
            s.targetFrameRate = Application.targetFrameRate;
            s.httpServerEnabled = GameHttpServerEnabled();
        }

        /// <summary>
        /// Whether the game's own HTTP Server preference is on (Preferences > HTTP Server). Only the setting is
        /// read: a connected tool makes the game handle requests on the main thread, which shows up as game code.
        /// </summary>
        public static bool GameHttpServerEnabled()
        {
            try
            {
                return Game.Preferences.HTTPServerEnabled;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Every loaded mod in load order: id, version, and whether it runs code (DLL), a Relay main script, or neither.</summary>
        public static List<ReportData.LoadedMod> LoadedMods()
        {
            var list = new List<ReportData.LoadedMod>();
            var mgr = AssetPackManager.Shared;
            if (mgr == null)
            {
                return list;
            }
            var withMainScript = LoadedScripts.PackIdsWithMainScript();
            foreach (var pack in mgr.OrderedPacks)
            {
                var info = pack.Info;
                if (info == null)
                {
                    continue;
                }
                var kinds = new List<string>();
                if (info.Assemblies != null && info.Assemblies.Count > 0) kinds.Add("dll");
                if (withMainScript.Contains(info.Identifier ?? "")) kinds.Add("relay-main");
                if (kinds.Count == 0) kinds.Add("content");
                list.Add(new ReportData.LoadedMod
                {
                    id = info.Identifier ?? "",
                    name = info.Name ?? "",
                    version = info.Version ?? "",
                    types = kinds,
                    builtin = info.Identifier != null && info.Identifier.StartsWith("rr/", StringComparison.Ordinal),
                });
            }
            return list;
        }

        /// <summary>mods.json: the same list as report.json's loadedMods, one mod per line.</summary>
        public static string ModsJson()
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            var mods = LoadedMods();
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                var types = new List<string>();
                foreach (var k in m.types) types.Add(Text.Json(k));
                sb.Append("  {\"id\": ").Append(Text.Json(m.id))
                    .Append(", \"version\": ").Append(Text.Json(m.version))
                    .Append(", \"name\": ").Append(Text.Json(m.name))
                    .Append(", \"types\": [").Append(string.Join(", ", types)).Append(']')
                    .Append(", \"builtin\": ").Append(m.builtin ? "true" : "false")
                    .Append(i < mods.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static void Field(StringBuilder sb, string name, string? value) => Raw(sb, name, Text.Json(value));

        private static void Raw(StringBuilder sb, string name, string value, bool last = false)
        {
            sb.Append("  ").Append(Text.Json(name)).Append(": ").Append(value).Append(last ? "\n" : ",\n");
        }
    }
}
