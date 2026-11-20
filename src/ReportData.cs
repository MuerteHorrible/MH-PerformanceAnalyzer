using System;
using System.Collections.Generic;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// report.json: every summary figure of one session, in a fixed, versioned shape. summary.md, ai-report.md
    /// and the HTML reports are all built from this, so every reader sees the same numbers. Field names are part
    /// of the format: change them only together with SchemaVersion.
    /// </summary>
    [Serializable]
    internal sealed class ReportData
    {
        public const int CurrentSchema = 1;

        public int schemaVersion = CurrentSchema;
        public string analyzerVersion = "";
        public SessionInfo session = new SessionInfo();
        public SystemSummary system = new SystemSummary();
        public List<Setting> gameSettings = new List<Setting>();
        public List<LoadedMod> loadedMods = new List<LoadedMod>();
        public List<FrameRow> frameTimes = new List<FrameRow>();
        public SteadyCpuGpu steady = new SteadyCpuGpu();
        public SpikeSummary spikes = new SpikeSummary();
        public List<SpikeRow> worstSpikes = new List<SpikeRow>();
        public List<SpikeRow> topPlaySpikes = new List<SpikeRow>();
        public ViewSummary view = new ViewSummary();
        public SaveSummary saves = new SaveSummary();
        public GcSummary gc = new GcSummary();
        public MemorySummary memory = new MemorySummary();
        public TimingSummary timing = new TimingSummary();
        public List<ModRow> mods = new List<ModRow>();
        public List<MethodRow> methodsByTotal = new List<MethodRow>();
        public List<MethodRow> methodsByWorstFrame = new List<MethodRow>();
        public OverheadSummary overhead = new OverheadSummary();
        public List<Finding> findings = new List<Finding>();

        [Serializable]
        internal sealed class SessionInfo
        {
            public string name = "";
            public string label = "";
            public string map = "";
            public string startedLocal = "";
            public string writtenLocal = "";
            public bool final;
            public double durationMinutes;
            public long frames;
            public int worldLoads;
            public bool startedInLoadedWorld;
            public bool perModTiming;
        }

        [Serializable]
        internal sealed class SystemSummary
        {
            public string gameVersion = "";
            public string unityVersion = "";
            public int cpuThreads;
            public int gpuMemoryMB;
            public string graphicsApi = "";
            public int ramMB;
            public double refreshHz;
            public string quality = "";
            public int vSyncCount;
            public int targetFrameRate;
            public bool httpServerEnabled;
        }

        /// <summary>One loaded mod. types: dll, relay-main and/or content. builtin: shipped with the game (rr/).</summary>
        [Serializable]
        internal sealed class LoadedMod
        {
            public string id = "";
            public string name = "";
            public string version = "";
            public List<string> types = new List<string>();
            public bool builtin;
        }

        [Serializable]
        internal sealed class Setting
        {
            public string name = "";
            public string value = "";
        }

        [Serializable]
        internal sealed class FrameRow
        {
            public string scope = "";
            public long frames;
            public double avgMs, p50Ms, p95Ms, p99Ms, p999Ms, maxMs, avgFps;
            public long over33Ms, over100Ms;
        }

        [Serializable]
        internal sealed class SteadyCpuGpu
        {
            public long frames;
            public double cpuMainMs, cpuRenderMs, gpuMs;
        }

        [Serializable]
        internal sealed class SpikeSummary
        {
            public double thresholdMs, multiplier, cooldownMs, medianCapMs;
            public int play;
            public double playPerMinute;
            public int playWithGc, playDuringSave;
            public int total, inWorld, loading, withGc, saving, unload, analyzer, mergedFrames;
            public bool timerLagKnown;
            public int timerLagFrames;
            public double timerMismatchMs;
            public int timerMatchedSeconds, timerUnmatchedSeconds;
            // Play spikes whose CPU/GPU values match the frame, by what took the time.
            public int aligned, mainThread, renderThread, gpu, outsideCpuThreads, modCodeQuarter;
        }

        [Serializable]
        internal sealed class SpikeRow
        {
            public double t, ms, cpuMainMs, cpuRenderMs, gpuMs, gcMs;
            public bool timersMatch;
            public string state = "", camera = "", topMod = "";
            public double topModMs;
            public int cars;
        }

        [Serializable]
        internal sealed class ViewSummary
        {
            public int seconds;
            public bool stable;
            public string modes = "";
            public double maxMoveM, yawRangeDeg, pitchRangeDeg;
            public bool hasZoom;
            public double zoomMinM, zoomMaxM;
            public string firstMode = "";
            public double firstX, firstY, firstZ, firstYaw, firstPitch, firstFov, firstZoomM;
            public double drawCallsAvg;
            public long drawCallsMin, drawCallsMax;
        }

        [Serializable]
        internal sealed class SaveSummary
        {
            public int count;
            public double lastMs, maxMs;
        }

        [Serializable]
        internal sealed class GcSummary
        {
            public long collections;
            public double perMinute, totalSeconds;
            public bool incremental;
        }

        [Serializable]
        internal sealed class MemorySummary
        {
            public double totalUsedMB, systemUsedMB, gcUsedMB, gcReservedMB, monoUsedMB;
            public bool growthKnown;
            public double growthTotalMBPerMin, growthGcMBPerMin, growthSystemMBPerMin;
        }

        [Serializable]
        internal sealed class TimingSummary
        {
            public bool enabled, started;
            public int methods, harmony, update, coroutine, asyncMethods, passes;
            public string relay = "";
            public int relayFailures, excluded, generic;
            public List<string> failed = new List<string>();
            public List<string> stoppedHot = new List<string>();
            public double passMsTotal, passMsMax, callsPerFrame, hookCostUs;
            public int unbalanced;
            public long timedFrames;
            public double allocWholeKBPerFrame, allocTimedKBPerFrame;
            public long gcDuringCall;
            public List<string> dllModsNothingToTime = new List<string>();
            public List<string> relayModsQuiet = new List<string>();
        }

        [Serializable]
        internal sealed class ModRow
        {
            public string mod = "";
            public double totalSeconds, avgMsPerFrame, sharePercent, maxFrameMs;
            public int playSpikesOver1Ms;
            public double maxSpikeFrameMs, allocMBLowerBound, allocKBPerFrameLowerBound, maxAllocKBInFrame;
        }

        [Serializable]
        internal sealed class MethodRow
        {
            public string method = "", kind = "", mods = "";
            public bool timingStopped;
            public long calls;
            public double callsPerFrame, totalMs, avgUs, maxCallMs, maxFrameMs, allocKB;
        }

        [Serializable]
        internal sealed class OverheadSummary
        {
            public double sampleAvgUs, sampleMaxMs;
            public int failures;
            public bool disabled;
        }

        [Serializable]
        internal sealed class Finding
        {
            /// <summary>"suspect" (points at a likely cause), "warn" (worth a look) or "info".</summary>
            public string level = "";
            public string id = "";
            public string text = "";
        }
    }
}
