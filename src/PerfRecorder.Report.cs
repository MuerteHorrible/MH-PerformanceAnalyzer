using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>Builds report.json from the recorder's figures and writes the text reports made from it.</summary>
    internal sealed partial class PerfRecorder
    {
        private const int TopPlaySpikes = 20;
        private const int WorstSpikes = 10;
        private const int TopMethodsByTotal = 15;
        private const int TopMethodsByWorst = 10;

        private static double R(double v, int digits) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, digits);

        /// <summary>
        /// Writes report.json and summary.md (every 60 s while recording, and at the end); at the end also
        /// ai-report.md. final=true also writes everything queued to disk at once.
        /// </summary>
        public void WriteSummary(bool final)
        {
            try
            {
                if (final && pending.Active)
                {
                    FinishSpike();
                }
                var report = BuildReport(final);
                report.findings = Findings.Evaluate(report);
                // Newtonsoft (shipped with the game), not JsonUtility: JsonUtility leaves out the nested sections
                // for types from a mod assembly. Field order follows ReportData, numbers are invariant.
                writer.Replace("report.json", JsonConvert.SerializeObject(report, Formatting.Indented) + "\n");
                writer.Replace("summary.md", SummaryMarkdown.Render(report));
                if (final)
                {
                    writer.Replace("ai-report.md", AiReport.Render(report));
                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "MH Performance Analyzer: report failed.");
            }
        }

        private ReportData BuildReport(bool final)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            var r = new ReportData { analyzerVersion = ModEntryPoint.Version };

            r.session.name = SessionName ?? writer.FolderName;
            r.session.label = cfg.sessionLabel;
            r.session.map = Recording.MapName();
            r.session.startedLocal = sessionStartLocal.ToString("yyyy-MM-dd HH:mm:ss", Text.Inv);
            r.session.writtenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Text.Inv);
            r.session.final = final;
            r.session.durationMinutes = R((now - sessionStart) / 60.0, 2);
            r.session.frames = totalFrames;
            r.session.worldLoads = worldLoads;
            r.session.startedInLoadedWorld = settled;
            r.session.perModTiming = instr != null;

            Inventory.FillSystem(r.system);
            r.gameSettings = GameSettings.Read();
            r.loadedMods = Inventory.LoadedMods();

            r.frameTimes.Add(FrameRowOf("steady play", histSteady));
            r.frameTimes.Add(FrameRowOf("play", histWorld));
            r.frameTimes.Add(FrameRowOf("whole session", histAll));
            if (steadyFrames > 0)
            {
                r.steady.frames = steadyFrames;
                r.steady.cpuMainMs = R(steadyCpuMainSum / steadyFrames, 2);
                r.steady.cpuRenderMs = R(steadyCpuRenderSum / steadyFrames, 2);
                r.steady.gpuMs = R(steadyGpuSum / steadyFrames, 2);
            }

            var sp = r.spikes;
            sp.thresholdMs = R(cfg.spikeThresholdMs, 1);
            sp.multiplier = R(cfg.spikeMultiplier, 1);
            sp.cooldownMs = R(cfg.spikeCooldownMs, 0);
            sp.medianCapMs = MedianCapMs;
            sp.play = spikesPlay;
            double playMinutes = histWorld.Sum / 60000.0;
            sp.playPerMinute = playMinutes > 0.05 ? R(spikesPlay / playMinutes, 2) : 0;
            sp.playWithGc = spikesPlayGc;
            sp.playDuringSave = spikesPlaySaving;
            sp.total = totalSpikes;
            sp.inWorld = spikesWorld;
            sp.loading = spikesLoading;
            sp.withGc = spikesWithGc;
            sp.saving = spikesSaving;
            sp.unload = spikesUnload;
            sp.analyzer = spikesBusy;
            sp.mergedFrames = spikesMerged;
            sp.timerLagKnown = timerLagKnown;
            sp.timerLagFrames = timerLag;
            sp.timerMismatchMs = R(timerLagErr, 2);
            foreach (int c in timerLagSeconds) sp.timerMatchedSeconds += c;
            sp.timerUnmatchedSeconds = timerLagUnmatched;
            sp.aligned = playAligned;
            sp.mainThread = playMainThread;
            sp.renderThread = playRenderThread;
            sp.gpu = playGpu;
            sp.outsideCpuThreads = playOutside;
            sp.modCodeQuarter = playModQuarter;

            // Largest first; equal values keep their recording order, so the tables are deterministic.
            var byMs = new List<ReportData.SpikeRow>(spikeRows);
            StableSortDescending(byMs, x => x.ms);
            for (int i = 0; i < byMs.Count && r.worstSpikes.Count < WorstSpikes; i++)
            {
                r.worstSpikes.Add(byMs[i]);
            }
            for (int i = 0; i < byMs.Count && r.topPlaySpikes.Count < TopPlaySpikes; i++)
            {
                if (byMs[i].state == "play")
                {
                    r.topPlaySpikes.Add(byMs[i]);
                }
            }

            FillView(r.view);

            r.saves.count = SaveHook.Count;
            r.saves.lastMs = R(SaveHook.LastMs, 1);
            r.saves.maxMs = R(SaveHook.MaxMs, 1);

            double minutes = (now - sessionStart) / 60.0;
            r.gc.collections = gcCollectionsTotal;
            r.gc.perMinute = minutes > 0.02 ? R(gcCollectionsTotal / minutes, 1) : 0;
            r.gc.totalSeconds = R(gcMsTotal / 1000.0, 2);
            r.gc.incremental = UnityEngine.Scripting.GarbageCollector.isIncremental;

            var mem = r.memory;
            mem.totalUsedMB = R(memTotalUsed / 1048576.0, 1);
            mem.systemUsedMB = R(memSystemUsed / 1048576.0, 1);
            mem.gcUsedMB = R(memGcUsed / 1048576.0, 1);
            mem.gcReservedMB = R(memGcReserved / 1048576.0, 1);
            mem.monoUsedMB = R(memMono / 1048576.0, 1);
            if (growTotal.TrySlope(out double gt) && growGc.TrySlope(out double gg) && growSystem.TrySlope(out double gs))
            {
                mem.growthKnown = true;
                mem.growthTotalMBPerMin = R(gt, 1);
                mem.growthGcMBPerMin = R(gg, 1);
                mem.growthSystemMBPerMin = R(gs, 1);
            }

            FillTiming(r);

            r.overhead.sampleAvgUs = overheadFrames == 0 ? 0 : R(overheadTicks * 1e6 / Stopwatch.Frequency / overheadFrames, 1);
            r.overhead.sampleMaxMs = R(overheadMaxMs, 2);
            r.overhead.failures = failures;
            r.overhead.disabled = disabled;
            return r;
        }

        private static ReportData.FrameRow FrameRowOf(string scope, Histogram h)
        {
            var row = new ReportData.FrameRow { scope = scope, frames = h.Count };
            if (h.Count == 0)
            {
                return row;
            }
            row.avgMs = R(h.Average, 2);
            row.p50Ms = R(h.Percentile(0.5), 2);
            row.p95Ms = R(h.Percentile(0.95), 2);
            row.p99Ms = R(h.Percentile(0.99), 2);
            row.p999Ms = R(h.Percentile(0.999), 2);
            row.maxMs = R(h.Max, 2);
            row.avgFps = h.Average > 0 ? R(1000.0 / h.Average, 1) : 0;
            row.over33Ms = h.CountAbove(33.25f);
            row.over100Ms = h.CountAbove(100f);
            return row;
        }

        private void FillView(ReportData.ViewSummary v)
        {
            v.seconds = viewSeconds;
            if (viewSeconds == 0)
            {
                return;
            }
            var modes = new List<string>();
            for (int i = 0; i < viewModeSeconds.Length; i++)
            {
                if (viewModeSeconds[i] > 0)
                {
                    modes.Add(ModeName(i) + " " + viewModeSeconds[i] + " s");
                }
            }
            float yawRange = viewYawMax - viewYawMin;
            float pitchRange = viewPitchMax - viewPitchMin;
            float distRange = viewDistMax >= 0 ? viewDistMax - viewDistMin : 0f;
            v.stable = modes.Count == 1 && viewMaxMove < 5f && yawRange < 5f && pitchRange < 5f && (viewDistMax < 0 || distRange < Math.Max(1f, 0.05f * viewDistMax));
            v.modes = string.Join(", ", modes);
            v.maxMoveM = R(viewMaxMove, 1);
            v.yawRangeDeg = R(yawRange, 1);
            v.pitchRangeDeg = R(pitchRange, 1);
            v.hasZoom = viewDistMax >= 0;
            v.zoomMinM = v.hasZoom ? R(viewDistMin, 1) : 0;
            v.zoomMaxM = v.hasZoom ? R(viewDistMax, 1) : 0;
            v.firstMode = ModeName(viewFirst.Mode);
            v.firstX = R(viewFirst.Pos.x, 0);
            v.firstY = R(viewFirst.Pos.y, 0);
            v.firstZ = R(viewFirst.Pos.z, 0);
            v.firstYaw = R(viewFirst.Yaw, 0);
            v.firstPitch = R(viewFirst.Pitch, 0);
            v.firstFov = R(viewFirst.Fov, 0);
            v.firstZoomM = viewFirst.Distance >= 0 ? R(viewFirst.Distance, 1) : 0;
            v.drawCallsAvg = R(viewDrawSum / viewSeconds, 0);
            v.drawCallsMin = viewDrawMin;
            v.drawCallsMax = viewDrawMax;
        }

        private void FillTiming(ReportData r)
        {
            var t = r.timing;
            var ins = instr;
            t.enabled = ins != null;
            if (ins == null || ins.Passes == 0)
            {
                return;
            }
            t.started = true;
            t.methods = ins.Total;
            t.harmony = ins.HarmonyCount;
            t.update = ins.UpdateCount;
            t.coroutine = ins.CoroutineCount;
            t.asyncMethods = ins.AsyncCount;
            t.passes = ins.Passes;
            t.relay = RelayTiming.Status;
            t.relayFailures = RelayTiming.Failures;
            t.excluded = ins.ExcludedCount;
            t.generic = ins.GenericSkipped;
            t.failed.AddRange(ins.Failed);
            t.stoppedHot.AddRange(ins.SkippedHot);
            t.passMsTotal = R(ins.PassMsTotal, 0);
            t.passMsMax = R(ins.PassMsMax, 0);
            double frames = Math.Max(1, timedFrames);
            long allCalls = 0;
            for (int s = 0; s < Timing.SlotCount; s++)
            {
                allCalls += Timing.TotCalls[s];
            }
            t.callsPerFrame = R(allCalls / frames, 2);
            t.hookCostUs = R(ins.CallCostUs, 3);
            t.unbalanced = Timing.Unbalanced;
            t.timedFrames = timedFrames;
            t.allocWholeKBPerFrame = timedFrameAllocFrames == 0 ? 0 : R(timedFrameAlloc / 1024.0 / timedFrameAllocFrames, 1);
            t.allocTimedKBPerFrame = R(Timing.TotTrackedAlloc / 1024.0 / frames, 1);
            t.gcDuringCall = Timing.GcDuringCall;

            var relaySeen = new HashSet<string>(StringComparer.Ordinal);
            for (int s = 0; s < Timing.SlotCount; s++)
            {
                if (Timing.Kind[s].StartsWith("relay", StringComparison.Ordinal) && Timing.TotCalls[s] > 0)
                {
                    foreach (int m in Timing.Mods[s]) relaySeen.Add(Timing.ModId[m]);
                }
            }
            foreach (var id in ins.DllMods)
            {
                if (id != "mh/performance-analyzer" && !ins.InstrumentedMods.Contains(id)) t.dllModsNothingToTime.Add(id);
            }
            foreach (var id in ins.RelayMainMods)
            {
                if (!relaySeen.Contains(id) && !id.StartsWith("rr/", StringComparison.Ordinal)) t.relayModsQuiet.Add(id);
            }
            t.dllModsNothingToTime.Sort(StringComparer.Ordinal);
            t.relayModsQuiet.Sort(StringComparer.Ordinal);

            var mods = new List<int>();
            for (int m = 0; m < Timing.ModCount; m++)
            {
                if (Timing.ModTotMs[m] > 0 || Timing.ModTotAlloc[m] > 0 || Timing.ModPlaySpikesOver1[m] > 0)
                {
                    mods.Add(m);
                }
            }
            mods.Sort((a, b) => Timing.ModTotMs[b] != Timing.ModTotMs[a] ? Timing.ModTotMs[b].CompareTo(Timing.ModTotMs[a]) : string.CompareOrdinal(Timing.ModId[a], Timing.ModId[b]));
            foreach (int m in mods)
            {
                r.mods.Add(new ReportData.ModRow
                {
                    mod = Timing.ModId[m],
                    totalSeconds = R(Timing.ModTotMs[m] / 1000.0, 3),
                    avgMsPerFrame = R(Timing.ModTotMs[m] / frames, 4),
                    sharePercent = timedFrameMs > 0 ? R(100.0 * Timing.ModTotMs[m] / timedFrameMs, 2) : 0,
                    maxFrameMs = R(Timing.ModMaxFrameMs[m], 2),
                    playSpikesOver1Ms = Timing.ModPlaySpikesOver1[m],
                    maxSpikeFrameMs = R(Timing.ModMaxSpikeMs[m], 2),
                    allocMBLowerBound = R(Timing.ModTotAlloc[m] / 1048576.0, 1),
                    allocKBPerFrameLowerBound = R(Timing.ModTotAlloc[m] / 1024.0 / frames, 2),
                    maxAllocKBInFrame = R(Timing.ModMaxFrameAlloc[m] / 1024.0, 1),
                });
            }

            var slots = new List<int>();
            for (int s = 0; s < Timing.SlotCount; s++)
            {
                if (Timing.TotCalls[s] > 0)
                {
                    slots.Add(s);
                }
            }
            slots.Sort((a, b) => Timing.TotTicks[b] != Timing.TotTicks[a] ? Timing.TotTicks[b].CompareTo(Timing.TotTicks[a]) : string.CompareOrdinal(Timing.Label[a], Timing.Label[b]));
            for (int i = 0; i < Math.Min(TopMethodsByTotal, slots.Count); i++) r.methodsByTotal.Add(MethodRowOf(slots[i], frames));
            slots.Sort((a, b) => Timing.MaxFrameTicks[b] != Timing.MaxFrameTicks[a] ? Timing.MaxFrameTicks[b].CompareTo(Timing.MaxFrameTicks[a]) : string.CompareOrdinal(Timing.Label[a], Timing.Label[b]));
            for (int i = 0; i < Math.Min(TopMethodsByWorst, slots.Count); i++) r.methodsByWorstFrame.Add(MethodRowOf(slots[i], frames));
        }

        private static ReportData.MethodRow MethodRowOf(int s, double frames)
        {
            long calls = Timing.TotCalls[s];
            double totalMs = Timing.TotTicks[s] * Timing.TickToMs;
            return new ReportData.MethodRow
            {
                method = Timing.Label[s],
                kind = Timing.Kind[s],
                mods = Timing.ModsText(s),
                timingStopped = Timing.Skipped[s],
                calls = calls,
                callsPerFrame = R(calls / frames, 3),
                totalMs = R(totalMs, 1),
                avgUs = calls > 0 ? R(totalMs * 1000.0 / calls, 1) : 0,
                maxCallMs = R(Timing.MaxCallTicks[s] * Timing.TickToMs, 2),
                maxFrameMs = R(Timing.MaxFrameTicks[s] * Timing.TickToMs, 2),
                allocKB = R(Timing.TotAlloc[s] / 1024.0, 1),
            };
        }

        /// <summary>Largest first; equal items keep their original order (List.Sort alone is not stable).</summary>
        private static void StableSortDescending<T>(List<T> list, Func<T, double> key)
        {
            var order = new List<int>(list.Count);
            for (int i = 0; i < list.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = key(list[b]).CompareTo(key(list[a]));
                return c != 0 ? c : a.CompareTo(b);
            });
            var sorted = new List<T>(list.Count);
            foreach (int i in order) sorted.Add(list[i]);
            list.Clear();
            list.AddRange(sorted);
        }
    }
}
