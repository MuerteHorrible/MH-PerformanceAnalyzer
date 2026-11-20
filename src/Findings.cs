using System;
using System.Collections.Generic;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Plain, fixed rules that turn a report's numbers into short findings, so players get an answer without
    /// any AI and every reader starts from the same facts. Same report in, same findings out (same order, same
    /// wording). Thresholds are named constants below; change them here only.
    /// </summary>
    internal static class Findings
    {
        private const double ShortSessionMinutes = 3.0;
        private const double SpikesPerMinuteWarn = 2.0;
        private const double ShareMajority = 0.5;
        private const double ModSpikeShareSuspect = 0.10;
        private const int ModSpikeMinCount = 3;
        private const double ModMsPerFrameWarn = 0.5;
        private const double GcPerMinuteWarn = 30.0;
        private const double GcSpikeShareSuspect = 0.30;
        private const double SaveMsWarn = 50.0;
        private const double MemoryGrowthWarn = 20.0;
        private const double GpuBoundFactor = 1.2;

        public static List<ReportData.Finding> Evaluate(ReportData r)
        {
            var list = new List<ReportData.Finding>();
            var steadyRow = r.frameTimes.Count > 0 ? r.frameTimes[0] : null;
            var sp = r.spikes;

            // Data quality first.
            if (steadyRow == null || steadyRow.frames == 0)
            {
                Add(list, "warn", "no-steady", "No steady play was recorded (in the world, not loading). Record while playing for a few minutes.");
            }
            else if (steadyRow.frames > 0 && steadyRow.avgMs > 0 && steadyRow.frames * steadyRow.avgMs / 60000.0 < ShortSessionMinutes)
            {
                Add(list, "info", "short", $"Short session: {F(steadyRow.frames * steadyRow.avgMs / 60000.0, 1)} min of steady play. Results are more reliable from about {F(ShortSessionMinutes, 0)} min.");
            }
            if (!sp.timerLagKnown && sp.play > 0)
            {
                Add(list, "warn", "timers", "The game's CPU/GPU frame timers could not be lined up with the frames, so the CPU/GPU values of spikes are not reliable in this session.");
            }
            if (r.overhead.failures > 0)
            {
                Add(list, "warn", "failures", $"Performance Analyzer hit {r.overhead.failures} internal errors while sampling{(r.overhead.disabled ? " and stopped recording" : "")}. See Player.log.");
            }

            // How the game ran.
            if (steadyRow != null && steadyRow.frames > 0 && r.steady.frames > 0)
            {
                bool gpuBound = r.steady.gpuMs > r.steady.cpuMainMs * GpuBoundFactor;
                Add(list, "info", "fps",
                    $"Steady play: {F(steadyRow.avgMs, 2)} ms per frame ({F(steadyRow.avgFps, 0)} fps), 99% of frames under {F(steadyRow.p99Ms, 1)} ms. " +
                    $"Average CPU main thread {F(r.steady.cpuMainMs, 2)} ms, GPU {F(r.steady.gpuMs, 2)} ms: " +
                    (gpuBound ? "the graphics card sets the frame rate." : "the CPU sets the frame rate."));
            }

            // Spikes.
            if (sp.play > 0)
            {
                string rate = $"{sp.play} play spikes ({F(sp.playPerMinute, 1)} per min).";
                Add(list, sp.playPerMinute >= SpikesPerMinuteWarn ? "warn" : "info", "spike-rate", rate);
            }
            if (sp.aligned > 0)
            {
                double main = (double)sp.mainThread / sp.aligned;
                double render = (double)sp.renderThread / sp.aligned;
                double gpu = (double)sp.gpu / sp.aligned;
                double outside = (double)sp.outsideCpuThreads / sp.aligned;
                double mods = (double)sp.modCodeQuarter / sp.aligned;
                string counts = $"Of {sp.aligned} play spikes with matching timers: main thread {sp.mainThread}, render thread {sp.renderThread}, GPU {sp.gpu}, outside the CPU threads {sp.outsideCpuThreads}; timed mod code was a quarter or more of the spike in {sp.modCodeQuarter}.";
                if (mods >= ShareMajority)
                {
                    Add(list, "suspect", "spike-cause", counts + " Most spikes are mainly timed mod code: see the per-mod table.");
                }
                else if (main >= ShareMajority)
                {
                    Add(list, "suspect", "spike-cause", counts + " Most spikes are main-thread work that is not timed mod code: game code or mod code Performance Analyzer does not time (see Not instrumented). A test with mods switched off tells the two apart.");
                }
                else if (render >= ShareMajority)
                {
                    Add(list, "suspect", "spike-cause", counts + " Most spikes are render-thread work (draw submission, shaders or textures). Visual mods can add to this; a test without them tells.");
                }
                else if (gpu >= ShareMajority)
                {
                    Add(list, "suspect", "spike-cause", counts + " Most spikes are GPU work. Graphics settings or visual mods are the likely lever.");
                }
                else if (outside >= ShareMajority)
                {
                    Add(list, "suspect", "spike-cause", counts + " Most spikes are time outside both CPU threads and the GPU: driver, frame presentation or the operating system.");
                }
                else
                {
                    Add(list, "info", "spike-cause", counts + " No single cause dominates.");
                }
            }

            // Mods.
            foreach (var m in r.mods)
            {
                if (sp.play > 0 && m.playSpikesOver1Ms >= ModSpikeMinCount && (double)m.playSpikesOver1Ms / sp.play >= ModSpikeShareSuspect)
                {
                    Add(list, "suspect", "mod-spikes:" + m.mod,
                        $"{m.mod} spent more than 1 ms in {m.playSpikesOver1Ms} of {sp.play} play spikes ({F(100.0 * m.playSpikesOver1Ms / sp.play, 0)}%), up to {F(m.maxSpikeFrameMs, 1)} ms in a spike frame.");
                }
            }
            foreach (var m in r.mods)
            {
                if (m.avgMsPerFrame >= ModMsPerFrameWarn)
                {
                    Add(list, "warn", "mod-cost:" + m.mod,
                        $"{m.mod} takes {F(m.avgMsPerFrame, 2)} ms per frame on average ({F(m.sharePercent, 1)}% of frame time), up to {F(m.maxFrameMs, 1)} ms in one frame.");
                }
            }

            // Garbage collection.
            if (r.gc.perMinute >= GcPerMinuteWarn)
            {
                string top = "";
                ReportData.ModRow? biggest = null;
                foreach (var m in r.mods)
                {
                    if (biggest == null || m.allocMBLowerBound > biggest.allocMBLowerBound) biggest = m;
                }
                if (biggest != null && biggest.allocMBLowerBound > 0)
                {
                    top = $" The largest allocation seen in timed mod code is {biggest.mod} (at least {F(biggest.allocMBLowerBound, 0)} MB).";
                }
                Add(list, "warn", "gc", $"Garbage collection runs {F(r.gc.perMinute, 0)} times per min ({F(r.gc.totalSeconds, 1)} s in total).{top}");
            }
            if (sp.play >= ModSpikeMinCount && (double)sp.playWithGc / sp.play >= GcSpikeShareSuspect)
            {
                Add(list, "suspect", "gc-spikes", $"{sp.playWithGc} of {sp.play} play spikes had a garbage collection in the frame.");
            }

            // Saves and memory.
            if (r.saves.maxMs >= SaveMsWarn)
            {
                Add(list, "info", "saves", $"Saving the world took up to {F(r.saves.maxMs, 0)} ms on the main thread ({r.saves.count} saves).");
            }
            if (r.memory.growthKnown && r.memory.growthTotalMBPerMin >= MemoryGrowthWarn)
            {
                Add(list, "warn", "memory", $"Memory grew by about {F(r.memory.growthTotalMBPerMin, 0)} MB per min. A single jump after moving to another area is normal; steady growth over a long session is a leak.");
            }

            // Settings for the player to consider (they choose; Performance Analyzer never changes them).
            if (steadyRow != null && steadyRow.frames > 0 && r.steady.frames > 0 && r.gameSettings.Count > 0)
            {
                bool gpuBound = r.steady.gpuMs > r.steady.cpuMainMs * GpuBoundFactor;
                bool gpuSpikes = sp.aligned > 0 && (double)sp.gpu / sp.aligned >= ShareMajority;
                bool cpuSpikes = sp.aligned > 0 && (double)(sp.mainThread + sp.renderThread) / sp.aligned >= ShareMajority;
                if (gpuBound || gpuSpikes)
                {
                    Add(list, "info", "settings-gpu",
                        "To consider: the graphics card limits this game here. These settings mostly cost GPU time, and lowering them may raise fps: " +
                        Settings(r, "Render Scale", "Anti-aliasing", "Shadow Quality", "Ambient Occlusion", "Volumetric Clouds", "Volumetric Lighting") +
                        ", and screen resolution.");
                }
                if (cpuSpikes || !gpuBound)
                {
                    Add(list, "info", "settings-cpu",
                        "To consider: these settings add CPU work, most of all while the view moves and new scenery comes into sight, and lowering them may reduce spikes: " +
                        Settings(r, "Draw Distance", "Tree Density", "Grass Density", "Grass Distance", "Vegetation Richness", "Rolling Stock Detail", "Shadow Quality") + ".");
                }
            }

            if (r.system.httpServerEnabled)
            {
                Add(list, "info", "http-server", "The game's HTTP Server was on (Preferences). A tool connected to it, such as the throttle page left open in a browser, makes the game handle requests on the main thread, which counts as game code here.");
            }

            // What the timing could not see.
            if (!r.timing.enabled)
            {
                Add(list, "info", "timing-off", "Per-mod timing was off, so mods were not measured.");
            }
            if (r.timing.stoppedHot.Count > 0)
            {
                Add(list, "info", "hot", $"Timing was stopped for {r.timing.stoppedHot.Count} very frequently called methods: {string.Join("; ", r.timing.stoppedHot)}.");
            }
            if (r.timing.dllModsNothingToTime.Count > 0)
            {
                Add(list, "info", "not-timed", "DLL mods with nothing Performance Analyzer can time: " + string.Join(", ", r.timing.dllModsNothingToTime) + ".");
            }
            if (r.view.seconds > 0 && !r.view.stable)
            {
                Add(list, "info", "view", "The camera moved during the session. Fine for a play test; for an A/B comparison load a save with a fixed view and do not touch anything.");
            }
            return list;
        }

        /// <summary>"Render Scale 100%, Shadow Quality High" for the named settings that were read.</summary>
        private static string Settings(ReportData r, params string[] names)
        {
            var parts = new List<string>();
            foreach (string n in names)
            {
                string v = GameSettings.Value(r.gameSettings, n);
                if (v != "?") parts.Add(n + " " + v);
            }
            return string.Join(", ", parts);
        }

        private static void Add(List<ReportData.Finding> list, string level, string id, string text) =>
            list.Add(new ReportData.Finding { level = level, id = id, text = text });

        private static string F(double v, int digits) => v.ToString("F" + digits, Text.Inv);
    }
}
