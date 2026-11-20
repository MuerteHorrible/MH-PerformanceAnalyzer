using System.Collections.Generic;
using System.Text;

namespace MHPerformanceAnalyzer
{
    /// <summary>Shared Markdown helpers for summary.md and ai-report.md. Fixed formats, invariant numbers.</summary>
    internal static class Md
    {
        public static string F(double v, int digits) => v.ToString("F" + digits, Text.Inv);

        public static string Esc(string? s) => (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

        public static void Table(StringBuilder sb, string[] header, List<string[]> rows)
        {
            sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n|");
            foreach (var _ in header) sb.Append("---|");
            sb.Append('\n');
            foreach (var row in rows) sb.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
            sb.Append('\n');
        }

        public static void FrameTable(StringBuilder sb, ReportData r)
        {
            var rows = new List<string[]>();
            foreach (var f in r.frameTimes)
            {
                rows.Add(f.frames == 0
                    ? new[] { f.scope, "0", "", "", "", "", "", "", "", "", "" }
                    : new[] { f.scope, f.frames.ToString(Text.Inv), F(f.avgMs, 2), F(f.p50Ms, 2), F(f.p95Ms, 2), F(f.p99Ms, 2), F(f.p999Ms, 2), F(f.maxMs, 2), F(f.avgFps, 1), f.over33Ms.ToString(Text.Inv), f.over100Ms.ToString(Text.Inv) });
            }
            Table(sb, new[] { "scope", "frames", "avg ms", "p50", "p95", "p99", "p99.9", "max", "avg fps", "frames > 33.3 ms", "frames > 100 ms" }, rows);
        }

        public static void SpikeTable(StringBuilder sb, List<ReportData.SpikeRow> spikes)
        {
            var rows = new List<string[]>();
            foreach (var s in spikes)
            {
                rows.Add(new[] { F(s.t, 1), F(s.ms, 1), s.state, s.timersMatch ? F(s.cpuMainMs, 1) : "?", s.timersMatch ? F(s.cpuRenderMs, 1) : "?", s.timersMatch ? F(s.gpuMs, 1) : "?", F(s.gcMs, 1), Esc(s.topMod.Length > 0 ? s.topMod + " " + F(s.topModMs, 2) : "-"), Esc(s.camera), s.cars.ToString(Text.Inv) });
            }
            Table(sb, new[] { "t s", "ms", "state", "CPU main", "render", "GPU", "GC ms", "top mod (ms)", "camera", "cars" }, rows);
        }

        public static void ModTable(StringBuilder sb, ReportData r, int max)
        {
            var rows = new List<string[]>();
            for (int i = 0; i < r.mods.Count && i < max; i++)
            {
                var m = r.mods[i];
                rows.Add(new[] { Esc(m.mod), F(m.avgMsPerFrame, 3), F(m.sharePercent, 2) + "%", F(m.maxFrameMs, 2), m.playSpikesOver1Ms + " of " + r.spikes.play, F(m.maxSpikeFrameMs, 2), F(m.allocKBPerFrameLowerBound, 2) });
            }
            Table(sb, new[] { "mod", "avg ms/frame", "share of frame time", "max ms in one frame", "play spikes with > 1 ms", "max ms in a spike frame", "alloc KB/frame (lower bound)" }, rows);
        }

        public static void MethodTable(StringBuilder sb, List<ReportData.MethodRow> methods)
        {
            var rows = new List<string[]>();
            foreach (var m in methods)
            {
                rows.Add(new[] { Esc(m.method) + (m.timingStopped ? " (timing stopped)" : ""), m.kind, Esc(m.mods), m.calls.ToString(Text.Inv), F(m.callsPerFrame, 3), F(m.totalMs, 1), F(m.avgUs, 1), F(m.maxCallMs, 2), F(m.maxFrameMs, 2) });
            }
            Table(sb, new[] { "method", "kind", "mods", "calls", "calls/frame", "total ms", "avg us", "max call ms", "max ms in one frame" }, rows);
        }

        public static void FindingList(StringBuilder sb, ReportData r)
        {
            if (r.findings.Count == 0)
            {
                sb.Append("- Nothing stands out.\n\n");
                return;
            }
            foreach (var f in r.findings)
            {
                sb.Append("- **").Append(f.level).Append("**: ").Append(f.text).Append('\n');
            }
            sb.Append('\n');
        }

        /// <summary>Every loaded mod with version and type, so mods that were not timed (content, quiet Relay) show too.</summary>
        public static void LoadedModsSection(StringBuilder sb, ReportData r)
        {
            if (r.loadedMods.Count == 0)
            {
                return;
            }
            var parts = new List<string>();
            int builtin = 0;
            foreach (var m in r.loadedMods)
            {
                if (m.builtin)
                {
                    builtin++;
                    continue;
                }
                parts.Add(Esc(m.id) + " " + (m.version.Length > 0 ? Esc(m.version) : "?") + " (" + string.Join("+", m.types) + ")");
            }
            sb.Append("## Mods loaded\n\n");
            sb.Append(parts.Count > 0 ? string.Join(", ", parts) : "No mods besides the game's own");
            sb.Append(builtin > 0 ? $". Plus {builtin} packs built into the game (rr/).\n\n" : ".\n\n");
        }

        public static void SettingsSection(StringBuilder sb, ReportData r)
        {
            if (r.gameSettings.Count == 0)
            {
                return;
            }
            sb.Append("## Game settings\n\n");
            var parts = new List<string>();
            foreach (var s in r.gameSettings) parts.Add(s.name + " " + s.value);
            sb.Append(string.Join(" | ", parts)).Append("\n\n");
        }

        public static string SessionLine(ReportData r) =>
            $"{r.session.name} | map {r.session.map} | {F(r.session.durationMinutes, 1)} min | Performance Analyzer {r.analyzerVersion} | game {r.system.gameVersion}" +
            (r.session.label.Length > 0 ? " | label: " + Esc(r.session.label) : "");

        public static string SystemLine(ReportData r) =>
            $"{r.system.cpuThreads} CPU threads, {r.system.gpuMemoryMB} MB video memory ({r.system.graphicsApi}), {r.system.ramMB} MB RAM, " +
            $"{F(r.system.refreshHz, 0)} Hz, quality {r.system.quality}, vSync {r.system.vSyncCount}, frame cap {(r.system.targetFrameRate > 0 ? r.system.targetFrameRate.ToString(Text.Inv) : "none")}, " +
            $"game HTTP Server {(r.system.httpServerEnabled ? "on" : "off")}";
    }

