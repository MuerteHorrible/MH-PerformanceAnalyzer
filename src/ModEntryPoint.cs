// Structure based on PrettyConvenientMenu (MIT, crazybmanp).
using System;
using Railroader.Modding;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Game entry point. Loading the mod only reads the player's settings; nothing is measured or patched
    /// until the player switches recording on (see Recording).
    /// </summary>
    public sealed class ModEntryPoint : IModEntryPoint, IWorldLifecycle
    {
        public const string Version = "1.0.0";

        internal static IModLogger? Log { get; private set; }

        public void OnModLoad(IModContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            Log = context.Log;
            try
            {
                // The game reuses this assembly when the mod list changes in-game: start from clean statics.
                Timing.Reset();
                SaveHook.Reset();
                RelayTiming.Reset();

                // Everything Performance Analyzer reads or writes is inside <mod folder>\Output (see OutputStore).
                var store = new OutputStore(context.ModDirectory);
                store.EnsureRoot();
                Recording.Initialise(store, Config.Load(store, Log), Log);
                Application.quitting += OnQuitting;
                Log.Info($"MH Performance Analyzer {Version} loaded, not recording.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MH Performance Analyzer failed to start.");
            }
        }

        public void OnModUnload()
        {
            Application.quitting -= OnQuitting;
            Recording.Shutdown();
        }

        public void OnWorldLoad() => Recording.OnWorldLoad();

        public void OnWorldUnload() => Recording.OnWorldUnload();

        private static void OnQuitting() => Recording.Stop();
    }
}
