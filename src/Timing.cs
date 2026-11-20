using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine.Profiling;

namespace MHPerformanceAnalyzer
{
    /// <summary>State passed from a timing prefix to its finalizer. SlotPlus1 == 0 means "not timed".</summary>
    public struct TimingState
    {
        public int SlotPlus1;
        public long Start;
        public long Mem;
    }

    /// <summary>
    /// Per-method timing slots, all preallocated. Main thread only. Each slot keeps inclusive time and
    /// allocation (whole patched call) and exclusive values (minus nested timed calls); per-mod sums use
    /// exclusive values so nested calls are not counted twice. Recursion into an active slot is not re-timed.
    /// Allocation = change in mono heap used size across the call, approximate (allocator chunks, and a
    /// call during which a GC ran counts as 0).
    /// </summary>
    internal static class Timing
    {
        public const int MaxSlots = 1024;
        public const int MaxMods = 256;
        private const int MaxDepth = 64;

        [ThreadStatic] private static bool isMain;

        public static void MarkMainThread() => isMain = true;
        public static bool IsMain => isMain;

        // Slot metadata.
        public static int SlotCount;
        public static readonly string[] Label = new string[MaxSlots];
        public static readonly string[] Kind = new string[MaxSlots];
        public static readonly int[][] Mods = new int[MaxSlots][];
        public static readonly MethodBase?[] Method = new MethodBase?[MaxSlots];
        public static readonly bool[] Skipped = new bool[MaxSlots];

        // Lookup for the fallback (non-generated) prefix.
        public static readonly Dictionary<IntPtr, int> ByHandle = new Dictionary<IntPtr, int>();

        // Session totals.
        public static readonly long[] TotCalls = new long[MaxSlots];
        public static readonly long[] TotTicks = new long[MaxSlots];
        public static readonly long[] TotExcl = new long[MaxSlots];
        public static readonly long[] TotAlloc = new long[MaxSlots];
        public static readonly long[] TotAllocExcl = new long[MaxSlots];
        public static readonly long[] MaxCallTicks = new long[MaxSlots];
        public static readonly long[] MaxFrameTicks = new long[MaxSlots];

        // 10 s window.
        public static readonly long[] WinCalls = new long[MaxSlots];
        public static readonly long[] WinTicks = new long[MaxSlots];
        public static readonly long[] WinMaxCall = new long[MaxSlots];
        public static readonly long[] WinAlloc = new long[MaxSlots];
        public static long WinFrames;

        // Current frame.
        private static readonly long[] frameCalls = new long[MaxSlots];
        private static readonly long[] frameTicks = new long[MaxSlots];
        private static readonly long[] frameExcl = new long[MaxSlots];
        private static readonly long[] frameAlloc = new long[MaxSlots];
        private static readonly long[] frameAllocExcl = new long[MaxSlots];
        private static readonly int[] touched = new int[MaxSlots];
        private static int touchedCount;
        private static readonly int[] depth = new int[MaxSlots];

        // Nesting stack.
        private static readonly int[] stackSlot = new int[MaxDepth];
        private static readonly long[] stackChild = new long[MaxDepth];
        private static readonly long[] stackChildAlloc = new long[MaxDepth];
        private static int sp;

        /// <summary>Times a timed call was still open at the end of a frame (should stay 0).</summary>
        public static int Unbalanced;
        /// <summary>Timed calls during which a GC ran (allocation counted as 0).</summary>
        public static long GcDuringCall;
        /// <summary>Exclusive allocation by all timed calls in the frame that just ended.</summary>
        public static long LastFrameTrackedAlloc;
        public static long TotTrackedAlloc;

