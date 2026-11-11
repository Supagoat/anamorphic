using System;
using System.Globalization;
using Desqueeze.Core;
using Desqueeze.Core.Batch;
using UnityEngine;

namespace Desqueeze.App
{
    /// <summary>Remembers the user's last settings and folders between sessions (PlayerPrefs).</summary>
    internal static class Prefs
    {
        private const string P = "Desqueeze.";

        public static string GetString(string key, string fallback)
        {
            return PlayerPrefs.GetString(P + key, fallback);
        }

        public static void SetString(string key, string value)
        {
            PlayerPrefs.SetString(P + key, value ?? "");
        }

        public static bool GetBool(string key, bool fallback)
        {
            return PlayerPrefs.GetInt(P + key, fallback ? 1 : 0) != 0;
        }

        public static void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(P + key, value ? 1 : 0);
        }

        public static double GetDouble(string key, double fallback)
        {
            double v;
            string s = PlayerPrefs.GetString(P + key, "");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static void SetDouble(string key, double value)
        {
            PlayerPrefs.SetString(P + key, value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static DesqueezeSettings LoadSettings(DesqueezeSettings defaults)
        {
            var s = defaults.Clone();
            s.Factor = GetDouble("Factor", s.Factor);
            s.WidthShare = GetDouble("WidthShare", s.WidthShare);
            ResampleAlgorithm a;
            if (Enum.TryParse(GetString("Algorithm", s.Algorithm.ToString()), out a) && Enum.IsDefined(typeof(ResampleAlgorithm), a))
                s.Algorithm = a;
            LightSpace l;
            if (Enum.TryParse(GetString("ReduceLight", s.ReduceLight.ToString()), out l) && Enum.IsDefined(typeof(LightSpace), l))
                s.ReduceLight = l;
            if (Enum.TryParse(GetString("EnlargeLight", s.EnlargeLight.ToString()), out l) && Enum.IsDefined(typeof(LightSpace), l))
                s.EnlargeLight = l;
            s.AntiRinging = (float)GetDouble("AntiRinging", s.AntiRinging);
            WidenMethod wm;
            if (Enum.TryParse(GetString("Widen", s.Widen.ToString()), out wm) && Enum.IsDefined(typeof(WidenMethod), wm))
                s.Widen = wm;
            SqueezeDirection dir;
            if (Enum.TryParse(GetString("Direction", s.Direction.ToString()), out dir) && Enum.IsDefined(typeof(SqueezeDirection), dir))
                s.Direction = dir;
            s.DistortionAmount = GetDouble("DistortionAmount", s.DistortionAmount);
            Desqueeze.Core.Lens.DistortionEdges edges;
            if (Enum.TryParse(GetString("DistortionEdges", s.DistortionEdges.ToString()), out edges) && Enum.IsDefined(typeof(Desqueeze.Core.Lens.DistortionEdges), edges))
                s.DistortionEdges = edges;
            Desqueeze.Core.Lens.DistortionOrder order;
            if (Enum.TryParse(GetString("DistortionOrder", s.DistortionOrder.ToString()), out order) && Enum.IsDefined(typeof(Desqueeze.Core.Lens.DistortionOrder), order))
                s.DistortionOrder = order;
            try
            {
                s.Validate();
            }
            catch (Exception)
            {
                s = defaults.Clone();
            }
            return s;
        }

        public static void SaveSettings(DesqueezeSettings s)
        {
            SetDouble("Factor", s.Factor);
            SetDouble("WidthShare", s.WidthShare);
            SetString("Algorithm", s.Algorithm.ToString());
            SetString("ReduceLight", s.ReduceLight.ToString());
            SetString("EnlargeLight", s.EnlargeLight.ToString());
            SetDouble("AntiRinging", s.AntiRinging);
            SetString("Widen", s.Widen.ToString());
            SetString("Direction", s.Direction.ToString());
            SetDouble("DistortionAmount", s.DistortionAmount);
            SetString("DistortionEdges", s.DistortionEdges.ToString());
            SetString("DistortionOrder", s.DistortionOrder.ToString());
        }

        public static void LoadBatch(BatchOptions o, string defaultSuffix)
        {
            o.InputFolder = GetString("BatchIn", "");
            o.OutputFolder = GetString("BatchOut", "");
            o.IncludeSubfolders = GetBool("BatchSubfolders", false);
            o.OverwriteExisting = GetBool("BatchOverwrite", false);
            o.FileSuffix = GetString("BatchSuffix", defaultSuffix);
        }

        public static void SaveBatch(BatchOptions o)
        {
            SetString("BatchIn", o.InputFolder);
            SetString("BatchOut", o.OutputFolder);
            SetBool("BatchSubfolders", o.IncludeSubfolders);
            SetBool("BatchOverwrite", o.OverwriteExisting);
            SetString("BatchSuffix", o.FileSuffix);
        }

        public static void Flush()
        {
            PlayerPrefs.Save();
        }
    }
}
