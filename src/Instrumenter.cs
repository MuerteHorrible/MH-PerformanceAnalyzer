using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Model.Database;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Finds what to time and adds the timing prefix/finalizer: methods other mods patch with Harmony,
    /// Update/LateUpdate/FixedUpdate of MonoBehaviours in mod DLLs, coroutine and async MoveNext in mod DLLs,
    /// and Relay dispatch. Runs on world load and again about 10 s later for late patchers.
    /// </summary>
    internal sealed class Instrumenter
    {
        public const string OwnHarmonyId = "mh.performance-analyzer";
        private const string OwnPack = "mh/performance-analyzer";

        private readonly Harmony harmony;
        private readonly Config cfg;
        private readonly HashSet<string> excludeOwners;
        private readonly HashSet<string> excludeMethods;
        private readonly HashSet<string> focusMods;
        private readonly MethodInfo prefix = AccessTools.Method(typeof(TimingPatch), nameof(TimingPatch.Prefix));
        private readonly MethodInfo finalizer = AccessTools.Method(typeof(TimingPatch), nameof(TimingPatch.Finalizer));
        private readonly HashSet<MethodBase> done = new HashSet<MethodBase>();
        private Dictionary<Assembly, string> asmToMod = new Dictionary<Assembly, string>();

        public int HarmonyCount, UpdateCount, CoroutineCount, AsyncCount, ExcludedCount, GenericSkipped;
        public double PassMsTotal, PassMsMax;
        public double CallCostUs;
        public readonly List<string> Failed = new List<string>();
        public readonly List<string> SkippedHot = new List<string>();
        public readonly HashSet<string> InstrumentedMods = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> DllMods = new List<string>();
        public readonly List<string> RelayMainMods = new List<string>();
        public bool RelayApplied;
        public int Passes;

        public Instrumenter(Harmony harmony, Config cfg)
        {
            this.harmony = harmony;
            this.cfg = cfg;
            excludeOwners = new HashSet<string>(cfg.excludeOwners ?? new string[0], StringComparer.Ordinal);
            excludeMethods = new HashSet<string>(cfg.excludeMethods ?? new string[0], StringComparer.Ordinal);
            focusMods = new HashSet<string>(cfg.focusMods ?? new string[0], StringComparer.Ordinal);
        }

        public int Total => HarmonyCount + UpdateCount + CoroutineCount + AsyncCount;

        /// <summary>One pass. Returns the number of newly timed methods.</summary>
        public int Run()
        {
            Passes++;
            if (Passes == 1)
            {
                CallCostUs = Timing.CalibrateMicroseconds();
            }
            int before = Total;
            BuildModMap();
            InstrumentHarmonyPatched();
            InstrumentModAssemblies();
            if (!RelayApplied)
            {
                RelayApplied = true;
                var ids = AssetPackManager.Shared?.OrderedPacks.Select(p => p.Info?.Identifier).Where(i => i != null).Cast<string>() ?? Enumerable.Empty<string>();
                RelayTiming.Apply(harmony, ids);
            }
            return Total - before;
        }

        private void BuildModMap()
        {
            asmToMod = new Dictionary<Assembly, string>();
            DllMods.Clear();
            RelayMainMods.Clear();
            var mgr = AssetPackManager.Shared;
            if (mgr == null)
            {
                return;
            }
            // Mod DLLs are matched to their mod by assembly name: the game loads each assembly listed in a
            // mod's Info.json and refuses a second one with the same name, so the name is unique. In memory only.
            var loadedByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.IsDynamic)
                {
                    loadedByName[a.GetName().Name] = a;
                }
            }
            RelayMainMods.AddRange(LoadedScripts.PackIdsWithMainScript());
            foreach (var pack in mgr.OrderedPacks)
            {
                var info = pack.Info;
                if (info?.Identifier == null || info.Assemblies == null || info.Assemblies.Count == 0)
                {
                    continue;
                }
                DllMods.Add(info.Identifier);
                foreach (var listed in info.Assemblies)
                {
                    if (loadedByName.TryGetValue(AssemblyNameOf(listed), out var asm))
                    {
                        asmToMod[asm] = info.Identifier;
                    }
                }
            }
        }

        /// <summary>"Folder/MyMod.dll" -> "MyMod" (string handling only).</summary>
        private static string AssemblyNameOf(string listed)
        {
            string name = listed.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        private string ModFor(Assembly? asm, string fallback)
        {
            if (asm != null && asmToMod.TryGetValue(asm, out var id))
            {
                return id;
            }
            if (asm != null && asm == typeof(Instrumenter).Assembly)
            {
                return OwnPack;
            }
            return fallback;
        }

        private void InstrumentHarmonyPatched()
        {
            List<MethodBase> methods;
            try
            {
                methods = Harmony.GetAllPatchedMethods().ToList();
            }
            catch (Exception ex)
            {
                Failed.Add("GetAllPatchedMethods: " + ex.Message);
                return;
            }
            foreach (var m in methods)
            {
                bool already = done.Contains(m);
                Patches? info;
                try
                {
                    info = Harmony.GetPatchInfo(m);
                }
                catch (Exception)
                {
                    continue;
                }
                if (info == null)
                {
                    continue;
                }
                var mods = new List<string>();
                bool excluded = false;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (patch.owner == OwnHarmonyId)
                    {
                        continue;
                    }
                    string mod = ModFor(patch.PatchMethod?.DeclaringType?.Assembly, patch.owner);
                    if (excludeOwners.Contains(patch.owner) || excludeOwners.Contains(mod))
                    {
                        excluded = true;
                    }
                    if (!mods.Contains(mod))
                    {
                        mods.Add(mod);
                    }
                }
                if (mods.Count == 0)
                {
                    continue;
                }
                if (already)
                {
                    // A mod that patched this method after it was first timed is added to its mod list (F17).
                    if (Timing.ByHandle.TryGetValue(m.MethodHandle.Value, out int slot))
                    {
                        var current = new List<int>(Timing.Mods[slot]);
                        bool changed = false;
                        foreach (var mod in mods)
                        {
                            int idx = Timing.ModIndex(mod);
                            if (!current.Contains(idx))
                            {
                                current.Add(idx);
                                InstrumentedMods.Add(mod);
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            Timing.Mods[slot] = current.ToArray();
                        }
                    }
                    continue;
                }
                // "Only time these mods": skip methods none of the focus mods patch.
                if (focusMods.Count > 0 && !mods.Exists(focusMods.Contains))
                {
                    ExcludedCount++;
                    done.Add(m);
                    continue;
                }
                if (excluded)
                {
                    ExcludedCount++;
                    done.Add(m);
                    continue;
                }
                if (Patch(m, "harmony", mods))
                {
                    HarmonyCount++;
                }
            }
        }

        private void InstrumentModAssemblies()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var kv in asmToMod)
            {
                if (kv.Value == OwnPack || excludeOwners.Contains(kv.Value) || (focusMods.Count > 0 && !focusMods.Contains(kv.Value)))
                {
                    continue;
                }
                Type?[] types;
                try
                {
                    types = kv.Key.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }
                var mods = new List<string> { kv.Value };
                foreach (var t in types)
                {
                    if (t == null)
                    {
                        continue;
                    }
                    if (t.ContainsGenericParameters)
                    {
                        if (typeof(MonoBehaviour).IsAssignableFrom(t) || IsStateMachine(t))
                        {
                            GenericSkipped++;
                        }
                        continue;
                    }
                    if (typeof(MonoBehaviour).IsAssignableFrom(t))
                    {
                        foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                        {
                            MethodInfo? m = null;
                            try
                            {
                                m = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
                            }
                            catch (Exception)
                            {
                            }
                            if (m != null && !m.IsAbstract && Patch(m, "update", mods))
                            {
                                UpdateCount++;
                            }
                        }
                    }
                    else if (IsStateMachine(t))
                    {
                        MethodInfo? m = null;
                        try
                        {
                            m = t.GetMethod("MoveNext", flags, null, Type.EmptyTypes, null);
                        }
                        catch (Exception)
                        {
                        }
                        if (m == null)
                        {
                            continue;
                        }
                        bool isAsync = typeof(IAsyncStateMachine).IsAssignableFrom(t);
                        if (Patch(m, isAsync ? "async" : "coroutine", mods))
                        {
                            if (isAsync) AsyncCount++; else CoroutineCount++;
                        }
                    }
                }
            }
        }

        private static bool IsStateMachine(Type t)
        {
            if (!t.IsNested || !t.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                return false;
            }
            return typeof(IEnumerator).IsAssignableFrom(t) || typeof(IAsyncStateMachine).IsAssignableFrom(t);
        }

        public static string LabelFor(MethodBase m)
        {
            var t = m.DeclaringType;
            if (t == null)
            {
                return m.Name;
            }
            if (t.IsNested && t.Name.StartsWith("<", StringComparison.Ordinal))
            {
                // <Name>d__12 -> Outer.Name
                int end = t.Name.IndexOf('>');
                string inner = end > 1 ? t.Name.Substring(1, end - 1) : t.Name;
                return (t.DeclaringType?.FullName ?? "?") + "." + inner;
            }
            return t.FullName + "." + m.Name;
        }

        private bool Patch(MethodBase m, string kind, List<string> mods)
        {
            if (!done.Add(m))
            {
                return false;
            }
            string label = LabelFor(m);
            if (excludeMethods.Contains(label) || excludeMethods.Contains((m.DeclaringType?.FullName ?? "") + "." + m.Name))
            {
                ExcludedCount++;
                return false;
            }
            if (m.IsGenericMethodDefinition || (m.DeclaringType?.ContainsGenericParameters ?? false))
            {
                GenericSkipped++;
                return false;
            }
            var modIdx = new int[mods.Count];
            for (int i = 0; i < mods.Count; i++)
            {
                modIdx[i] = Timing.ModIndex(mods[i]);
            }
            int slot = Timing.AddSlot(label, kind, modIdx, m);
            if (slot < 0)
            {
                Failed.Add(label + ": slot limit reached");
                return false;
            }
            try
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });
                foreach (var mod in mods)
                {
                    InstrumentedMods.Add(mod);
                }
                return true;
            }
            catch (Exception ex)
            {
                Timing.Skipped[slot] = true;
                Failed.Add(label + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes the timing patch from a hot method (called from the main thread).</summary>
        public void Unpatch(int slot)
        {
            Timing.Skipped[slot] = true;
            var m = Timing.Method[slot];
            if (m == null)
            {
                return;
            }
            try
            {
                harmony.Unpatch(m, prefix);
                harmony.Unpatch(m, finalizer);
            }
            catch (Exception ex)
            {
                Failed.Add(Timing.Label[slot] + " unpatch: " + ex.Message);
            }
        }
    }
}
