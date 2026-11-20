using System;
using Railroader.Modding;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>The player's settings, kept in Output\settings.json (written only when a setting is changed).</summary>
    [Serializable]
    internal sealed class Config
    {
        public float spikeThresholdMs = 33.3f;
        public float spikeMultiplier = 3.0f;
        public float spikeCooldownMs = 500f;
        public bool perModTiming = true;
        public float memorySampleSeconds = 1f;
        public int keepSessions = 10;
        public string[] excludeOwners = new string[0];
        public string[] excludeMethods = new string[0];
        public string[] focusMods = new string[0];
        public int maxCallsPerFrame = 200;
        public string sessionLabel = "";

        public const string FileName = "settings.json";

        /// <summary>Reads Output\settings.json if the player has saved settings; otherwise the defaults. Never writes.</summary>
        public static Config Load(OutputStore store, IModLogger log)
        {
            var config = new Config();
            try
            {
                string? json = store.ReadText(FileName);
                if (json != null)
                {
                    JsonUtility.FromJsonOverwrite(json, config);
                }
            }
            catch (Exception ex)
            {
                log.Error("MH Performance Analyzer: settings.json could not be read (" + ex.GetType().Name + "), using defaults.");
                config = new Config();
            }
            config.Clamp();
            return config;
        }

        /// <summary>A separate copy, so changes made in the settings window apply from the next Start.</summary>
        public Config Copy()
        {
            var c = JsonUtility.FromJson<Config>(JsonUtility.ToJson(this));
            c.Clamp();
            return c;
        }

        /// <summary>Writes Output\settings.json. Called only when the player changes a setting.</summary>
        public void Save(OutputStore store)
        {
            Clamp();
            store.WriteText(FileName, JsonUtility.ToJson(this, true));
        }

        private void Clamp()
        {
            spikeThresholdMs = Mathf.Max(1f, spikeThresholdMs);
            spikeMultiplier = Mathf.Max(1f, spikeMultiplier);
            spikeCooldownMs = Mathf.Max(0f, spikeCooldownMs);
            memorySampleSeconds = Mathf.Clamp(memorySampleSeconds, 0.25f, 60f);
            keepSessions = Math.Max(1, keepSessions);
            maxCallsPerFrame = Math.Max(1, maxCallsPerFrame);
            excludeOwners ??= new string[0];
            excludeMethods ??= new string[0];
            focusMods ??= new string[0];
            sessionLabel ??= "";
        }
    }
}
