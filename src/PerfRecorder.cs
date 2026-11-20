using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Railroader.Modding;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Core sampler. OnFrame runs once per frame at the start of PlayerLoop.Initialization and reads
    /// the previous frame's values into preallocated buffers. Per-second rows, memory samples, spike
    /// records and the summary are produced here and handed to SessionWriter.
    /// </summary>
    internal sealed partial class PerfRecorder : IDisposable
    {
        private const int RingSize = 120;
        private const float MedianCapMs = 50f;
        private const int SaveWindowFrames = 3;
        private const double GrowthSettleSeconds = 180.0;
        private const double SteadySeconds = 60.0;
        private const double UnloadWindowSeconds = 2.0;
        private const string FramesHeader = "t_s,world,loading,frames,fps_avg,ms_avg,ms_p95,ms_p99,ms_max,cpu_main_ms,cpu_render_ms,gpu_ms,gc_ms,gc_collections,alloc_mb,mono_used_mb,gc_used_mb,gc_reserved_mb,total_used_mb,system_used_mb,spikes,draw_calls,batches,setpass,cam_mode,cam_x,cam_y,cam_z,cam_yaw,cam_pitch,cam_fov,cam_dist,cpu_total_max,cpu_main_max,cpu_render_max,play_s,steady\n";

        // Timer alignment (F1). The CPU/GPU frame timers report several frames late. Every frame's
        // reads are kept in a ring keyed by the frame that just finished; once a second the lag is
        // measured as the offset at which "CPU Total Frame Time" best matches the real frame ms.
        private const int TimerRingSize = 64;
        private const int MaxTimerLag = 20;
        private readonly int[] trFrame = new int[TimerRingSize];
        private readonly float[] trMs = new float[TimerRingSize];
        private readonly float[] trCpuTotal = new float[TimerRingSize];
        private readonly float[] trCpuMain = new float[TimerRingSize];
        private readonly float[] trCpuRender = new float[TimerRingSize];
        private readonly float[] trGpu = new float[TimerRingSize];
        private int trPos, trCount;
        private int timerLag = 6;
        private bool timerLagKnown;
        private float timerLagErr;
        private readonly int[] timerLagSeconds = new int[MaxTimerLag + 1];
        private int timerLagUnmatched;
        private float secCpuTotalMax, secCpuMainMax, secCpuRenderMax;
        private bool secLoadingAny;
        private bool loadingNow;
        private double unloadTime = double.MinValue;
        private readonly Histogram histSteady = new Histogram();
        // Last frames added to the play histograms, so the world teardown can be taken out again.
        private const int RecentSize = 512;
        private readonly float[] recentMs = new float[RecentSize];
        private readonly double[] recentTime = new double[RecentSize];
        private readonly byte[] recentFlags = new byte[RecentSize]; // 1 = in histWorld, 2 = in histSteady
        private int recentPos;
        private int spikesUnload, spikesMerged;
        private int writeErrorsLogged;
        private double nextWriteErrorLog;

        private readonly Config cfg;
        private readonly IModLogger log;
        private readonly SessionWriter writer;

        // Recorders (names verified in M1).
        private ProfilerRecorder rCpuMain, rCpuRender, rCpuTotal, rGpu, rGc;
        private ProfilerRecorder rDraw, rBatches, rSetPass;
        private ProfilerRecorder rGcUsed, rGcReserved, rTotalUsed, rSystemUsed;

        // Per-second accumulation.
        private readonly float[] secFrames = new float[8192];
        private readonly float[] sortBuf = new float[8192];
        private int secCount;
        private double secStart;
        private double secCpuMain, secCpuRender, secGpu, secGcMs;
        private long secAlloc;
        private int secGcCollections, secSpikes;
        private long secDraw, secBatches, secSetPass;

        // View during play, to tell whether two runs looked at the same scene.
        private int viewSeconds;
        private bool viewHasFirst;
        private GameContext.View viewFirst;
        private float viewMaxMove, viewYawMin, viewYawMax, viewPitchMin, viewPitchMax, viewFovMin = float.MaxValue, viewFovMax;
        private float viewDistMin = float.MaxValue, viewDistMax = -1f;
        private readonly int[] viewModeSeconds = new int[8];
        private double viewDrawSum;
        private long viewDrawMin = long.MaxValue, viewDrawMax;

        // Rolling median for the spike threshold.
        private readonly float[] ring = new float[RingSize];
        private readonly float[] ringSort = new float[RingSize];
        private int ringPos, ringCount;
        private float rollingMedian = 16.7f;

        // Session totals.
        private readonly Histogram histAll = new Histogram();
        private readonly Histogram histWorld = new Histogram();
        private readonly double sessionStart;
        private readonly DateTime sessionStartLocal = DateTime.Now;
        private long totalFrames;
        private int totalSpikes, spikesWithGc, spikesLoading, spikesSaving, spikesWorld;
        private int spikesPlay, spikesPlayGc, spikesPlaySaving;
        private double gcMsTotal;
        private long gcCollectionsTotal;
        private float lastGpuMs;
        private int prevGcCount;
        private long prevMono;
        private int lastSampleFrame = -1;

        // Pending spike (one hitch): opened by the first frame over the threshold, held for the
        // cooldown so a longer frame inside it replaces the candidate, then written once the
        // lagging timer values for the worst frame have arrived.
        private struct PendingSpike
        {
            public bool Active;
            public double StartNow;   // detection time of the first frame of the hitch
            public double Now;        // detection time of the worst frame
            public int Frame;         // worst frame
            public float Ms, Threshold, Median;
            public float GcMs;
            public int GcDelta;
            public long Alloc, Mono;
            public bool Loading, World, Busy, Unload;
            public int Merged;
            public bool TimersCaptured;
            public float CpuTotal, CpuMain, CpuRender, Gpu, TimerErr;
            public int TimerLagUsed;
            public long Draw, Batches, SetPass;
            public string TopMethodsJson, TopModsJson, TopModText;
            public double TrackedAllocKB;
            public List<KeyValuePair<int, double>>? ModMs;
        }

        private PendingSpike pending;

        // Every finished spike as a compact row (for the report's worst and top play spike tables), capped.
        private const int MaxSpikeRows = 5000;
        private readonly List<ReportData.SpikeRow> spikeRows = new List<ReportData.SpikeRow>();

        // Play spikes whose CPU/GPU values match the frame, by what took the time.
        private int playAligned, playMainThread, playRenderThread, playGpu, playOutside, playModQuarter;

        // Steady play CPU/GPU averages (per frame).
        private long steadyFrames;
        private double steadyCpuMainSum, steadyCpuRenderSum, steadyGpuSum;

        // Memory.
        private double nextMemSample;
        private long memMono, memGcUsed, memGcReserved, memTotalUsed, memSystemUsed;
        private readonly Regression growTotal = new Regression();
        private readonly Regression growGc = new Regression();
        private readonly Regression growSystem = new Regression();
        private double worldLoadTime = -1;

        // World state.
        private bool worldLoaded;
        private int worldLoads;
        private double nextSummary;

        // Own overhead.
        private long overheadTicks;
        private long overheadFrames;
        private double overheadMaxMs;
        private int failures;
        private bool disabled;

        private readonly HashSet<object> setScratch = new HashSet<object>();

        // Per-mod timing (M3).
        private const double TimingWindowSeconds = 10.0;
        private const double HotWarmupSeconds = 30.0;
        private const string ModTimingHeader = "t_s,kind,mods,method,calls,calls_per_frame,total_ms,avg_us,max_call_ms,alloc_kb\n";
        private Instrumenter? instr;
        private double instrumentAt = -1, rescanAt = -1, nextWindow = -1, hotCheckFrom = double.MaxValue;
        private int busyFrame = -10;
        private long timedFrames;
        private double timedFrameMs;
        private long timedFrameAlloc;
        private long timedFrameAllocFrames;
        private int spikesBusy;
        private long passTicksThisFrame;

        public int LastSampleFrame => lastSampleFrame;
        public bool Disabled => disabled;
        public int PlaySpikeCount => spikesPlay;

        /// <summary>The name the session folder gets when recording stops (shown in the final summary).</summary>
        public string? SessionName { get; set; }
        // Recording was started in a world that had already loaded and settled: nothing is left out at the start.
        private bool settled;

        public PerfRecorder(Config cfg, IModLogger log, SessionWriter writer)
        {
            this.cfg = cfg;
            this.log = log;
            this.writer = writer;
            sessionStart = Time.realtimeSinceStartupAsDouble;
            secStart = sessionStart;
            nextMemSample = sessionStart;
            nextSummary = sessionStart + 60.0;
            prevGcCount = GC.CollectionCount(0);
            prevMono = Profiler.GetMonoUsedSizeLong();

            rCpuMain = Start(ProfilerCategory.Render, "CPU Main Thread Frame Time");
            rCpuRender = Start(ProfilerCategory.Render, "CPU Render Thread Frame Time");
            rCpuTotal = Start(ProfilerCategory.Render, "CPU Total Frame Time");
            rGpu = Start(ProfilerCategory.Render, "GPU Frame Time");
            rGc = Start(new ProfilerCategory("GC"), "GC.Collect");
            if (!rGc.Valid)
            {
                rGc.Dispose();
                rGc = Start(ProfilerCategory.Scripts, "GC.Collect");
            }
            rDraw = Start(ProfilerCategory.Render, "Draw Calls Count");
            rBatches = Start(ProfilerCategory.Render, "Batches Count");
            rSetPass = Start(ProfilerCategory.Render, "SetPass Calls Count");
            rGcUsed = Start(ProfilerCategory.Memory, "GC Used Memory");
            rGcReserved = Start(ProfilerCategory.Memory, "GC Reserved Memory");
            rTotalUsed = Start(ProfilerCategory.Memory, "Total Used Memory");
            rSystemUsed = Start(ProfilerCategory.Memory, "System Used Memory");

            writer.Replace("frames.csv", FramesHeader);
        }

        /// <summary>Enables per-mod timing; instrumentation happens shortly after the first world load.</summary>
        public void EnableTiming(HarmonyLib.Harmony harmony)
        {
            instr = new Instrumenter(harmony, cfg);
            writer.Replace("mod-timing.csv", ModTimingHeader);
        }

        private static ProfilerRecorder Start(ProfilerCategory cat, string name)
        {
            try
            {
                return ProfilerRecorder.StartNew(cat, name);
            }
            catch (Exception)
            {
                return default;
            }
        }

        public void Dispose()
        {
            foreach (var r in new[] { rCpuMain, rCpuRender, rCpuTotal, rGpu, rGc, rDraw, rBatches, rSetPass, rGcUsed, rGcReserved, rTotalUsed, rSystemUsed })
            {
                try
                {
                    r.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        public string ValidRecorderNote()
        {
            var bad = new List<string>();
            if (!rCpuMain.Valid) bad.Add("CPU Main Thread Frame Time");
            if (!rCpuRender.Valid) bad.Add("CPU Render Thread Frame Time");
            if (!rGpu.Valid) bad.Add("GPU Frame Time");
            if (!rGc.Valid) bad.Add("GC.Collect");
            if (!rTotalUsed.Valid) bad.Add("Total Used Memory");
            return bad.Count == 0 ? "all recorders valid" : "invalid recorders: " + string.Join(", ", bad);
        }

        public void OnWorldLoad(bool alreadySettled)
        {
            settled = alreadySettled;
            worldLoaded = true;
            if (!alreadySettled)
            {
                worldLoads++;
            }
            worldLoadTime = Time.realtimeSinceStartupAsDouble;
            growTotal.Clear();
            growGc.Clear();
            growSystem.Clear();
            if (instr != null)
            {
                if (instr.Passes == 0)
                {
                    instrumentAt = worldLoadTime + 2.0;
                }
                else
                {
                    rescanAt = worldLoadTime + 10.0;
                }
            }
        }

        public void OnWorldUnload()
        {
            worldLoaded = false;
            unloadTime = Time.realtimeSinceStartupAsDouble;
            // The world teardown is not play: take the last frames out of the play histograms ...
            for (int i = 0; i < RecentSize; i++)
            {
                if (recentFlags[i] != 0 && recentTime[i] >= unloadTime - UnloadWindowSeconds)
                {
                    if ((recentFlags[i] & 1) != 0) histWorld.Remove(recentMs[i]);
                    if ((recentFlags[i] & 2) != 0) histSteady.Remove(recentMs[i]);
                    recentFlags[i] = 0;
                }
            }
            // ... and label a hitch that started just before the unload as "unload".
            if (pending.Active && unloadTime - pending.StartNow < UnloadWindowSeconds)
            {
                pending.Unload = true;
            }
            if (pending.Active)
            {
                FinishSpike();
            }
            RelayTiming.OnWorldUnload();
        }

        private static float Ms(ref ProfilerRecorder r)
        {
            if (!r.Valid)
            {
                return 0f;
            }
            long v = r.LastValue;
            return v <= 0 ? 0f : v * 1e-6f;
        }

        private static long Val(ref ProfilerRecorder r) => r.Valid ? r.LastValue : 0;

        /// <summary>PlayerLoop callback. Must not allocate on the normal path.</summary>
        public void OnFrame()
        {
            if (disabled)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                int frame = Time.frameCount;
                if (frame == lastSampleFrame)
                {
                    return;
                }
                bool first = lastSampleFrame < 0;
                lastSampleFrame = frame;
                if (instr != null)
                {
                    Timing.EndFrame();
                }
                double now = Time.realtimeSinceStartupAsDouble;
                if (first)
                {
                    // The first delta covers mod loading; skip it.
                    secStart = now;
                    return;
                }

                float ms = Time.unscaledDeltaTime * 1000f;
                float cpuMain = Ms(ref rCpuMain);
                float cpuRender = Ms(ref rCpuRender);
                float cpuTotal = Ms(ref rCpuTotal);
                float gpu = Ms(ref rGpu);
                if (gpu > 0f)
                {
                    lastGpuMs = gpu;
                }
                float gcMs = Ms(ref rGc);
                int gcCount = GC.CollectionCount(0);
                int gcDelta = gcCount - prevGcCount;
                prevGcCount = gcCount;
                long mono = Profiler.GetMonoUsedSizeLong();
                long alloc = gcDelta == 0 ? Math.Max(0, mono - prevMono) : -1;
                prevMono = mono;

                if (secCount < secFrames.Length)
                {
                    secFrames[secCount++] = ms;
                }
                secCpuMain += cpuMain;
                secCpuRender += cpuRender;
                secGpu += lastGpuMs;
                secGcMs += gcMs;
                secGcCollections += gcDelta;
                if (alloc > 0)
                {
                    secAlloc += alloc;
                }
                secDraw += Val(ref rDraw);
                secBatches += Val(ref rBatches);
                secSetPass += Val(ref rSetPass);

                // One loading test per frame, used for histograms, spikes and the CSV (F9).
                loadingNow = GameContext.IsLoading();
                if (loadingNow)
                {
                    secLoadingAny = true;
                }
                if (cpuTotal > secCpuTotalMax) secCpuTotalMax = cpuTotal;
                if (cpuMain > secCpuMainMax) secCpuMainMax = cpuMain;
                if (cpuRender > secCpuRenderMax) secCpuRenderMax = cpuRender;

                // Timer ring, keyed by the frame that just finished.
                trFrame[trPos] = frame - 1;
                trMs[trPos] = ms;
                trCpuTotal[trPos] = cpuTotal;
                trCpuMain[trPos] = cpuMain;
                trCpuRender[trPos] = cpuRender;
                trGpu[trPos] = gpu;
                trPos = (trPos + 1) % TimerRingSize;
                if (trCount < TimerRingSize)
                {
                    trCount++;
                }

                histAll.Add(ms);
                byte flags = 0;
                if (worldLoaded && !loadingNow)
                {
                    histWorld.Add(ms);
                    flags |= 1;
                    if (settled || now - worldLoadTime >= SteadySeconds)
                    {
                        histSteady.Add(ms);
                        flags |= 2;
                        steadyFrames++;
                        steadyCpuMainSum += cpuMain;
                        steadyCpuRenderSum += cpuRender;
                        steadyGpuSum += lastGpuMs;
                    }
                }
                recentMs[recentPos] = ms;
                recentTime[recentPos] = now;
                recentFlags[recentPos] = flags;
                recentPos = (recentPos + 1) % RecentSize;
                ring[ringPos] = ms;
                ringPos = (ringPos + 1) % RingSize;
                if (ringCount < RingSize)
                {
                    ringCount++;
                }
                totalFrames++;
                gcMsTotal += gcMs;
                gcCollectionsTotal += gcDelta;
                if (instr != null && instr.Passes > 0)
                {
                    timedFrames++;
                    timedFrameMs += ms;
                    if (alloc >= 0)
                    {
                        // Frames without a GC: total allocation, to compare with what timed calls account for.
                        timedFrameAlloc += alloc;
                        timedFrameAllocFrames++;
                    }
                }

                if (totalFrames > 60)
                {
                    float threshold = Math.Max(cfg.spikeThresholdMs, cfg.spikeMultiplier * Math.Min(rollingMedian, MedianCapMs));
                    bool inHitch = pending.Active && (now - pending.StartNow) * 1000.0 < cfg.spikeCooldownMs;
                    if (ms > threshold)
                    {
                        if (inHitch)
                        {
                            // Same hitch: keep the worst frame (F8); Performance Analyzer's own pass frame stays flagged.
                            pending.Merged++;
                            pending.Busy |= frame - 1 == busyFrame;
                            if (ms > pending.Ms)
                            {
                                SetCandidate(now, frame - 1, ms, threshold, gcMs, gcDelta, alloc, mono);
                            }
                        }
                        else
                        {
                            if (pending.Active)
                            {
                                FinishSpike();
                            }
                            pending = new PendingSpike { Active = true, StartNow = now };
                            SetCandidate(now, frame - 1, ms, threshold, gcMs, gcDelta, alloc, mono);
                        }
                    }
                }

                if (pending.Active)
                {
                    if (!pending.TimersCaptured && frame - 1 >= pending.Frame + timerLag)
                    {
                        CaptureTimers();
                    }
                    if (pending.TimersCaptured && (now - pending.StartNow) * 1000.0 >= cfg.spikeCooldownMs)
                    {
                        FinishSpike();
                    }
                }

                if (now - secStart >= 1.0)
                {
                    EndSecond(now);
                }
                if (now >= nextMemSample)
                {
                    SampleMemory(now);
                    nextMemSample = now + cfg.memorySampleSeconds;
                }
                if (now >= nextSummary)
                {
                    nextSummary = now + 60.0;
                    WriteSummary(false);
                }
                if (instr != null)
                {
                    TimingHousekeeping(now, frame);
                }
            }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1)
                {
                    log.Error(ex, "MH Performance Analyzer: frame sample failed (further failures counted silently).");
                }
                if (failures >= 100)
                {
                    disabled = true;
                    log.Error("MH Performance Analyzer: 100 sample failures, recording stopped for this session.");
                }
            }
            finally
            {
                // The one-off instrumentation pass is reported separately, not as per-frame overhead.
                long dt = Stopwatch.GetTimestamp() - t0 - passTicksThisFrame;
                passTicksThisFrame = 0;
                if (dt < 0)
                {
                    dt = 0;
                }
                overheadTicks += dt;
                overheadFrames++;
                double dms = dt * 1000.0 / Stopwatch.Frequency;
                if (dms > overheadMaxMs)
                {
                    overheadMaxMs = dms;
                }
            }
        }

        private void TimingHousekeeping(double now, int frame)
        {
            var ins = instr!;
            if (instrumentAt > 0 && now >= instrumentAt)
            {
                instrumentAt = -1;
                RunPass(frame, true);
                rescanAt = now + 10.0;
                nextWindow = now + TimingWindowSeconds;
                hotCheckFrom = now + HotWarmupSeconds;
                Timing.ResetWindow();
            }
            else if (rescanAt > 0 && now >= rescanAt)
            {
                rescanAt = -1;
                RunPass(frame, false);
            }

            if (ins.Passes > 0 && now >= nextWindow)
            {
                nextWindow = now + TimingWindowSeconds;
                WriteTimingWindow(now);
            }
        }

        private void RunPass(int frame, bool firstPass)
        {
            var ins = instr!;
            busyFrame = frame;
            long p0 = Stopwatch.GetTimestamp();
            try
            {
                int added = ins.Run();
                double passMs = (Stopwatch.GetTimestamp() - p0) * Timing.TickToMs;
                ins.PassMsTotal += passMs;
                if (passMs > ins.PassMsMax)
                {
                    ins.PassMsMax = passMs;
                }
                if (firstPass || added > 0)
                {
                    log.Info($"MH Performance Analyzer: timing {ins.Total} methods ({ins.HarmonyCount} Harmony-patched, {ins.UpdateCount} Update, {ins.CoroutineCount} coroutine, {ins.AsyncCount} async), Relay {(RelayTiming.Active ? "on" : "off")}, {ins.Failed.Count} failed, pass {passMs:0} ms.");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "MH Performance Analyzer: instrumentation pass failed.");
            }
            passTicksThisFrame += Stopwatch.GetTimestamp() - p0;
        }

        private void WriteTimingWindow(double now)
        {
            long frames = Math.Max(1, Timing.WinFrames);
            var sb = new StringBuilder(4096);
            string t = Text.F(now - sessionStart, "0.0");
            for (int s = 0; s < Timing.SlotCount; s++)
            {
                long calls = Timing.WinCalls[s];
                if (calls == 0)
                {
                    continue;
                }
                double totalMs = Timing.WinTicks[s] * Timing.TickToMs;
                double cpf = (double)calls / frames;
                sb.Append(t).Append(',')
                    .Append(Timing.Kind[s]).Append(',')
                    .Append(Csv(Timing.ModsText(s))).Append(',')
                    .Append(Csv(Timing.Label[s])).Append(',')
                    .Append(calls.ToString(Text.Inv)).Append(',')
                    .Append(Text.F(cpf, "0.###")).Append(',')
                    .Append(Text.F(totalMs, "0.000")).Append(',')
                    .Append(Text.F(totalMs * 1000.0 / calls, "0.0")).Append(',')
                    .Append(Text.F(Timing.WinMaxCall[s] * Timing.TickToMs, "0.000")).Append(',')
                    .Append(Text.F(Timing.WinAlloc[s] / 1024.0, "0.0")).Append('\n');

                // Cost control: stop timing methods that are called too often per frame.
                if (now >= hotCheckFrom && Timing.Method[s] != null && !Timing.Skipped[s] && cpf > cfg.maxCallsPerFrame)
                {
                    instr!.Unpatch(s);
                    string entry = Timing.Label[s] + " (" + Text.F(cpf, "0") + " calls/frame)";
                    instr.SkippedHot.Add(entry);
                    log.Info("MH Performance Analyzer: stopped timing " + entry + ", above maxCallsPerFrame " + cfg.maxCallsPerFrame + ".");
                }
            }
            if (sb.Length > 0)
            {
                writer.Append("mod-timing.csv", sb.ToString());
            }
            Timing.ResetWindow();
        }

        private static string Csv(string s) => s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private void EndSecond(double now)
        {
            int n = secCount;
            double span = now - secStart;
            if (n > 0)
            {
                Array.Copy(secFrames, sortBuf, n);
                Array.Sort(sortBuf, 0, n);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += sortBuf[i];
                }
                float p95 = sortBuf[(int)(0.95 * (n - 1))];
                float p99 = sortBuf[(int)(0.99 * (n - 1))];
                float max = sortBuf[n - 1];
                long draw = secDraw / n, batches = secBatches / n, setPass = secSetPass / n;
                var view = worldLoaded ? GameContext.CaptureView() : default;
                if (worldLoaded && !secLoadingAny && view.Valid)
                {
                    NoteView(view, draw);
                }

                var sb = new StringBuilder(320);
                sb.Append(Text.F(now - sessionStart, "0.0")).Append(',')
                    .Append(worldLoaded ? '1' : '0').Append(',')
                    .Append(secLoadingAny ? '1' : '0').Append(',')
                    .Append(n.ToString(Text.Inv)).Append(',')
                    .Append(Text.F(span > 0 ? n / span : 0, "0.0")).Append(',')
                    .Append(Text.F(sum / n)).Append(',')
                    .Append(Text.F(p95)).Append(',')
                    .Append(Text.F(p99)).Append(',')
                    .Append(Text.F(max)).Append(',')
                    .Append(Text.F(secCpuMain / n)).Append(',')
                    .Append(Text.F(secCpuRender / n)).Append(',')
                    .Append(Text.F(secGpu / n)).Append(',')
                    .Append(Text.F(secGcMs)).Append(',')
                    .Append(secGcCollections.ToString(Text.Inv)).Append(',')
                    .Append(Text.Mb(secAlloc)).Append(',')
                    .Append(Text.Mb(memMono)).Append(',')
                    .Append(Text.Mb(memGcUsed)).Append(',')
                    .Append(Text.Mb(memGcReserved)).Append(',')
                    .Append(Text.Mb(memTotalUsed)).Append(',')
                    .Append(Text.Mb(memSystemUsed)).Append(',')
                    .Append(secSpikes.ToString(Text.Inv)).Append(',')
                    .Append(draw.ToString(Text.Inv)).Append(',')
                    .Append(batches.ToString(Text.Inv)).Append(',')
                    .Append(setPass.ToString(Text.Inv)).Append(',');
                if (view.Valid)
                {
                    sb.Append(ModeName(view.Mode)).Append(',')
                        .Append(Text.F(view.Pos.x, "0.0")).Append(',')
                        .Append(Text.F(view.Pos.y, "0.0")).Append(',')
                        .Append(Text.F(view.Pos.z, "0.0")).Append(',')
                        .Append(Text.F(view.Yaw, "0.0")).Append(',')
                        .Append(Text.F(view.Pitch, "0.0")).Append(',')
                        .Append(Text.F(view.Fov, "0.0")).Append(',')
                        .Append(view.Distance >= 0 ? Text.F(view.Distance, "0.0") : "").Append(',');
                }
                else
                {
                    sb.Append(",,,,,,,,");
                }
                sb.Append(Text.F(secCpuTotalMax)).Append(',')
                    .Append(Text.F(secCpuMainMax)).Append(',')
                    .Append(Text.F(secCpuRenderMax)).Append(',')
                    .Append(worldLoaded && worldLoadTime >= 0 ? Text.F(now - worldLoadTime, "0.0") : "").Append(',')
                    .Append(worldLoaded && !secLoadingAny && (settled || now - worldLoadTime >= SteadySeconds) ? '1' : '0').Append('\n');
                writer.Append("frames.csv", sb.ToString());
            }

            // Rolling median over the last RingSize frames.
            if (ringCount > 0)
            {
                Array.Copy(ring, ringSort, ringCount);
                Array.Sort(ringSort, 0, ringCount);
                rollingMedian = ringSort[ringCount / 2];
            }

            secCount = 0;
            secDraw = secBatches = secSetPass = 0;
            secCpuMain = secCpuRender = secGpu = secGcMs = 0;
            secAlloc = 0;
            secGcCollections = 0;
            secSpikes = 0;
            secStart = now;
            secLoadingAny = false;
            secCpuTotalMax = secCpuMainMax = secCpuRenderMax = 0f;

            MeasureTimerLag();

            // Write errors: first one at once, then a count at most once a minute (F12).
            int errors = writer.ErrorCount;
            if (errors > writeErrorsLogged && (writeErrorsLogged == 0 || now >= nextWriteErrorLog))
            {
                log.Error(writeErrorsLogged == 0
                    ? "MH Performance Analyzer: log write failed: " + writer.LastError + " (failed writes are retried)."
                    : $"MH Performance Analyzer: {errors - writeErrorsLogged} more log write failures in the last minute; last: {writer.LastError}");
                writeErrorsLogged = errors;
                nextWriteErrorLog = now + 60.0;
            }
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case 0: return "FirstPerson";
                case 1: return "Strategy";
                case 2: return "Dispatcher";
                case 3: return "Fly";
                default: return "?";
            }
        }

        private const double ViewSettleSeconds = 30.0;

        /// <summary>Tracks the play view (from 30 s after the world load) so the summary can say whether it stayed put.</summary>
        private void NoteView(GameContext.View v, long draw)
        {
            if (worldLoadTime < 0 || (!settled && Time.realtimeSinceStartupAsDouble - worldLoadTime < ViewSettleSeconds))
            {
                return;
            }
            viewSeconds++;
            if (v.Mode >= 0 && v.Mode < viewModeSeconds.Length)
            {
                viewModeSeconds[v.Mode]++;
            }
            viewDrawSum += draw;
            if (draw < viewDrawMin) viewDrawMin = draw;
            if (draw > viewDrawMax) viewDrawMax = draw;
            if (!viewHasFirst)
            {
                viewHasFirst = true;
                viewFirst = v;
                viewYawMin = viewYawMax = 0f;
                viewPitchMin = viewPitchMax = v.Pitch;
            }
            float move = Vector3.Distance(v.Pos, viewFirst.Pos);
            if (move > viewMaxMove) viewMaxMove = move;
            float dYaw = Mathf.DeltaAngle(viewFirst.Yaw, v.Yaw);
            if (dYaw < viewYawMin) viewYawMin = dYaw;
            if (dYaw > viewYawMax) viewYawMax = dYaw;
            if (v.Pitch < viewPitchMin) viewPitchMin = v.Pitch;
            if (v.Pitch > viewPitchMax) viewPitchMax = v.Pitch;
            if (v.Fov < viewFovMin) viewFovMin = v.Fov;
            if (v.Fov > viewFovMax) viewFovMax = v.Fov;
            if (v.Distance >= 0)
            {
                if (v.Distance < viewDistMin) viewDistMin = v.Distance;
                if (v.Distance > viewDistMax) viewDistMax = v.Distance;
            }
        }

        private void SampleMemory(double now)
        {
            memMono = Profiler.GetMonoUsedSizeLong();
            memGcUsed = Val(ref rGcUsed);
            memGcReserved = Val(ref rGcReserved);
            memTotalUsed = Val(ref rTotalUsed);
            memSystemUsed = Val(ref rSystemUsed);
            // Memory settles for a few minutes after a world load; leave that out of the growth fit.
            if (worldLoaded && !loadingNow && worldLoadTime >= 0 && (settled || now - worldLoadTime >= GrowthSettleSeconds))
            {
                double minutes = (now - worldLoadTime) / 60.0;
                growTotal.Add(minutes, memTotalUsed / 1048576.0);
                growGc.Add(minutes, memGcUsed / 1048576.0);
                growSystem.Add(minutes, memSystemUsed / 1048576.0);
            }
        }

        /// <summary>Makes this frame the hitch's candidate (first frame, or a longer frame inside the cooldown).</summary>
        private void SetCandidate(double now, int frame, float ms, float threshold, float gcMs, int gcDelta, long alloc, long mono)
        {
            pending.Now = now;
            pending.Frame = frame;
            pending.Ms = ms;
            pending.Threshold = threshold;
            pending.Median = rollingMedian;
            pending.GcMs = gcMs;
            pending.GcDelta = gcDelta;
            pending.Alloc = alloc;
            pending.Mono = mono;
            pending.Loading = loadingNow;
            pending.World = worldLoaded;
            pending.Unload = !worldLoaded && now - unloadTime < UnloadWindowSeconds;
            pending.Busy |= frame == busyFrame;
            pending.Draw = Val(ref rDraw);
            pending.Batches = Val(ref rBatches);
            pending.SetPass = Val(ref rSetPass);
            pending.TimersCaptured = false;
            pending.TopMethodsJson = "[]";
            pending.TopModsJson = "[]";
            pending.TopModText = null!;
            pending.TrackedAllocKB = 0;
            pending.ModMs = null;
            if (instr != null && instr.Passes > 0)
            {
                // Per-mod timing of the frame that just ended (this frame) is valid until the next EndFrame.
                var sb = new StringBuilder(400);
                sb.Append('[');
                for (int i = 0; i < Timing.LastTopCount; i++)
                {
                    int s = Timing.LastTopSlot[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"method\":").Append(Text.Json(Timing.Label[s]))
                        .Append(",\"kind\":").Append(Text.Json(Timing.Kind[s]))
                        .Append(",\"mods\":").Append(Text.Json(Timing.ModsText(s)))
                        .Append(",\"ms\":").Append(Text.F(Timing.LastTopMs[i], "0.000"))
                        .Append(",\"allocKB\":").Append(Text.F(Timing.LastTopAlloc[i] / 1024.0, "0.0")).Append('}');
                }
                sb.Append(']');
                pending.TopMethodsJson = sb.ToString();
                sb.Clear().Append('[');
                var mods = Timing.LastTopMods(Timing.MaxMods);
                var modMs = new List<KeyValuePair<int, double>>(mods.Count);
                for (int i = 0; i < mods.Count; i++)
                {
                    int m = mods[i];
                    modMs.Add(new KeyValuePair<int, double>(m, Timing.LastModMs[m]));
                    if (i >= 5)
                    {
                        continue;
                    }
                    if (i > 0) sb.Append(',');
                    else pending.TopModText = Timing.ModId[m] + " " + Text.F(Timing.LastModMs[m], "0.00");
                    sb.Append("{\"mod\":").Append(Text.Json(Timing.ModId[m]))
                        .Append(",\"ms\":").Append(Text.F(Timing.LastModMs[m], "0.000"))
                        .Append(",\"allocKB\":").Append(Text.F(Timing.LastModAlloc[m] / 1024.0, "0.0")).Append('}');
                }
                sb.Append(']');
                pending.TopModsJson = sb.ToString();
                pending.ModMs = modMs;
                pending.TrackedAllocKB = Timing.LastFrameTrackedAlloc / 1024.0;
            }
            // If the timer lag is 0 the values are already in the ring.
            if (timerLag == 0)
            {
                CaptureTimers();
            }
        }

        /// <summary>Finds the timer reads that belong to the candidate frame: the ring entry timerLag frames later.</summary>
        private void CaptureTimers()
        {
            int want = pending.Frame + timerLag;
            int found = -1;
            for (int k = 0; k < trCount; k++)
            {
                int i = (trPos - 1 - k + TimerRingSize) % TimerRingSize;
                if (trFrame[i] == want)
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                // Fallback: the entry after the candidate whose CPU total is closest to the frame ms.
                float best = float.MaxValue;
                for (int k = 0; k < trCount; k++)
                {
                    int i = (trPos - 1 - k + TimerRingSize) % TimerRingSize;
                    int d = trFrame[i] - pending.Frame;
                    if (d < 0 || d > MaxTimerLag)
                    {
                        continue;
                    }
                    float err = Math.Abs(trCpuTotal[i] - pending.Ms);
                    if (err < best)
                    {
                        best = err;
                        found = i;
                    }
                }
            }
            if (found < 0)
            {
                pending.TimersCaptured = true;
                pending.TimerLagUsed = -1;
                pending.TimerErr = -1f;
                return;
            }
            pending.CpuTotal = trCpuTotal[found];
            pending.CpuMain = trCpuMain[found];
            pending.CpuRender = trCpuRender[found];
            // GPU time can read 0 on some frames; take the nearest non-zero value at or after the entry.
            float gpu = trGpu[found];
            for (int k = 1; gpu <= 0f && k < 4; k++)
            {
                int j = (found + k) % TimerRingSize;
                if (trFrame[j] == trFrame[found] + k)
                {
                    gpu = trGpu[j];
                }
            }
            pending.Gpu = gpu;
            pending.TimerLagUsed = trFrame[found] - pending.Frame;
            pending.TimerErr = Math.Abs(pending.CpuTotal - pending.Ms);
            pending.TimersCaptured = true;
        }

        /// <summary>Once a second: the lag at which CPU Total Frame Time best matches the real frame ms.</summary>
        private void MeasureTimerLag()
        {
            int bestLag = -1;
            double bestErr = double.MaxValue, secondErr = double.MaxValue;
            for (int lag = 0; lag <= MaxTimerLag; lag++)
            {
                double err = 0;
                int n = 0;
                for (int k = 0; k < trCount; k++)
                {
                    int i = (trPos - 1 - k + TimerRingSize) % TimerRingSize;
                    if (trCpuTotal[i] <= 0f)
                    {
                        continue;
                    }
                    int j = (i - lag + TimerRingSize) % TimerRingSize;
                    if (k + lag >= trCount || trFrame[j] != trFrame[i] - lag)
                    {
                        continue;
                    }
                    err += Math.Abs(trCpuTotal[i] - trMs[j]);
                    n++;
                }
                if (n < 20)
                {
                    continue;
                }
                double e = err / n;
                if (e < bestErr)
                {
                    secondErr = bestErr;
                    bestErr = e;
                    bestLag = lag;
                }
                else if (e < secondErr)
                {
                    secondErr = e;
                }
            }
            // Accept only a clear, distinct match: aligned values agree closely, and with very even
            // frame times every lag fits about equally, which says nothing about the lag.
            if (bestLag >= 0 && bestErr < 0.5 && bestErr < 0.5 * secondErr)
            {
                timerLag = bestLag;
                timerLagKnown = true;
                timerLagErr = (float)bestErr;
                timerLagSeconds[bestLag]++;
            }
            else
            {
                timerLagUnmatched++;
            }
        }

        private void FinishSpike()
        {
            if (!pending.TimersCaptured)
            {
                CaptureTimers();
            }
            var p = pending;
            pending = default;

            bool saving = SaveHook.Depth > 0 || Math.Abs(SaveHook.LastSaveFrame - p.Frame) <= SaveWindowFrames;
            bool gc = p.GcMs > 0f || p.GcDelta > 0;
            bool play = p.World && !p.Loading && !p.Busy && !p.Unload;
            if (p.Busy) spikesBusy++;
            if (p.Unload) spikesUnload++;
            spikesMerged += p.Merged;
            totalSpikes++;
            secSpikes++;
            if (gc) spikesWithGc++;
            if (p.Loading) spikesLoading++;
            if (saving) spikesSaving++;
            if (p.World) spikesWorld++;
            if (play)
            {
                spikesPlay++;
                if (gc) spikesPlayGc++;
                if (saving) spikesPlaySaving++;
                if (p.ModMs != null)
                {
                    Timing.NotePlaySpike(p.ModMs);
                }
            }

            var ctx = GameContext.Capture(p.World && worldLoaded, setScratch);
            double t = p.Now - sessionStart;
            string state = p.Busy ? "analyzer" : p.Unload ? "unload" : p.Loading ? "loading" : saving ? "saving" : p.World ? "play" : "menu";

            var sb = new StringBuilder(720);
            sb.Append("{\"t\":").Append(Text.F(t, "0.000"))
                .Append(",\"frame\":").Append(p.Frame.ToString(Text.Inv))
                .Append(",\"ms\":").Append(Text.F(p.Ms))
                .Append(",\"state\":").Append(Text.Json(state))
                .Append(",\"framesOverThresholdInHitch\":").Append((p.Merged + 1).ToString(Text.Inv))
                .Append(",\"threshold\":").Append(Text.F(p.Threshold))
                .Append(",\"median\":").Append(Text.F(p.Median))
                .Append(",\"cpuTotalMs\":").Append(Text.F(p.CpuTotal))
                .Append(",\"cpuMainMs\":").Append(Text.F(p.CpuMain))
                .Append(",\"cpuRenderMs\":").Append(Text.F(p.CpuRender))
                .Append(",\"gpuMs\":").Append(Text.F(p.Gpu))
                .Append(",\"timerLagFrames\":").Append(p.TimerLagUsed.ToString(Text.Inv))
                .Append(",\"timerMatchErrMs\":").Append(p.TimerErr >= 0 ? Text.F(p.TimerErr) : "null")
                .Append(",\"gcMs\":").Append(Text.F(p.GcMs))
                .Append(",\"gcCollections\":").Append(p.GcDelta.ToString(Text.Inv))
                .Append(",\"allocBytes\":").Append(p.Alloc >= 0 ? p.Alloc.ToString(Text.Inv) : "null")
                .Append(",\"monoUsedMB\":").Append(Text.Mb(p.Mono))
                .Append(",\"drawCalls\":").Append(p.Draw.ToString(Text.Inv))
                .Append(",\"batches\":").Append(p.Batches.ToString(Text.Inv))
                .Append(",\"setPass\":").Append(p.SetPass.ToString(Text.Inv))
                .Append(",\"timeScale\":").Append(Text.F(Time.timeScale, "0.##"))
                .Append(",\"world\":").Append(p.World ? "true" : "false")
                .Append(",\"loading\":").Append(p.Loading ? "true" : "false")
                .Append(",\"saving\":").Append(saving ? "true" : "false")
                .Append(",\"gameTime\":").Append(Text.Json(ctx.GameTime))
                .Append(",\"camera\":").Append(Text.Json(ctx.Camera))
                .Append(",\"cameraPos\":").Append(ctx.HasCameraPos ? "[" + Text.F(ctx.CameraPos.x, "0") + "," + Text.F(ctx.CameraPos.y, "0") + "," + Text.F(ctx.CameraPos.z, "0") + "]" : "null")
                .Append(",\"camYaw\":").Append(ctx.View.Valid ? Text.F(ctx.View.Yaw, "0.0") : "null")
                .Append(",\"camPitch\":").Append(ctx.View.Valid ? Text.F(ctx.View.Pitch, "0.0") : "null")
                .Append(",\"camFov\":").Append(ctx.View.Valid ? Text.F(ctx.View.Fov, "0.0") : "null")
                .Append(",\"camZoomDistance\":").Append(ctx.View.Valid && ctx.View.Distance >= 0 ? Text.F(ctx.View.Distance, "0.0") : "null")
                .Append(",\"cars\":").Append(ctx.Cars.ToString(Text.Inv))
                .Append(",\"locomotives\":").Append(ctx.Locomotives.ToString(Text.Inv))
                .Append(",\"trains\":").Append(ctx.Trains.ToString(Text.Inv))
                .Append(",\"analyzerBusy\":").Append(p.Busy ? "true" : "false")
                .Append(",\"timedAllocKB\":").Append(Text.F(p.TrackedAllocKB, "0.0"))
                .Append(",\"topMods\":").Append(p.TopModsJson ?? "[]")
                .Append(",\"topMethods\":").Append(p.TopMethodsJson ?? "[]").Append("}\n");
            writer.Append("spikes.jsonl", sb.ToString());

            bool timersMatch = p.TimerErr >= 0f && p.TimerErr <= Math.Max(1f, 0.05f * p.Ms);
            double topModMs = p.ModMs != null && p.ModMs.Count > 0 ? p.ModMs[0].Value : 0;
            if (play && timersMatch)
            {
                playAligned++;
                if (p.CpuMain >= 0.6f * p.Ms) playMainThread++;
                else if (p.CpuRender >= 0.6f * p.Ms) playRenderThread++;
                else if (p.Gpu >= 0.6f * p.Ms) playGpu++;
                else if (p.CpuTotal < 0.6f * p.Ms) playOutside++;
                if (topModMs >= 0.25 * p.Ms) playModQuarter++;
            }
            if (spikeRows.Count < MaxSpikeRows)
            {
                spikeRows.Add(new ReportData.SpikeRow
                {
                    t = R(t, 1), ms = R(p.Ms, 2), cpuMainMs = R(p.CpuMain, 2), cpuRenderMs = R(p.CpuRender, 2), gpuMs = R(p.Gpu, 2), gcMs = R(p.GcMs, 2),
                    timersMatch = timersMatch, state = state, camera = ctx.Camera ?? "", cars = ctx.Cars,
                    topMod = p.ModMs != null && p.ModMs.Count > 0 ? Timing.ModId[p.ModMs[0].Key] : "", topModMs = R(topModMs, 3),
                });
            }
        }

        private sealed class Regression
        {
            private int n;
            private double st, sy, sty, stt, tMin, tMax;

            public void Clear()
            {
                n = 0;
                st = sy = sty = stt = 0;
            }

            public void Add(double t, double y)
            {
                if (n == 0)
                {
                    tMin = t;
                }
                tMax = t;
                n++;
                st += t;
                sy += y;
                sty += t * y;
                stt += t * t;
            }

            /// <summary>MB per minute, or false when there are fewer than 10 samples over 2 minutes.</summary>
            public bool TrySlope(out double slope)
            {
                slope = 0;
                double d = n * stt - st * st;
                if (n < 10 || tMax - tMin < 2.0 || Math.Abs(d) < 1e-9)
                {
                    return false;
                }
                slope = (n * sty - st * sy) / d;
                return true;
            }
        }
    }
}
