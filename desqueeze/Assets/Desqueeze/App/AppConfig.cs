using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Desqueeze.Core;
using UnityEngine;

namespace Desqueeze.App
{
    [Serializable]
    public sealed class AnamorphicPreset
    {
        /// <summary>Button label. Leave empty to show the factor, e.g. "1.5x".</summary>
        public string name = "";
        public double factor = 1.5;

        public AnamorphicPreset() { }

        public AnamorphicPreset(string name, double factor)
        {
            this.name = name;
            this.factor = factor;
        }

        public string Label =>
            string.IsNullOrEmpty(name) ? factor.ToString("0.###", CultureInfo.InvariantCulture) + "x" : name;
    }

    /// <summary>
    /// App configuration, read from StreamingAssets/desqueeze-config.json at start-up.
    /// Edit that file to add anamorphic presets or change defaults; no rebuild is needed for a
    /// built player (the file sits next to the app's data). Any field left out keeps the value below.
    /// </summary>
    [Serializable]
    public sealed class AppConfig
    {
        public const string FileName = "desqueeze-config.json";
        /// <summary>Trained model for the learned widening methods (in StreamingAssets).</summary>
        public const string ModelFileName = "desqueeze-axissr.bin";

        public AnamorphicPreset[] presets =
        {
            new AnamorphicPreset("1.33x", 1.33),
            new AnamorphicPreset("1.5x", 1.5),
            new AnamorphicPreset("1.6x", 1.6),
        };

        /// <summary>Factor selected on first launch (and after "Reset to defaults").</summary>
        public double defaultFactor = 1.5;
        public double customFactorMin = 1.0;
        public double customFactorMax = 3.0;

        /// <summary>Default position of the width/height split slider: 0 = height only, 1 = width only.</summary>
        public double defaultWidthShare = 0.5;

        /// <summary>Name of a ResampleAlgorithm value, e.g. "MagicKernelSharp2021", "Lanczos3", "EwaLanczosSharp".</summary>
        public string defaultAlgorithm = "MagicKernelSharp2021";
        /// <summary>How the stretched axis is enlarged: "Detail", "DetailFast" or "Kernel".</summary>
        public string widenMethod = "Detail";
        /// <summary>Axis the lens squeezed: "Horizontal" (normal anamorphic) or "Vertical".</summary>
        public string squeezeAxis = "Horizontal";

        /// <summary>Light space when shrinking an axis: "Gamma", "Linear" or "Sigmoidal".</summary>
        public string reduceLight = "Gamma";
        /// <summary>Light space when enlarging an axis: "Sigmoidal", "Gamma" or "Linear".</summary>
        public string enlargeLight = "Sigmoidal";
        public float antiRinging = 0f;

        /// <summary>"jpg" or "png" for interactive Save As.</summary>
        public string defaultSaveFormat = "jpg";
        public string batchFileSuffix = "_desqueezed";

        /// <summary>UI size multiplier. 0 picks automatically from the screen's DPI.</summary>
        public float uiScale = 0f;

        /// <summary>Largest viewer magnification (integer).</summary>
        public int maxZoom = 64;

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        /// <summary>Loads the config file. Never throws: problems are reported in <paramref name="warning"/>.</summary>
        public static AppConfig Load(out string warning)
        {
            warning = null;
            var cfg = new AppConfig();
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path), cfg);
                }
                else
                {
                    warning = "Config file not found (" + path + "); using built-in defaults.";
                }
            }
            catch (Exception e)
            {
                cfg = new AppConfig();
                warning = "Could not read " + FileName + ": " + e.Message + " - using built-in defaults.";
            }
            cfg.Sanitize();
            return cfg;
        }

        private void Sanitize()
        {
            var list = new List<AnamorphicPreset>();
            if (presets != null)
                foreach (var p in presets)
                    if (p != null && p.factor > 0 && !double.IsNaN(p.factor) && !double.IsInfinity(p.factor))
                        list.Add(p);
            if (list.Count == 0) list.Add(new AnamorphicPreset("1.5x", 1.5));
            presets = list.ToArray();

            if (!(customFactorMin > 0)) customFactorMin = 1.0;
            if (!(customFactorMax > customFactorMin)) customFactorMax = Math.Max(customFactorMin + 0.5, 3.0);
            if (!(defaultFactor > 0)) defaultFactor = presets[0].factor;
            if (double.IsNaN(defaultWidthShare)) defaultWidthShare = 0.5;
            defaultWidthShare = Math.Max(0, Math.Min(1, defaultWidthShare));
            antiRinging = Mathf.Clamp01(antiRinging);
            if (maxZoom < 1) maxZoom = 1;
            if (maxZoom > 256) maxZoom = 256;
            if (uiScale < 0 || uiScale > 4) uiScale = 0;
            defaultSaveFormat = (defaultSaveFormat ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
            if (defaultSaveFormat != "png") defaultSaveFormat = "jpg";
            if (batchFileSuffix == null) batchFileSuffix = "";
        }

        public ResampleAlgorithm DefaultAlgorithm
        {
            get
            {
                ResampleAlgorithm a;
                if (!string.IsNullOrEmpty(defaultAlgorithm) && Enum.TryParse(defaultAlgorithm.Trim(), true, out a) &&
                    Enum.IsDefined(typeof(ResampleAlgorithm), a))
                    return a;
                return ResampleAlgorithm.MagicKernelSharp2021;
            }
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            T v;
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value.Trim(), true, out v) && Enum.IsDefined(typeof(T), v)) return v;
            return fallback;
        }

        public static string ModelPath => Path.Combine(Application.streamingAssetsPath, ModelFileName);

        private static LightSpace ParseLight(string value, LightSpace fallback)
        {
            LightSpace l;
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value.Trim(), true, out l) && Enum.IsDefined(typeof(LightSpace), l))
                return l;
            return fallback;
        }

        public DesqueezeSettings CreateDefaultSettings()
        {
            return new DesqueezeSettings
            {
                Factor = defaultFactor,
                WidthShare = defaultWidthShare,
                Algorithm = DefaultAlgorithm,
                Widen = ParseEnum(widenMethod, WidenMethod.Detail),
                Axis = ParseEnum(squeezeAxis, SqueezeAxis.Horizontal),
                ReduceLight = ParseLight(reduceLight, LightSpace.Gamma),
                EnlargeLight = ParseLight(enlargeLight, LightSpace.Sigmoidal),
                AntiRinging = antiRinging,
            };
        }

        /// <summary>Index of the preset matching a factor, or -1 for a custom value.</summary>
        public int PresetIndexOf(double factor)
        {
            for (int i = 0; i < presets.Length; i++)
                if (Math.Abs(presets[i].factor - factor) < 1e-9) return i;
            return -1;
        }
    }
}