        // Mods.
        public static int ModCount;
        public static readonly string[] ModId = new string[MaxMods];
        private static readonly Dictionary<string, int> modIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        public static readonly double[] ModTotMs = new double[MaxMods];
        public static readonly long[] ModTotAlloc = new long[MaxMods];
        public static readonly double[] ModMaxFrameMs = new double[MaxMods];
        public static readonly long[] ModMaxFrameAlloc = new long[MaxMods];
        public static readonly int[] ModPlaySpikesOver1 = new int[MaxMods];
        public static readonly double[] ModMaxSpikeMs = new double[MaxMods];
        /// <summary>Exclusive ms / bytes per mod in the frame that just ended (valid until the next EndFrame).</summary>
        public static readonly double[] LastModMs = new double[MaxMods];
        public static readonly long[] LastModAlloc = new long[MaxMods];
        private static readonly int[] lastTouchedMods = new int[MaxMods];
        private static int lastTouchedModCount;
        private static readonly bool[] modTouchedFlag = new bool[MaxMods];

        // Top 5 slots (inclusive ms) of the frame that just ended.
        public const int TopN = 5;
        public static readonly int[] LastTopSlot = new int[TopN];
        public static readonly double[] LastTopMs = new double[TopN];
        public static readonly long[] LastTopAlloc = new long[TopN];
        public static int LastTopCount;

        public static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

        public static int ModIndex(string id)
        {
            if (modIndex.TryGetValue(id, out int i))
            {
                return i;
            }
            if (ModCount >= MaxMods)
            {
                return MaxMods - 1;
            }
            i = ModCount++;
            ModId[i] = id;
            modIndex[id] = i;
            return i;
        }

        /// <summary>Adds a slot; returns -1 when full.</summary>
        public static int AddSlot(string label, string kind, int[] mods, MethodBase? method)
        {
            if (SlotCount >= MaxSlots)
            {
                return -1;
            }
            int s = SlotCount++;
            Label[s] = label;
            Kind[s] = kind;
            Mods[s] = mods;
            Method[s] = method;
            if (method != null)
            {
                ByHandle[method.MethodHandle.Value] = s;
            }
            return s;
        }

        public static void Enter(int s, out TimingState state)
        {
            if (!isMain || s < 0 || depth[s] > 0 || sp >= MaxDepth || Skipped[s])
            {
                state = default;
                return;
            }
            depth[s]++;
            stackSlot[sp] = s;
            stackChild[sp] = 0;
            stackChildAlloc[sp] = 0;
            sp++;
            state.SlotPlus1 = s + 1;
            state.Mem = Profiler.GetMonoUsedSizeLong();
            state.Start = Stopwatch.GetTimestamp();
        }

        public static void Exit(ref TimingState state)
        {
            if (state.SlotPlus1 == 0)
            {
                return;
            }
            long dt = Stopwatch.GetTimestamp() - state.Start;
            long alloc = Profiler.GetMonoUsedSizeLong() - state.Mem;
            if (alloc < 0)
            {
                GcDuringCall++;
                alloc = 0;
            }
            int s = state.SlotPlus1 - 1;
            state.SlotPlus1 = 0;
            if (depth[s] > 0)
            {
                depth[s]--;
            }

            // Pop this slot; if the stack got out of step, resync to it.
            int i = sp - 1;
            while (i >= 0 && stackSlot[i] != s)
            {
                i--;
            }
            if (i < 0)
            {
                return;
            }
            long child = stackChild[i];
            long childAlloc = stackChildAlloc[i];
            sp = i;
            if (sp > 0)
            {
                stackChild[sp - 1] += dt;
                stackChildAlloc[sp - 1] += alloc;
            }

            if (frameCalls[s] == 0)
            {
                touched[touchedCount++] = s;
            }
            frameCalls[s]++;
            frameTicks[s] += dt;
            frameExcl[s] += dt > child ? dt - child : 0;
            frameAlloc[s] += alloc;
            frameAllocExcl[s] += alloc > childAlloc ? alloc - childAlloc : 0;
            if (dt > MaxCallTicks[s])
            {
                MaxCallTicks[s] = dt;
            }
            if (dt > WinMaxCall[s])
            {
                WinMaxCall[s] = dt;
            }
        }

