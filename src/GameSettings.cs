using System;
using System.Collections.Generic;
using Game;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// The game's own graphics and performance settings, read from the game's Preferences (names as shown in
    /// the game's Preferences window), so the report reminds the player what they were running and which
    /// settings to consider. Only these settings are read; a setting the game no longer has is left out.
    /// </summary>
    internal static class GameSettings
    {
        public static List<ReportData.Setting> Read()
        {
            var list = new List<ReportData.Setting>();
            Add(list, "Quality Preset", () => Preferences.GraphicsQualityPreset.ToString());
            Add(list, "Render Scale", () => Pct(Preferences.GraphicsRenderScale));
            Add(list, "Anti-aliasing", () => Preferences.GraphicsAntiAliasing.ToString());
            Add(list, "Shadow Quality", () => Preferences.GraphicsShadowQuality.ToString());
            Add(list, "Ambient Occlusion", () => OnOff(Preferences.GraphicsAmbientOcclusion));
            Add(list, "Volumetric Clouds", () => Preferences.GraphicsVolumetricClouds.ToString());
            Add(list, "Volumetric Lighting", () => OnOff(Preferences.GraphicsVolumetricLighting));
            Add(list, "Texture Quality", () => Preferences.GraphicsTextureQuality.ToString());
            Add(list, "Draw Distance", () => Preferences.GraphicsDrawDistance.ToString("0", Text.Inv) + " m");
            Add(list, "Rolling Stock Detail", () => Preferences.GraphicsRollingStockDetail.ToString());
            Add(list, "Vegetation Richness", () => Preferences.GraphicsVegetationRichness.ToString());
            Add(list, "Tree Density", () => Num(Preferences.GraphicsTreeDensity));
            Add(list, "Grass Density", () => Num(Preferences.GraphicsDetailDensity));
            Add(list, "Grass Distance", () => Num(Preferences.GraphicsDetailDistance));
            Add(list, "Decal Distance", () => Preferences.GraphicsDecalDistance.ToString());
            Add(list, "Particles", () => Preferences.GraphicsParticleLevel.ToString());
            Add(list, "Loading Fog", () => OnOff(Preferences.GraphicsTerrainAwareFog));
            Add(list, "Vsync", () => Preferences.GraphicsVsync.ToString());
            Add(list, "Discord Rich Presence", () => Preferences.DiscordActivityDetailLevel.ToString());
            Add(list, "HTTP Server", () => OnOff(Preferences.HTTPServerEnabled));
            return list;
        }

        /// <summary>Value of a setting by name, or "?" when it was not read.</summary>
        public static string Value(List<ReportData.Setting> list, string name)
        {
            foreach (var s in list)
            {
                if (s.name == name) return s.value;
            }
            return "?";
        }

        private static void Add(List<ReportData.Setting> list, string name, Func<string> read)
        {
            try
            {
                list.Add(new ReportData.Setting { name = name, value = read() });
            }
            catch (Exception)
            {
                // The game changed this setting: leave it out rather than guess.
            }
        }

        private static string OnOff(bool b) => b ? "On" : "Off";

        private static string Num(float v) => v.ToString("0.##", Text.Inv);

        private static string Pct(float v) => (v * 100f).ToString("0", Text.Inv) + "%";
    }
}
