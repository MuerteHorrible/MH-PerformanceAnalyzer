using System;
using System.Collections.Generic;
using Game.Scripting;
using HarmonyLib;
using Relay;
using Relay.Coroutines;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Times Relay scripts per script module: event handlers (ScriptInstance.CallHandler) and coroutine
    /// steps (CoroutineProcessor.StepCoroutine). Module names: "&lt;pack&gt;:Main.rl", "asset:&lt;pack&gt;/&lt;path&gt;", "inline:&lt;name&gt;".
    /// </summary>
    internal static class RelayTiming
    {
        private const int MaxRelaySlots = 200;

        private static AccessTools.FieldRef<ScriptInstance, string>? componentName;
        private static AccessTools.FieldRef<ScriptInstance, Interpreter>? instanceInterpreter;
        private static AccessTools.FieldRef<object, CoroutineHandle>? scopedHandle;
        private static AccessTools.FieldRef<CoroutineHandle, Interpreter>? handleInterpreter;
        private static AccessTools.FieldRef<ScriptInstanceManager, List<ScriptInstance>>? managerInstances;

        private static readonly Dictionary<string, int> handlerSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> coroutineSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<Interpreter, int> interpreterSlots = new Dictionary<Interpreter, int>();
        private static int relaySlotCount;
        private static int otherHandlerSlot = -1, otherCoroutineSlot = -1;
        private static string[] packIds = new string[0];

        public static bool Active { get; private set; }
        public static string Status { get; private set; } = "not started";

        public static void Apply(Harmony harmony, IEnumerable<string> knownPackIds)
        {
            var ids = new List<string>(knownPackIds);
            ids.Sort((a, b) => b.Length.CompareTo(a.Length));
            packIds = ids.ToArray();
            try
            {
                componentName = AccessTools.FieldRefAccess<ScriptInstance, string>("_componentName");
                instanceInterpreter = AccessTools.FieldRefAccess<ScriptInstance, Interpreter>("_interpreter");
                var scopedType = AccessTools.Inner(typeof(CoroutineProcessor), "ScopedCoroutine");
                scopedHandle = AccessTools.FieldRefAccess<CoroutineHandle>(scopedType, "Handle");
                handleInterpreter = AccessTools.FieldRefAccess<CoroutineHandle, Interpreter>("<InterpreterInstance>k__BackingField");
                managerInstances = AccessTools.FieldRefAccess<ScriptInstanceManager, List<ScriptInstance>>("_scriptInstances");

                var callHandler = AccessTools.Method(typeof(ScriptInstance), nameof(ScriptInstance.CallHandler));
                var step = AccessTools.Method(typeof(CoroutineProcessor), "StepCoroutine");
                if (callHandler == null || step == null)
                {
                    Status = "not instrumented: dispatch methods not found";
                    return;
                }
                harmony.Patch(callHandler,
                    prefix: new HarmonyMethod(typeof(RelayTiming), nameof(HandlerPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(RelayTiming), nameof(HandlerFinalizer)) { priority = Priority.Last });
                harmony.Patch(step,
                    prefix: new HarmonyMethod(typeof(RelayTiming), nameof(StepPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(RelayTiming), nameof(StepFinalizer)) { priority = Priority.Last });
                Active = true;
                Status = "instrumented per script module (event handlers and coroutine steps)";
            }
            catch (Exception ex)
            {
                Status = "not instrumented: " + ex.Message;
                ModEntryPoint.Log?.Error(ex, "MH Performance Analyzer: Relay timing failed, Relay scripts are not instrumented.");
            }
        }

        /// <summary>Clears slot maps after a Timing.Reset (in-game mod reload); patches are reapplied by the next pass.</summary>
        public static void Reset()
        {
            handlerSlots.Clear();
            coroutineSlots.Clear();
            interpreterSlots.Clear();
            relaySlotCount = 0;
            otherHandlerSlot = -1;
            otherCoroutineSlot = -1;
            Active = false;
            Status = "not started";
            Failures = 0;
        }

        /// <summary>Drops references to the unloaded world's interpreters (F14).</summary>
        public static void OnWorldUnload() => interpreterSlots.Clear();

        /// <summary>Exceptions caught inside the Relay prefixes (should stay 0).</summary>
        public static int Failures { get; private set; }

        public static void HandlerPrefix(ScriptInstance __instance, out TimingState __state)
        {
            __state = default;
            if (!Timing.IsMain)
            {
                return;
            }
            try
            {
                string name = componentName!(__instance) ?? "unnamed";
                if (!handlerSlots.TryGetValue(name, out int s))
                {
                    s = NewSlot(name, "relay-handler", handlerSlots, ref otherHandlerSlot);
                }
                Timing.Enter(s, out __state);
            }
            catch (Exception)
            {
                Failures++;
                __state = default;
            }
        }

        public static void HandlerFinalizer(TimingState __state) => Timing.Exit(ref __state);

        public static void StepPrefix(object scopedCoroutine, out TimingState __state)
        {
            __state = default;
            if (!Timing.IsMain || scopedCoroutine == null)
            {
                return;
            }
            try
            {
                var handle = scopedHandle!(scopedCoroutine);
                var interp = handle != null ? handleInterpreter!(handle) : null;
                int s;
                if (interp == null)
                {
                    s = OtherSlot("relay-coroutine", ref otherCoroutineSlot);
                }
                else if (!interpreterSlots.TryGetValue(interp, out s))
                {
                    string name = FindModuleName(interp) ?? "unknown";
                    if (!coroutineSlots.TryGetValue(name, out s))
                    {
                        s = NewSlot(name, "relay-coroutine", coroutineSlots, ref otherCoroutineSlot);
                    }
                    if (interpreterSlots.Count > 4096)
                    {
                        interpreterSlots.Clear();
                    }
                    interpreterSlots[interp] = s;
                }
                Timing.Enter(s, out __state);
            }
            catch (Exception)
            {
                Failures++;
                __state = default;
            }
        }

        public static void StepFinalizer(TimingState __state) => Timing.Exit(ref __state);

        private static string? FindModuleName(Interpreter interp)
        {
            try
            {
                var list = managerInstances!(ScriptInstanceManager.Shared);
                if (list == null)
                {
                    return null;
                }
                foreach (var inst in list)
                {
                    if (inst != null && ReferenceEquals(instanceInterpreter!(inst), interp))
                    {
                        return componentName!(inst);
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static int NewSlot(string module, string kind, Dictionary<string, int> map, ref int otherSlot)
        {
            int s;
            if (relaySlotCount >= MaxRelaySlots)
            {
                s = OtherSlot(kind, ref otherSlot);
            }
            else
            {
                string mod = ModForModule(module);
                s = Timing.AddSlot(module, kind, new[] { Timing.ModIndex(mod) }, null);
                relaySlotCount++;
                if (s < 0)
                {
                    s = OtherSlot(kind, ref otherSlot);
                }
            }
            map[module] = s;
            return s;
        }

        private static int OtherSlot(string kind, ref int otherSlot)
        {
            if (otherSlot < 0)
            {
                otherSlot = Timing.AddSlot("(other relay scripts)", kind, new[] { Timing.ModIndex("relay:other") }, null);
            }
            return otherSlot;
        }

        /// <summary>Maps a Relay module name to the pack that owns it.</summary>
        internal static string ModForModule(string module)
        {
            if (module.StartsWith("inline:", StringComparison.Ordinal))
            {
                return "relay:inline";
            }
            string rest = module.StartsWith("asset:", StringComparison.Ordinal) ? module.Substring(6) : module;
            foreach (string id in packIds)
            {
                if (rest.Length > id.Length && rest.StartsWith(id, StringComparison.Ordinal) && (rest[id.Length] == '/' || rest[id.Length] == ':'))
                {
                    return id;
                }
            }
            int colon = rest.IndexOf(':');
            return colon > 0 ? rest.Substring(0, colon) : "relay:" + rest;
        }
    }
}