    /// <summary>summary.md: the human-readable report, built only from report.json's data.</summary>
    internal static class SummaryMarkdown
    {
        public static string Render(ReportData r)
        {
            var sb = new StringBuilder(8192);
            sb.Append("# MH Performance Analyzer session summary\n\n");
            sb.Append("- ").Append(Md.SessionLine(r)).Append('\n');
            sb.Append("- ").Append(Md.SystemLine(r)).Append('\n');
            sb.Append("- Written ").Append(r.session.writtenLocal).Append(r.session.final ? " (final: recording stopped)" : " (while recording, every 60 s)").Append('\n');
            if (r.session.startedInLoadedWorld)
            {
                sb.Append("- Recording was started in a world that had already loaded, so steady play counts from the start.\n");
            }
            sb.Append('\n');

            sb.Append("## Findings\n\n");
            Md.FindingList(sb, r);

            Md.SettingsSection(sb, r);

            sb.Append("## Frame times\n\n");
            Md.FrameTable(sb, r);
            sb.Append("Steady play: in the world, not loading, from 60 s after the world loads. Compare sessions on that row. Percentiles come from 0.25 ms buckets.\n\n");

            var sp = r.spikes;
            sb.Append("## Spikes\n\n");
            sb.Append($"- Rule: a frame longer than {Md.F(sp.thresholdMs, 1)} ms, or {Md.F(sp.multiplier, 1)} times the recent median frame (median capped at {Md.F(sp.medianCapMs, 0)} ms), whichever is larger. Frames over the rule within {Md.F(sp.cooldownMs, 0)} ms count as one hitch; the worst frame is kept ({sp.mergedFrames} frames merged).\n");
            sb.Append($"- Play spikes: {sp.play} ({Md.F(sp.playPerMinute, 1)} per min), with a garbage collection {sp.playWithGc}, during a world save {sp.playDuringSave}.\n");
            sb.Append($"- All spikes: {sp.total} (loading {sp.loading}, saving {sp.saving}, world teardown {sp.unload}, Performance Analyzer instrumentation {sp.analyzer}).\n");
            sb.Append($"- Timer alignment: the game's CPU/GPU frame timers report {(sp.timerLagKnown ? sp.timerLagFrames.ToString(Text.Inv) : "?")} frames late (matched in {sp.timerMatchedSeconds} s, mean mismatch {Md.F(sp.timerMismatchMs, 2)} ms). Spikes marked ? in the tables had no match; their CPU/GPU values are not shown.\n");
            sb.Append("- Two sessions' spike counts differ beyond chance only if the gap is larger than about 2 x the square root of both counts added together.\n\n");
            if (r.worstSpikes.Count > 0)
            {
                sb.Append("Worst spikes:\n\n");
                Md.SpikeTable(sb, r.worstSpikes);
            }

            var v = r.view;
            sb.Append("## View\n\n");
            if (v.seconds == 0)
            {
                sb.Append("No play seconds recorded.\n\n");
            }
            else
            {
                sb.Append($"- View stayed the same: {(v.stable ? "yes" : "no")} ({v.modes}; moved up to {Md.F(v.maxMoveM, 1)} m, turned {Md.F(v.yawRangeDeg, 1)} degrees, tilted {Md.F(v.pitchRangeDeg, 1)} degrees{(v.hasZoom ? $", zoom {Md.F(v.zoomMinM, 1)}-{Md.F(v.zoomMaxM, 1)} m" : "")}).\n");
                sb.Append($"- First view: {v.firstMode} at {Md.F(v.firstX, 0)}, {Md.F(v.firstY, 0)}, {Md.F(v.firstZ, 0)}, yaw {Md.F(v.firstYaw, 0)}, pitch {Md.F(v.firstPitch, 0)}, field of view {Md.F(v.firstFov, 0)}{(v.hasZoom ? $", zoom {Md.F(v.firstZoomM, 1)} m" : "")}.\n");
                sb.Append($"- Draw calls per frame: average {Md.F(v.drawCallsAvg, 0)}, per-second range {v.drawCallsMin}-{v.drawCallsMax}. For an A/B comparison these should match between sessions.\n\n");
            }

            sb.Append("## Garbage collection, saves and memory\n\n");
            sb.Append($"- Garbage collection: {r.gc.collections} collections ({Md.F(r.gc.perMinute, 1)} per min), {Md.F(r.gc.totalSeconds, 2)} s in total, incremental {(r.gc.incremental ? "on" : "off")}.\n");
            sb.Append($"- World saves: {r.saves.count}" + (r.saves.count > 0 ? $", main thread up to {Md.F(r.saves.maxMs, 1)} ms" : "") + ".\n");
            sb.Append($"- Memory now: total used {Md.F(r.memory.totalUsedMB, 0)} MB, system used {Md.F(r.memory.systemUsedMB, 0)} MB, GC used {Md.F(r.memory.gcUsedMB, 0)} MB.\n");
            sb.Append(r.memory.growthKnown
                ? $"- Memory growth (from 3 min after the world load): total {Md.F(r.memory.growthTotalMBPerMin, 1)}, GC {Md.F(r.memory.growthGcMBPerMin, 1)}, system {Md.F(r.memory.growthSystemMBPerMin, 1)} MB per min.\n\n"
                : "- Memory growth: not enough data (needs 5 min in the world).\n\n");

            var t = r.timing;
            sb.Append("## Per-mod timing (main thread)\n\n");
            if (!t.enabled)
            {
                sb.Append("Off in this session.\n\n");
            }
            else if (!t.started)
            {
                sb.Append("Not started (it starts 2 s after a world has loaded).\n\n");
            }
            else
            {
                sb.Append($"- Timed: {t.methods} methods ({t.harmony} game methods patched by mods, {t.update} mod Update methods, {t.coroutine} mod coroutines, {t.asyncMethods} mod async methods) and Relay scripts ({t.relay}).\n");
                sb.Append("- A game method patched by mods is timed as a whole and listed under every mod that patches it, so per-mod figures are an upper bound. Rendering work a mod adds is not visible here; compare a session without that mod.\n");
                sb.Append("- Allocation figures per mod are lower bounds and are not comparable between sessions; compare garbage collections per minute instead.\n\n");
                Md.ModTable(sb, r, 30);
                sb.Append("Top methods by total time:\n\n");
                Md.MethodTable(sb, r.methodsByTotal);
                sb.Append("Top methods by worst single frame:\n\n");
                Md.MethodTable(sb, r.methodsByWorstFrame);
                if (t.dllModsNothingToTime.Count > 0 || t.relayModsQuiet.Count > 0)
                {
                    sb.Append("Not instrumented: ");
                    if (t.dllModsNothingToTime.Count > 0) sb.Append("DLL mods with nothing to time: ").Append(string.Join(", ", t.dllModsNothingToTime)).Append(". ");
                    if (t.relayModsQuiet.Count > 0) sb.Append("Relay mods with no script activity: ").Append(string.Join(", ", t.relayModsQuiet)).Append('.');
                    sb.Append("\n\n");
                }
            }

            sb.Append("## Performance Analyzer's own cost\n\n");
            sb.Append($"- Per-frame sample: {Md.F(r.overhead.sampleAvgUs, 1)} us on average. Per-mod timing adds well under 0.15 ms per frame (measured).\n");
            if (t.started)
            {
                sb.Append($"- Setting up the timing took {Md.F(t.passMsTotal, 0)} ms once (marked as a Performance Analyzer spike, not a play spike).\n");
            }
            sb.Append("\nFiles in this folder: report.html (charts), ai-report.md (for an AI assistant), report.json (all figures), frames.csv (one row per second), spikes.jsonl (one line per spike), mod-timing.csv (per method every 10 s), system.json, mods.json, session.json.\n");
            return sb.ToString();
        }
    }

