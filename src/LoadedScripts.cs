using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Model.Database;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Which mods have a Relay main script (Main.rl) running, read from the game's own list of loaded scripts
    /// (AssetPackManager -> MainScriptManager -> loaded scripts) instead of looking at any files.
    /// The list exists once a world has loaded.
    /// </summary>
    internal static class LoadedScripts
    {
        public static HashSet<string> PackIdsWithMainScript()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var mgr = AssetPackManager.Shared;
                object? main = mgr != null ? AccessTools.Field(typeof(AssetPackManager), "_mainScriptManager")?.GetValue(mgr) : null;
                if (main == null)
                {
                    return ids;
                }
                var list = AccessTools.Field(main.GetType(), "_loadedScripts")?.GetValue(main) as IEnumerable;
                if (list == null)
                {
                    return ids;
                }
                foreach (object? item in list)
                {
                    if (item != null && AccessTools.Field(item.GetType(), "PackIdentifier")?.GetValue(item) is string id)
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (Exception)
            {
                // Game internals changed: the mod list then shows no Relay flags.
            }
            return ids;
        }
    }
}
