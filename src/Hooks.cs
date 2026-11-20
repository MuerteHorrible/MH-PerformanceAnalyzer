using System;
using System.Collections.Generic;
using Game.Persistence;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace MHPerformanceAnalyzer
{
    /// <summary>Inserts the per-frame sample at the start of the PlayerLoop Initialization phase.</summary>
    internal static class LoopHook
    {
        private struct AnalyzerFrameSample
        {
        }

        public static bool Install(PlayerLoopSystem.UpdateFunction callback)
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(root))
            {
                return true;
            }
            var subs = root.subSystemList;
            if (subs == null)
            {
                return false;
            }
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(UnityEngine.PlayerLoop.Initialization))
                {
                    continue;
                }
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                list.Insert(0, new PlayerLoopSystem { type = typeof(AnalyzerFrameSample), updateDelegate = callback });
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return true;
            }
            return false;
        }

        public static void Uninstall()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (!Contains(root))
            {
                return;
            }
            root = Strip(root);
            PlayerLoop.SetPlayerLoop(root);
        }

        private static bool Contains(PlayerLoopSystem system)
        {
            if (system.type == typeof(AnalyzerFrameSample))
            {
                return true;
            }
            if (system.subSystemList == null)
            {
                return false;
            }
            foreach (var s in system.subSystemList)
            {
                if (Contains(s))
                {
                    return true;
                }
            }
            return false;
        }

        private static PlayerLoopSystem Strip(PlayerLoopSystem system)
        {
            if (system.subSystemList == null)
            {
                return system;
            }
            var list = new List<PlayerLoopSystem>(system.subSystemList.Length);
            foreach (var s in system.subSystemList)
            {
                if (s.type != typeof(AnalyzerFrameSample))
                {
                    list.Add(Strip(s));
                }
            }
            system.subSystemList = list.ToArray();
            return system;
        }
    }

    /// <summary>Marks frames in which the world was serialised (manual save and autosave both call WorldStore.Save).</summary>
    internal static class SaveHook
    {
        public static int Depth;
        public static int LastSaveFrame = -1000;
        public static int Count;
        public static double LastMs;
        public static double MaxMs;
        private static long startTicks;

        public static void Reset()
        {
            Depth = 0;
            LastSaveFrame = -1000;
            Count = 0;
            LastMs = 0;
            MaxMs = 0;
        }

        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(WorldStore), nameof(WorldStore.Save), Type.EmptyTypes);
            if (target == null)
            {
                ModEntryPoint.Log?.Warning("MH Performance Analyzer: WorldStore.Save not found, spikes will not show saving.");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SaveHook), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SaveHook), nameof(Finalizer)) { priority = Priority.Last });
        }

        private static void Prefix()
        {
            if (Depth == 0)
            {
                startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            Depth++;
            Count++;
            LastSaveFrame = Time.frameCount;
        }

        private static void Finalizer()
        {
            if (Depth > 0)
            {
                Depth--;
            }
            if (Depth == 0)
            {
                LastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (LastMs > MaxMs)
                {
                    MaxMs = LastMs;
                }
            }
            LastSaveFrame = Time.frameCount;
        }
    }
}