    /// <summary>
    /// ai-report.md: a fixed prompt followed by a compact digest of report.json, so a player can paste one file
    /// into any AI assistant. Same report in, same text out.
    /// </summary>
    internal static class AiReport
    {
        /// <summary>For the person reading the answer. Also shown above the prompt in report.html.</summary>
        public const string Disclaimer =
            "Disclaimer: an AI assistant's answer is its own interpretation, not a finding of MH Performance Analyzer. " +
            "AI can make mistakes and can sound sure when it is wrong. Check its conclusions against the figures in this report " +
            "before you change your setup or report a problem to a mod author.";

        public const string Prompt =
            "This report comes from MH Performance Analyzer, a Railroader mod that records frame times and the main-thread time of the other mods. " +
            "Answer: (1) Is there a performance problem, and how big? (2) What most likely causes it: a specific mod (name it), the game itself, " +
            "rendering or the GPU, garbage collection, or the system? (3) How confident are you, and why? (4) The single most useful next test.\n\n" +
            "Rules for reading this report:\n" +
            "- The Findings section comes from fixed rules in Performance Analyzer. Check them against the numbers; do not ignore them.\n" +
            "- Only play spikes matter for mods. Loading, saving, world teardown and Performance Analyzer spikes are expected.\n" +
            "- Per-mod times are upper bounds: a game method patched by a mod is timed as a whole and listed under every mod that patches it. Mod code Performance Analyzer does not time (see Not instrumented) and rendering work a mod adds are not visible at all.\n" +
            "- Spike CPU/GPU values marked ? had no timer match; do not use them. A spike whose CPU main time is high but whose top mod time is small is game code or untimed code, not timed mod code.\n" +
            "- Allocation per mod is a lower bound; use garbage collections per minute for comparisons.\n" +
            "- One session cannot prove a mod is innocent of rendering cost or that the game is at fault. The proof is an A/B test: the same save, the view saved in that save, idle for 6 minutes without touching anything, " +
            "once with the mod and once without, restarting the game after changing the mod list. Spike counts then differ beyond chance only if the gap is larger than about 2 x the square root of both counts added together.\n" +
            "- Be specific and brief. Say clearly when the data is not enough.\n";