        /// <summary>Folds the frame that just ended into totals; fills LastMod* and LastTop*. No allocation.</summary>
        public static void EndFrame()
        {
            for (int k = 0; k < lastTouchedModCount; k++)
            {
                int m = lastTouchedMods[k];
                LastModMs[m] = 0;
                LastModAlloc[m] = 0;
                modTouchedFlag[m] = false;
            }
            lastTouchedModCount = 0;
            LastTopCount = 0;
            LastFrameTrackedAlloc = 0;
            WinFrames++;
            // No timed call can be open at the start of a frame on the main thread; release any that are.
            if (sp > 0)
            {
                for (int i = 0; i < sp; i++)
                {
                    depth[stackSlot[i]] = 0;
                }
                Unbalanced++;
                sp = 0;
            }

            for (int k = 0; k < touchedCount; k++)
            {
                int s = touched[k];
                long calls = frameCalls[s];
                long ticks = frameTicks[s];
                long excl = frameExcl[s];
                long alloc = frameAlloc[s];
                long allocExcl = frameAllocExcl[s];
                frameCalls[s] = 0;
                frameTicks[s] = 0;
                frameExcl[s] = 0;
                frameAlloc[s] = 0;
                frameAllocExcl[s] = 0;

                TotCalls[s] += calls;
                TotTicks[s] += ticks;
                TotExcl[s] += excl;
                TotAlloc[s] += alloc;
                TotAllocExcl[s] += allocExcl;
                WinCalls[s] += calls;
                WinTicks[s] += ticks;
                WinAlloc[s] += alloc;
                LastFrameTrackedAlloc += allocExcl;
                if (ticks > MaxFrameTicks[s])
                {
                    MaxFrameTicks[s] = ticks;
                }

                double exclMs = excl * TickToMs;
                var mods = Mods[s];
                for (int j = 0; j < mods.Length; j++)
                {
                    int m = mods[j];
                    if (!modTouchedFlag[m])
                    {
                        modTouchedFlag[m] = true;
                        lastTouchedMods[lastTouchedModCount++] = m;
                    }
                    LastModMs[m] += exclMs;
                    LastModAlloc[m] += allocExcl;
                }

                // Insert into top N by inclusive ms.
                double ms = ticks * TickToMs;
                int pos = LastTopCount < TopN ? LastTopCount : TopN;
                while (pos > 0 && LastTopMs[pos - 1] < ms)
                {
                    pos--;
                }
                if (pos < TopN)
                {
                    int last = LastTopCount < TopN ? LastTopCount : TopN - 1;
                    for (int q = last; q > pos; q--)
                    {
                        LastTopSlot[q] = LastTopSlot[q - 1];
                        LastTopMs[q] = LastTopMs[q - 1];
                        LastTopAlloc[q] = LastTopAlloc[q - 1];
                    }
                    LastTopSlot[pos] = s;
                    LastTopMs[pos] = ms;
                    LastTopAlloc[pos] = alloc;
                    if (LastTopCount < TopN)
                    {
                        LastTopCount++;
                    }
                }
            }
            touchedCount = 0;
            TotTrackedAlloc += LastFrameTrackedAlloc;

            for (int k = 0; k < lastTouchedModCount; k++)
            {
                int m = lastTouchedMods[k];
                double ms = LastModMs[m];
                ModTotMs[m] += ms;
                ModTotAlloc[m] += LastModAlloc[m];
                if (ms > ModMaxFrameMs[m])
                {
                    ModMaxFrameMs[m] = ms;
                }
                if (LastModAlloc[m] > ModMaxFrameAlloc[m])
                {
                    ModMaxFrameAlloc[m] = LastModAlloc[m];
                }
            }
        }

        /// <summary>Called once per play spike with the per-mod ms of its worst frame: counts mods over 1 ms.</summary>
        public static void NotePlaySpike(List<KeyValuePair<int, double>> modMs)
        {
            foreach (var kv in modMs)
            {
                if (kv.Value > 1.0)
                {
                    ModPlaySpikesOver1[kv.Key]++;
                }
                if (kv.Value > ModMaxSpikeMs[kv.Key])
                {
                    ModMaxSpikeMs[kv.Key] = kv.Value;
                }
            }
        }

