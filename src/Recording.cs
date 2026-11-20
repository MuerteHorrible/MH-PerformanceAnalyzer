using System;
using Game;
using HarmonyLib;
using Railroader.Modding;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Recording ON/OFF, controlled only by the player.
    ///
    /// Off (the default after every game start): no frame callback, no Harmony patches, no files written.
    /// Start: creates a session folder in Output, adds the per-frame sample, the save marker and (if enabled)
    /// per-mod timing. Stop: writes the final reports, removes every Performance Analyzer patch and the frame callback,
    /// and gives the session folder its final name (date, time, map, the player's label).
    /// </summary>
    internal static class Recording
    {
        private const string HarmonyId = "mh.performance-analyzer";

        private static OutputStore? store;
        private static IModLogger? log;
        private static Harmony? harmony;
        private static PerfRecorder? recorder;
        private static SessionWriter? writer;
        private static GameObject? host;
        private static DateTime startedLocal;
        private static bool worldLoaded;

        public static Config Settings { get; private set; } = new Config();
        public static bool IsRecording => recorder != null;
        public static DateTime StartedLocal => startedLocal;

        /// <summary>Name of the last finished session folder inside Output, or null.</summary>
        public static string? LastSessionFolder { get; private set; }

        public static void Initialise(OutputStore outputStore, Config config, IModLogger logger)
        {
            store = outputStore;
            Settings = config;
            log = logger;
            SettingsPanel.Register();
        }

        /// <summary>Called when the mod unloads: stops recording and removes the settings section.</summary>
        public static void Shutdown()
        {
            Stop();
            SettingsPanel.Remove();
        }

        public static void ResetSettings()
        {
            Settings = new Config();
            SaveSettings();
        }

        /// <summary>
        /// Opens the newest report (Output\index.html once written, otherwise the newest session folder) in the
        /// player's default program. Only on the player's click, and only for something inside Output.
        /// </summary>
        public static void OpenReport()
        {
            if (store == null)
            {
                return;
            }
            try
            {
                if (store.FileExists("index.html"))
                {
                    Application.OpenURL(store.FileUri("index.html"));
                    return;
                }
                string? latest = LastSessionFolder ?? SessionFolders.Latest(store);
                Application.OpenURL(latest != null ? store.FileUri(latest) : store.RootUri());
            }
            catch (Exception ex)
            {
                log?.Error("MH Performance Analyzer: could not open the report (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>Opens the Output folder in the file browser. Only on the player's click.</summary>
        public static void OpenOutputFolder()
        {
            if (store == null)
            {
                return;
            }
            try
            {
                Application.OpenURL(store.RootUri());
            }
            catch (Exception ex)
            {
                log?.Error("MH Performance Analyzer: could not open the Output folder (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>Saves changed settings to Output\settings.json (only called after the player changes one).</summary>
        public static void SaveSettings()
        {
            if (store == null)
            {
                return;
            }
            try
            {
                Settings.Save(store);
            }
            catch (Exception ex)
            {
                log?.Error("MH Performance Analyzer: settings could not be saved (" + ex.GetType().Name + ").");
            }
        }

        public static void Start()
        {
            if (IsRecording || store == null || log == null)
            {
                return;
            }
            try
            {
                SessionFolders.Prune(store, Settings.keepSessions - 1, log);
                startedLocal = DateTime.Now;
                writer = new SessionWriter(store, SessionFolders.RecordingName(store, startedLocal));
                Config session = Settings.Copy(); // Settings changed during the recording apply from the next Start.
                writer.Replace("system.json", Inventory.SystemJson(session));
                writer.Replace("mods.json", Inventory.ModsJson());
                recorder = new PerfRecorder(session, log, writer);

                harmony = new Harmony(HarmonyId);
                try
                {
                    SaveHook.Apply(harmony);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "MH Performance Analyzer: save marker could not be added; spikes will not show saving.");
                }
                Timing.MarkMainThread();
                if (session.perModTiming)
                {
                    recorder.EnableTiming(harmony);
                }
                if (!LoopHook.Install(OnFrame))
                {
                    log.Error("MH Performance Analyzer: could not add the per-frame sample; nothing will be recorded.");
                }
                host = new GameObject("MHPerformanceAnalyzer");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<PerfHost>();
                if (worldLoaded)
                {
                    // Started in a loaded world: it has already settled, so nothing is left out at the start.
                    recorder.OnWorldLoad(alreadySettled: true);
                }
                log.Info($"MH Performance Analyzer: recording started ({recorder.ValidRecorderNote()}).");
            }
            catch (Exception ex)
            {
                log.Error(ex, "MH Performance Analyzer: recording could not start.");
                Stop();
            }
        }

        public static void Stop()
        {
            if (!IsRecording && writer == null)
            {
                return;
            }
            var rec = recorder;
            var w = writer;
            recorder = null;
            writer = null;
            try
            {
                LoopHook.Uninstall();
                string finalName = w != null
                    ? SessionFolders.FinalName(store!, w.FolderName, startedLocal, MapName(), Settings.sessionLabel)
                    : "";
                if (rec != null)
                {
                    rec.SessionName = finalName;
                    rec.WriteSummary(true);
                }
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                    host = null;
                }
                rec?.Dispose();
                if (w != null)
                {
                    w.Replace(SessionFolders.SessionInfoFile, SessionFolders.SessionInfoJson(Settings.sessionLabel, MapName(), startedLocal, DateTime.Now));
                    w.Dispose();
                    LastSessionFolder = w.FolderName;
                    if (finalName != w.FolderName)
                    {
                        store!.RenameDirectory(w.FolderName, finalName);
                        LastSessionFolder = finalName;
                    }
                }
                if (LastSessionFolder != null)
                {
                    HtmlReport.WriteSessionReport(store!, LastSessionFolder);
                    HtmlReport.WriteIndex(store!);
                }
                log?.Info("MH Performance Analyzer: recording stopped, saved as Output\\" + LastSessionFolder + ".");
            }
            catch (Exception ex)
            {
                log?.Error(ex, "MH Performance Analyzer: recording did not stop cleanly.");
            }
            finally
            {
                Timing.Reset();
                SaveHook.Reset();
                RelayTiming.Reset();
                if (Settings.sessionLabel.Length > 0)
                {
                    // A label belongs to one session.
                    Settings.sessionLabel = "";
                    SaveSettings();
                }
            }
        }

        public static void OnWorldLoad()
        {
            worldLoaded = true;
            try
            {
                recorder?.OnWorldLoad(alreadySettled: false);
                writer?.Replace("mods.json", Inventory.ModsJson());
            }
            catch (Exception ex)
            {
                log?.Error(ex, "MH Performance Analyzer: world load handling failed.");
            }
        }

        public static void OnWorldUnload()
        {
            worldLoaded = false;
            try
            {
                recorder?.OnWorldUnload();
                recorder?.WriteSummary(false);
            }
            catch (Exception ex)
            {
                log?.Error(ex, "MH Performance Analyzer: world unload handling failed.");
            }
        }

        /// <summary>Last part of the active map's identifier ("rr/bushnell-whittier" -> "bushnell-whittier"), or "menu".</summary>
        public static string MapName()
        {
            try
            {
                string? id = ActiveMapDefinition.Identifier;
                if (string.IsNullOrEmpty(id))
                {
                    return "menu";
                }
                int slash = id!.LastIndexOf('/');
                return slash >= 0 ? id.Substring(slash + 1) : id;
            }
            catch (Exception)
            {
                return "menu";
            }
        }

        public static string StatusText()
        {
            if (!IsRecording)
            {
                return LastSessionFolder == null ? "Not recording" : "Not recording. Last session: " + LastSessionFolder;
            }
            var span = DateTime.Now - startedLocal;
            return $"Recording for {(int)span.TotalMinutes} min {span.Seconds:00} s, {recorder!.PlaySpikeCount} play spikes so far";
        }

        private static void OnFrame() => recorder?.OnFrame();

        internal static int LastSampleFrame => recorder == null || recorder.Disabled ? -1 : recorder.LastSampleFrame;

        internal static bool ReinstallHook() => recorder != null && !recorder.Disabled && LoopHook.Install(OnFrame);
    }

    /// <summary>While recording: if something rebuilt the PlayerLoop and dropped the sample, put it back (logged, at most 5 times).</summary>
    internal sealed class PerfHost : MonoBehaviour
    {
        private int reinstalls;

        private void Update()
        {
            int last = Recording.LastSampleFrame;
            if (last < 0 || last == Time.frameCount || reinstalls >= 5)
            {
                return;
            }
            reinstalls++;
            bool ok = Recording.ReinstallHook();
            ModEntryPoint.Log?.Warning($"MH Performance Analyzer: per-frame sample was missing, added again ({(ok ? "ok" : "failed")}, {reinstalls}/5).");
        }
    }
}
