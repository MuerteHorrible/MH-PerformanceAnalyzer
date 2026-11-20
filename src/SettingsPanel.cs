using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UI.Builder;
using UI.PreferencesWindow;
using UnityEngine;
using UnityEngine.UI;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// The MH Performance Analyzer section in Preferences > Mods: recording ON/OFF, the session label, links to the reports,
    /// and every setting. Uses the shared Mods tab convention (started by ashenmare's mods): each taking part mod
    /// adds its draw method to a list in AppDomain data under SlotKey and patches PreferencesBuilder.BuildTabs; only
    /// the mod first in the list adds the tab, which then draws every mod's section.
    /// This patch is the only one Performance Analyzer keeps while it is not recording.
    /// </summary>
    internal static class SettingsPanel
    {
        private const string HarmonyId = "mh.performance-analyzer.settings";
        private const string SlotKey = "ashenmare.preferences-mods.v1";
        private const string TabId = "ashenmare.mods";
        private const float LabelWidth = 146f;
        private const float TallRowHeight = 44f;

        private static Harmony? harmony;
        private static Action<UIPanelBuilder>? mine;
        private static readonly UIState<bool> Expanded = new UIState<bool>(false);
        private static readonly UIState<bool> AdvancedExpanded = new UIState<bool>(false);
        private static bool loggedError;

        public static void Register()
        {
            try
            {
                MethodInfo? target = AccessTools.Method(typeof(PreferencesBuilder), "BuildTabs", new[] { typeof(UITabbedPanelBuilder) });
                if (target == null)
                {
                    ModEntryPoint.Log?.Info("MH Performance Analyzer: Preferences > Mods section unavailable (game changed?).");
                    return;
                }
                harmony = new Harmony(HarmonyId);
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SettingsPanel), nameof(BuildTabsPostfix)));
                Unregister();
                mine = Draw;
                var list = Slot(create: true)!;
                lock (list)
                {
                    list.Add(mine);
                }
            }
            catch (Exception ex)
            {
                ModEntryPoint.Log?.Error(ex, "MH Performance Analyzer: could not add the Preferences > Mods section.");
            }
        }

        public static void Unregister()
        {
            try
            {
                var list = Slot(create: false);
                if (list != null && mine != null)
                {
                    lock (list)
                    {
                        list.Remove(mine);
                    }
                }
            }
            catch (Exception)
            {
            }
            mine = null;
        }

        public static void Remove()
        {
            Unregister();
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }

        private static List<Action<UIPanelBuilder>>? Slot(bool create)
        {
            AppDomain domain = AppDomain.CurrentDomain;
            var list = domain.GetData(SlotKey) as List<Action<UIPanelBuilder>>;
            if (list == null && create)
            {
                list = new List<Action<UIPanelBuilder>>();
                domain.SetData(SlotKey, list);
            }
            return list;
        }

        private static void BuildTabsPostfix(UITabbedPanelBuilder builder)
        {
            try
            {
                var list = Slot(create: false);
                if (list == null || mine == null)
                {
                    return;
                }
                lock (list)
                {
                    if (list.Count == 0 || !ReferenceEquals(list[0], mine))
                    {
                        return; // Another mod is first and adds the tab.
                    }
                }
                builder.AddTab("Mods", TabId, DrawTab);
            }
            catch (Exception ex)
            {
                LogOnce(ex, "Could not add the Mods tab to Preferences.");
            }
        }

        private static void DrawTab(UIPanelBuilder builder)
        {
            builder.VScrollView(scroll =>
            {
                scroll.AddLabel("<style=Footnote>Settings for the mods you have that offer them, saved in each mod's folder on this computer.</style>");
                var list = Slot(create: false);
                if (list == null)
                {
                    return;
                }
                Action<UIPanelBuilder>[] sections;
                lock (list)
                {
                    sections = list.ToArray();
                }
                foreach (var section in sections)
                {
                    try
                    {
                        section(scroll);
                    }
                    catch (Exception ex)
                    {
                        LogOnce(ex, "A mod's settings section failed to draw.");
                    }
                }
            });
        }

        private static void Draw(UIPanelBuilder builder)
        {
            try
            {
                builder.AddExpandable("MH Performance Analyzer", Expanded, Content, ExpandableStyle.SectionHeader);
            }
            catch (Exception ex)
            {
                LogOnce(ex, "Could not draw the MH Performance Analyzer settings.");
            }
        }

        private static void Content(UIPanelBuilder b)
        {
            Config s = Recording.Settings;

            b.AddLabel(() => Recording.StatusText(), UIPanelBuilder.Frequency.Periodic);
            b.ButtonStrip(row =>
            {
                row.AddButton(Recording.IsRecording ? "Stop recording" : "Start recording", () =>
                {
                    if (Recording.IsRecording) Recording.Stop(); else Recording.Start();
                    b.Rebuild();
                });
                row.AddButton("Open report", () => Recording.OpenReport());
                row.AddButton("Open Output folder", () => Recording.OpenOutputFolder());
            });
            Field(b, "Session label", b.AddInputField(s.sessionLabel, text =>
            {
                s.sessionLabel = text.Trim();
                Recording.SaveSettings();
            }, "optional, for example: Seasons off", 60));
            b.AddLabel("<alpha=#80>Added to the session's folder name when recording stops. Labelled sessions are never deleted automatically.");

            b.AddExpandable("Recording settings (used from the next Start)", AdvancedExpanded, a =>
            {
                Toggle(a, "Per-mod timing", () => s.perModTiming, x => s.perModTiming = x,
                    "<alpha=#80>default on: times other mods' code on the main thread");
                TextList(a, "Only time these mods", s.focusMods, x => s.focusMods = x, "mod ids, comma separated; empty times every mod");
                TextList(a, "Never time these mods", s.excludeOwners, x => s.excludeOwners = x, "mod ids or Harmony ids, comma separated");
                TextList(a, "Never time these methods", s.excludeMethods, x => s.excludeMethods = x, "Namespace.Type.Method, comma separated");
                Slider(a, "Spike threshold", () => s.spikeThresholdMs, x => s.spikeThresholdMs = x, 0.1f, 10f, 100f, v => $"{v:0.0} ms",
                    "a frame longer than this is a spike (default 33.3 ms, one frame at 30 fps)");
                Slider(a, "Spike multiplier", () => s.spikeMultiplier, x => s.spikeMultiplier = x, 0.5f, 1.5f, 10f, v => $"x {v:0.0}",
                    "or longer than this times the recent median frame, whichever is larger (default x 3.0)");
                Slider(a, "Spike cooldown", () => s.spikeCooldownMs, x => s.spikeCooldownMs = x, 50f, 100f, 3000f, v => $"{v:0} ms",
                    "slow frames within this time count as one hitch (default 500 ms)");
                Slider(a, "Calls per frame limit", () => s.maxCallsPerFrame, x => s.maxCallsPerFrame = Mathf.RoundToInt(x), 10f, 10f, 2000f, v => $"{v:0}",
                    "a method called more often than this per frame stops being timed (default 200)");
                Slider(a, "Memory sample", () => s.memorySampleSeconds, x => s.memorySampleSeconds = x, 0.25f, 0.25f, 10f, v => $"{v:0.00} s",
                    "how often memory is read (default 1 s)");
                Slider(a, "Sessions to keep", () => s.keepSessions, x => s.keepSessions = Mathf.RoundToInt(x), 1f, 1f, 100f, v => $"{v:0}",
                    "older unlabelled sessions are deleted when a new one starts (default 10)");
                a.ButtonStrip(row => row.AddButton("Reset to defaults", () =>
                {
                    Recording.ResetSettings();
                    b.Rebuild();
                }));
            }, ExpandableStyle.Label);
        }

        private static void Toggle(UIPanelBuilder b, string label, Func<bool> get, Action<bool> set, string note)
        {
            Field(b, label, b.HStack(row =>
            {
                row.AddToggle(get, x => { set(x); Recording.SaveSettings(); });
                row.Spacer(8f);
                row.AddLabel(note);
            }));
        }

        private static void Slider(UIPanelBuilder b, string label, Func<float> get, Action<float> set, float step, float min, float max, Func<float, string> text, string note)
        {
            Field(b, label, b.AddSliderQuantized(get, () => text(get()), set, step, min, max, _ => Recording.SaveSettings()));
            b.AddLabel("<alpha=#80>" + note);
        }

        private static void TextList(UIPanelBuilder b, string label, string[] current, Action<string[]> set, string note)
        {
            Field(b, label, b.AddInputField(string.Join(", ", current), text =>
            {
                set(text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray());
                Recording.SaveSettings();
            }, note, 400));
        }

        /// <summary>A field whose label wraps onto two lines at full size instead of shrinking (same fix as the other MH mods).</summary>
        private static void Field(UIPanelBuilder b, string label, RectTransform control)
        {
            IConfigurableElement field = b.AddField(label, control);
            try
            {
                TMP_Text? text = field.RectTransform.Find("Label")?.GetComponent<TMP_Text>();
                if (text == null)
                {
                    return;
                }
                float size = text.fontSizeMax;
                text.enableAutoSizing = false;
                text.fontSize = size;
                text.textWrappingMode = TextWrappingModes.Normal;
                text.overflowMode = TextOverflowModes.Overflow;
                string shown = (text.fontStyle & FontStyles.UpperCase) != 0 ? label.ToUpperInvariant() : label;
                if (text.GetPreferredValues(shown, LabelWidth, 0f).y <= size * 1.6f)
                {
                    return;
                }
                text.margin = new Vector4(0f, 4f, 0f, 4f);
                text.verticalAlignment = VerticalAlignmentOptions.Middle;
                text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, TallRowHeight);
                var group = field.RectTransform.GetComponent<LayoutGroup>();
                if (group != null)
                {
                    group.childAlignment = TextAnchor.MiddleLeft;
                }
                var element = field.RectTransform.GetComponent<LayoutElement>();
                if (element != null)
                {
                    element.minHeight = Math.Max(element.minHeight, TallRowHeight);
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex, "Could not adjust a settings label.");
            }
        }

        private static void LogOnce(Exception ex, string message)
        {
            if (!loggedError)
            {
                loggedError = true;
                ModEntryPoint.Log?.Error(ex, "MH Performance Analyzer: " + message);
            }
        }
    }
}