        /// <summary>
        /// Clears all slots, totals and mods. The game keeps a mod's assembly loaded when the mod list is
        /// changed in-game and calls OnModLoad again, so statics would otherwise carry over (F4).
        /// </summary>
        public static void Reset()
        {
            SlotCount = 0;
            ModCount = 0;
            modIndex.Clear();
            ByHandle.Clear();
            foreach (Array a in new Array[]
            {
                Label, Kind, Mods, Method, Skipped, TotCalls, TotTicks, TotExcl, TotAlloc, TotAllocExcl, MaxCallTicks, MaxFrameTicks,
                WinCalls, WinTicks, WinMaxCall, WinAlloc, frameCalls, frameTicks, frameExcl, frameAlloc, frameAllocExcl, depth,
                ModId, ModTotMs, ModTotAlloc, ModMaxFrameMs, ModMaxFrameAlloc, ModPlaySpikesOver1, ModMaxSpikeMs, LastModMs, LastModAlloc, modTouchedFlag,
            })
            {
                Array.Clear(a, 0, a.Length);
            }
            touchedCount = 0;
            sp = 0;
            lastTouchedModCount = 0;
            LastTopCount = 0;
            WinFrames = 0;
            Unbalanced = 0;
            GcDuringCall = 0;
            LastFrameTrackedAlloc = 0;
            TotTrackedAlloc = 0;
        }

        /// <summary>Top mods by exclusive ms in the frame that just ended (allocates; spikes only).</summary>
        public static List<int> LastTopMods(int n)
        {
            var list = new List<int>(lastTouchedModCount);
            for (int k = 0; k < lastTouchedModCount; k++)
            {
                list.Add(lastTouchedMods[k]);
            }
            list.Sort((a, b) => LastModMs[b].CompareTo(LastModMs[a]));
            if (list.Count > n)
            {
                list.RemoveRange(n, list.Count - n);
            }
            return list;
        }

        public static void ResetWindow()
        {
            for (int s = 0; s < SlotCount; s++)
            {
                WinCalls[s] = 0;
                WinTicks[s] = 0;
                WinMaxCall[s] = 0;
                WinAlloc[s] = 0;
            }
            WinFrames = 0;
        }

        public static string ModsText(int s)
        {
            var mods = Mods[s];
            if (mods.Length == 1)
            {
                return ModId[mods[0]];
            }
            var names = new string[mods.Length];
            for (int i = 0; i < mods.Length; i++)
            {
                names[i] = ModId[mods[i]];
            }
            return string.Join("+", names);
        }

        /// <summary>
        /// Measures the cost of one timed call (Enter + Exit) on a scratch slot, in microseconds.
        /// Used to estimate Performance Analyzer's timing overhead per frame. Main thread, once per pass.
        /// </summary>
        public static double CalibrateMicroseconds()
        {
            int s = AddSlot("(Performance Analyzer calibration)", "calibration", new int[0], null);
            if (s < 0)
            {
                return 0;
            }
            int touchedBefore = touchedCount;
            const int n = 20000;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                Enter(s, out var st);
                Exit(ref st);
            }
            long dt = Stopwatch.GetTimestamp() - t0;
            // Discard the calibration slot's data (it was the only slot touched by the loop).
            touchedCount = touchedBefore;
            frameCalls[s] = 0;
            frameTicks[s] = 0;
            frameExcl[s] = 0;
            frameAlloc[s] = 0;
            frameAllocExcl[s] = 0;
            Skipped[s] = true;
            return dt * TickToMs * 1000.0 / n;
        }
    }

    /// <summary>
    /// The timing patch added to every timed method: a prefix that starts the clock and a finalizer that stops it.
    /// It finds the method's slot by the method handle Harmony passes in; it reads nothing and changes nothing.
    /// </summary>
    public static class TimingPatch
    {
        public static void Prefix(MethodBase __originalMethod, out TimingState __state)
        {
            if (!Timing.IsMain || !Timing.ByHandle.TryGetValue(__originalMethod.MethodHandle.Value, out int s))
            {
                __state = default;
                return;
            }
            Timing.Enter(s, out __state);
        }

        public static void Finalizer(TimingState __state)
        {
            Timing.Exit(ref __state);
        }
    }
}