        public static string Render(ReportData r)
        {
            var sb = new StringBuilder(8192);
            sb.Append("# MH Performance Analyzer report for an AI assistant\n\n");
            sb.Append("> ").Append(Disclaimer).Append("\n\n");
            sb.Append("## Instructions\n\n").Append(Prompt).Append('\n');
            sb.Append("## Session\n\n");
            sb.Append("- ").Append(Md.SessionLine(r)).Append('\n');
            sb.Append("- ").Append(Md.SystemLine(r)).Append('\n');
            if (r.session.startedInLoadedWorld) sb.Append("- Recording started in a loaded world.\n");
            sb.Append($"- Per-mod timing: {(r.timing.started ? "on" : "off")}. Recording {(r.session.final ? "finished" : "still running")}.\n\n");

            Md.SettingsSection(sb, r);
            Md.LoadedModsSection(sb, r);
            sb.Append("## Findings (fixed rules)\n\n");
            Md.FindingList(sb, r);

            sb.Append("## Frame times\n\n");
            Md.FrameTable(sb, r);
            if (r.steady.frames > 0)
            {
                sb.Append($"Steady play averages: CPU main thread {Md.F(r.steady.cpuMainMs, 2)} ms, render thread {Md.F(r.steady.cpuRenderMs, 2)} ms, GPU {Md.F(r.steady.gpuMs, 2)} ms per frame.\n\n");
            }

            var sp = r.spikes;
            sb.Append("## Spikes\n\n");
            sb.Append($"- Threshold {Md.F(sp.thresholdMs, 1)} ms or {Md.F(sp.multiplier, 1)} x median; play spikes {sp.play} ({Md.F(sp.playPerMinute, 1)} per min), with GC {sp.playWithGc}, during saves {sp.playDuringSave}.\n");
            sb.Append($"- Play spikes with matching timers {sp.aligned}: main thread {sp.mainThread}, render thread {sp.renderThread}, GPU {sp.gpu}, outside the CPU threads {sp.outsideCpuThreads}; timed mod code a quarter or more of the spike in {sp.modCodeQuarter}.\n");
            sb.Append($"- Timer lag {(sp.timerLagKnown ? sp.timerLagFrames + " frames" : "unknown")}.\n\n");
            if (r.topPlaySpikes.Count > 0)
            {
                sb.Append("Largest play spikes:\n\n");
                Md.SpikeTable(sb, r.topPlaySpikes);
            }

            sb.Append("## Mods (main-thread timing, upper bounds)\n\n");
            if (r.timing.started)
            {
                Md.ModTable(sb, r, 20);
                sb.Append("Top methods by total time:\n\n");
                Md.MethodTable(sb, r.methodsByTotal.Count > 10 ? r.methodsByTotal.GetRange(0, 10) : r.methodsByTotal);
                sb.Append("Top methods by worst single frame:\n\n");
                Md.MethodTable(sb, r.methodsByWorstFrame);
                if (r.timing.dllModsNothingToTime.Count > 0) sb.Append("Not instrumented (DLL mods with nothing to time): ").Append(string.Join(", ", r.timing.dllModsNothingToTime)).Append(".\n\n");
            }
            else
            {
                sb.Append("Per-mod timing did not run in this session.\n\n");
            }

            sb.Append("## Other\n\n");
            sb.Append($"- Garbage collection {Md.F(r.gc.perMinute, 1)} per min, {Md.F(r.gc.totalSeconds, 2)} s in total. World saves {r.saves.count}, up to {Md.F(r.saves.maxMs, 1)} ms.\n");
            sb.Append(r.memory.growthKnown
                ? $"- Memory: total used {Md.F(r.memory.totalUsedMB, 0)} MB, growth {Md.F(r.memory.growthTotalMBPerMin, 1)} MB per min.\n"
                : $"- Memory: total used {Md.F(r.memory.totalUsedMB, 0)} MB, growth unknown (short session).\n");
            if (r.view.seconds > 0)
            {
                sb.Append($"- View stayed the same: {(r.view.stable ? "yes" : "no")}; draw calls per frame {Md.F(r.view.drawCallsAvg, 0)}.\n");
            }
            return sb.ToString();
        }
    }
}
