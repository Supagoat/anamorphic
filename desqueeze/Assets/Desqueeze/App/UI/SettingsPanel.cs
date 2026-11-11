using System;
using System.Globalization;
using Desqueeze.Core;
using Desqueeze.Core.Imaging;
using UnityEngine;

namespace Desqueeze.App.UI
{
    /// <summary>
    /// The de-squeeze controls shared by interactive and batch modes: anamorphic factor (presets from
    /// the config file, or custom), the single width/height split slider (locked to the factor), the
    /// resampling algorithm and the advanced light-space options.
    /// </summary>
    internal sealed class SettingsPanel
    {
        internal struct AlgorithmInfo
        {
            public ResampleAlgorithm Algorithm;
            public string Name;
            public string Description;

            public AlgorithmInfo(ResampleAlgorithm a, string name, string description)
            {
                Algorithm = a;
                Name = name;
                Description = description;
            }
        }

        public static readonly AlgorithmInfo[] Algorithms =
        {
            new AlgorithmInfo(ResampleAlgorithm.MagicKernelSharp2021, "Magic Kernel Sharp 2021",
                "Best overall in our benchmark: keeps the most detail with low ringing, whether widening or shortening. Recommended."),
            new AlgorithmInfo(ResampleAlgorithm.Lanczos4, "Lanczos 4",
                "4-lobe windowed sinc. Marginally the sharpest for pure widening of very crisp (no AA filter) captures; a little more ringing."),
            new AlgorithmInfo(ResampleAlgorithm.Lanczos3, "Lanczos 3",
                "The classic 3-lobe windowed sinc. Very sharp, slight ringing."),
            new AlgorithmInfo(ResampleAlgorithm.Spline64, "Spline 64",
                "Panorama Tools spline, 4 lobes. Close to Lanczos 4 with slightly less ringing."),
            new AlgorithmInfo(ResampleAlgorithm.Spline36, "Spline 36",
                "Panorama Tools spline, 3 lobes. Close to Lanczos 3 with slightly less ringing."),
            new AlgorithmInfo(ResampleAlgorithm.EwaLanczosSharp, "EWA Lanczos Sharp",
                "Radial (Jinc) filter in one 2-D pass: smoothest diagonals but softer, and it also filters the axis that isn't resized. Slower."),
            new AlgorithmInfo(ResampleAlgorithm.CatmullRom, "Catmull-Rom",
                "Sharp bicubic. Fast."),
            new AlgorithmInfo(ResampleAlgorithm.Mitchell, "Mitchell-Netravali",
                "Soft bicubic with almost no ringing."),
            new AlgorithmInfo(ResampleAlgorithm.Bilinear, "Bilinear",
                "Fast and soft."),
            new AlgorithmInfo(ResampleAlgorithm.Box, "Box / Nearest",
                "Nearest neighbour when widening, exact area average when shortening. For comparison only."),
        };

        private static readonly WidenMethod[] WidenValues = { WidenMethod.Detail, WidenMethod.DetailFast, WidenMethod.Kernel };
        private static readonly string[] WidenNames = { "Detail", "Detail (fast)", "Kernel" };
        private static readonly string[] WidenTips =
        {
            "Cross-axis detail reconstruction: learned filters plus a small detail network that uses the fully sampled other axis to rebuild edges. Sharpest; slower.",
            "Learned filters only: about half the gain of Detail at a fraction of the time.",
            "Classic interpolation with the kernel selected below.",
        };

        private static readonly SqueezeDirection[] DirectionValues = { SqueezeDirection.Auto, SqueezeDirection.Horizontal, SqueezeDirection.Vertical };
        private static readonly string[] DirectionNames = { "Auto", "Horizontal", "Vertical" };
        private static readonly string[] DirectionTips =
        {
            "Follow the camera: shots taken with the camera turned 90° (orientation tag) are de-squeezed vertically, " +
            "as are portrait-shaped images whose pixels were already rotated. Everything else horizontally.",
            "Always de-squeeze across the width of the image as it is shown.",
            "Always de-squeeze along the height of the image as it is shown.",
        };

        private static readonly Desqueeze.Core.Lens.DistortionOrder[] OrderValues =
            { Desqueeze.Core.Lens.DistortionOrder.AfterDesqueeze, Desqueeze.Core.Lens.DistortionOrder.BeforeDesqueeze, Desqueeze.Core.Lens.DistortionOrder.SinglePass };
        private static readonly string[] OrderNames = { "Together", "Before", "One pass" };
        private static readonly string[] OrderTips =
        {
            "Correct while de-squeezing, in two passes: the detail widener rebuilds each row at the corrected positions, then each column is resampled once (also shortening the height). Measured best.",
            "Correct the distortion on the squeezed capture, then de-squeeze. Measured 0.4-0.5 dB worse: the widener then sees resampled pixels.",
            "One 2-D resampling straight from the capture to the corrected output, with the warp kernel only (no detail widener).",
        };

        private static readonly string[] LightNames = { "Linear", "Gamma", "Sigmoidal" };
        private static readonly LightSpace[] LightValues = { LightSpace.Linear, LightSpace.Gamma, LightSpace.Sigmoidal };

        private readonly AppConfig _cfg;
        private bool _customMode;
        private string _customText = "";
        private double _customTextValue = double.NaN;
        private bool _algoOpen;
        private bool _advancedOpen;

        private readonly Action<Action> _defer;
        private readonly Action _onChanged;

        /// <summary>Optional extra section drawn after the output size (the distortion controls). Returns true if a setting changed.</summary>
        public Func<DesqueezeSettings, UiStyles, PixelImage, bool> DrawAfterGeometry;

        /// <param name="defer">Runs structural changes outside OnGUI (see DesqueezeApp.Defer).</param>
        /// <param name="onChanged">Called after a deferred change to the settings.</param>
        public SettingsPanel(AppConfig cfg, DesqueezeSettings s, Action<Action> defer, Action onChanged)
        {
            _cfg = cfg;
            _defer = defer;
            _onChanged = onChanged;
            _customMode = cfg.PresetIndexOf(s.Factor) < 0;
            SyncCustomText(s.Factor);
        }

        public static AlgorithmInfo Info(ResampleAlgorithm a)
        {
            foreach (var i in Algorithms)
                if (i.Algorithm == a) return i;
            return new AlgorithmInfo(a, a.ToString(), "");
        }

        private void SyncCustomText(double factor)
        {
            _customText = factor.ToString("0.###", CultureInfo.InvariantCulture);
            _customTextValue = factor;
        }

        /// <summary>
        /// Draws the controls (GUILayout). Returns true if any setting changed.
        /// <paramref name="image"/> is the open (upright) image in interactive mode, or null.
        /// </summary>
        public bool Draw(DesqueezeSettings s, UiStyles ui, PixelImage image)
        {
            bool changed = false;

            // ---------------------------------------------------------------- factor
            GUILayout.Label("ANAMORPHIC FACTOR", ui.Section);
            int presetIndex = _customMode ? -1 : _cfg.PresetIndexOf(s.Factor);
            int perRow = 4;
            int count = _cfg.presets.Length + 1;
            for (int row = 0; row * perRow < count; row++)
            {
                GUILayout.BeginHorizontal();
                for (int i = row * perRow; i < Math.Min(count, (row + 1) * perRow); i++)
                {
                    bool isCustom = i == _cfg.presets.Length;
                    bool selected = isCustom ? presetIndex < 0 : presetIndex == i;
                    string label = isCustom ? "Custom" : _cfg.presets[i].Label;
                    var tip = isCustom ? "Enter any anamorphic factor" : "Anamorphic squeeze factor " + _cfg.presets[i].factor.ToString("0.###", CultureInfo.InvariantCulture) + "x";
                    if (GUILayout.Button(new GUIContent(label, tip), selected ? ui.ButtonSelected : ui.Button, GUILayout.MinWidth(ui.S(48))))
                    {
                        if (isCustom)
                        {
                            _defer(() =>
                            {
                                _customMode = true;
                                SyncCustomText(s.Factor);
                            });
                        }
                        else
                        {
                            double f = _cfg.presets[i].factor;
                            _defer(() =>
                            {
                                _customMode = false;
                                s.Factor = f;
                                _onChanged();
                            });
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }

            if (presetIndex < 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Factor", ui.Label, GUILayout.Width(ui.S(52)));
                GUI.SetNextControlName("customFactor");
                string t = GUILayout.TextField(_customText, ui.TextField, GUILayout.Width(ui.S(70)));
                if (t != _customText)
                {
                    _customText = t;
                    double v;
                    if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) &&
                        v >= _cfg.customFactorMin && v <= _cfg.customFactorMax)
                    {
                        _customTextValue = v;
                        if (s.Factor != v)
                        {
                            s.Factor = v;
                            changed = true;
                        }
                    }
                }
                GUILayout.Label("x", ui.Label, GUILayout.Width(ui.S(14)));
                float sl = GUILayout.HorizontalSlider((float)s.Factor, (float)_cfg.customFactorMin, (float)_cfg.customFactorMax,
                    ui.Slider, ui.Thumb, GUILayout.ExpandWidth(true));
                if (Math.Abs(sl - s.Factor) > 1e-6)
                {
                    s.Factor = Math.Round(sl, 3);
                    SyncCustomText(s.Factor);
                    GUI.FocusControl(null);
                    changed = true;
                }
                GUILayout.EndHorizontal();
                if (Math.Abs(_customTextValue - s.Factor) > 1e-9 && GUI.GetNameOfFocusedControl() != "customFactor")
                    SyncCustomText(s.Factor);
                GUILayout.Label("Range " + _cfg.customFactorMin.ToString("0.##", CultureInfo.InvariantCulture) + " - " +
                                _cfg.customFactorMax.ToString("0.##", CultureInfo.InvariantCulture) + " (set in the config file)", ui.MutedSmall);
            }

            // ---------------------------------------------------------------- direction
            GUILayout.Label("SQUEEZE DIRECTION", ui.Section);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < DirectionValues.Length; i++)
            {
                var d = DirectionValues[i];
                bool sel = s.Direction == d;
                if (GUILayout.Button(new GUIContent(DirectionNames[i], DirectionTips[i]), sel ? ui.ButtonSelected : ui.Button) && !sel)
                {
                    s.Direction = d;
                    changed = true;
                }
            }
            GUILayout.EndHorizontal();
            var choice = AxisChooser.Choose(s.Direction, image);
            bool vertical = choice.IsVertical;
            if (image != null)
                GUILayout.Label("This image: " + (vertical ? "vertical" : "horizontal") + " - " + choice.Reason, ui.MutedSmall);
            else if (s.Direction == SqueezeDirection.Auto)
                GUILayout.Label("Decided for each image from the camera's orientation.", ui.MutedSmall);
            else
                GUILayout.Label("Every image is de-squeezed " + (vertical ? "vertically." : "horizontally."), ui.MutedSmall);

            // ---------------------------------------------------------------- split
            // The slider is the share of the correction that comes from stretching the squeezed axis; its right end
            // adds resolution in either direction. Labels follow the axis of the open image (horizontal if none).
            GUILayout.Label(vertical ? "WIDTH / HEIGHT SPLIT" : "HEIGHT / WIDTH SPLIT", ui.Section);
            float share = GUILayout.HorizontalSlider((float)s.WidthShare, 0f, 1f, ui.Slider, ui.Thumb, GUILayout.ExpandWidth(true));
            if (Math.Abs(share - s.WidthShare) > 1e-6)
            {
                // Gentle snapping to the three landmarks.
                if (Math.Abs(share - 0.5f) < 0.012f) share = 0.5f;
                s.WidthShare = Math.Round(share, 3);
                changed = true;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(vertical ? "Narrow width" : "Shorten height", ui.MutedSmall);
            GUILayout.FlexibleSpace();
            GUILayout.Label(vertical ? "Stretch height" : "Widen width", ui.MutedSmall);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (vertical)
            {
                changed |= SnapButton(s, ui, 0.0, "Width only", "Height x1: only the width is reduced (no upsampling)");
                changed |= SnapButton(s, ui, 0.5, "Balanced", "Keeps the total pixel count the same");
                changed |= SnapButton(s, ui, 1.0, "Height only", "Width x1: keeps every captured column; only the height is enlarged");
            }
            else
            {
                changed |= SnapButton(s, ui, 0.0, "Height only", "Width x1: only the height is reduced (no upsampling)");
                changed |= SnapButton(s, ui, 0.5, "Balanced", "Keeps the total pixel count the same");
                changed |= SnapButton(s, ui, 1.0, "Width only", "Height x1: keeps every captured row; only the width is enlarged");
            }
            GUILayout.EndHorizontal();

            double grow = DesqueezeGeometry.WidthScaleFor(s.Factor, s.WidthShare);
            double shrink = DesqueezeGeometry.HeightScaleFor(s.Factor, s.WidthShare);
            double sx = vertical ? shrink : grow;
            double sy = vertical ? grow : shrink;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Width", ui.Label);
            GUILayout.Label("x" + sx.ToString("0.000", CultureInfo.InvariantCulture), ui.Value);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Height", ui.Label);
            GUILayout.Label("x" + sy.ToString("0.000", CultureInfo.InvariantCulture), ui.Value);
            GUILayout.EndHorizontal();

            if (image != null)
            {
                int imageWidth = image.Width, imageHeight = image.Height;
                try
                {
                    var g = DesqueezeGeometry.Compute(imageWidth, imageHeight, s.Factor, s.WidthShare, choice.Axis);
                    GUILayout.Label(imageWidth + " x " + imageHeight + "  »  " + g.OutputWidth + " x " + g.OutputHeight + " px", ui.Label);
                    GUILayout.Label("Aspect " + g.OutputAspect.ToString("0.000", CultureInfo.InvariantCulture) + " : 1   ·   " +
                                    (g.PixelCountRatio * 100).ToString("0", CultureInfo.InvariantCulture) + "% of the pixels   ·   " +
                                    ((double)g.OutputWidth * g.OutputHeight / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MP", ui.MutedSmall);
                }
                catch (Exception e)
                {
                    GUILayout.Label(e.Message, ui.Error);
                }
            }

            // ---------------------------------------------------------------- distortion (drawn by the lens panel)
            if (DrawAfterGeometry != null) changed |= DrawAfterGeometry(s, ui, image);

            // ---------------------------------------------------------------- widening method
            GUILayout.Label(vertical ? "STRETCHING (HEIGHT)" : "WIDENING", ui.Section);
            bool modelLoaded = Desqueeze.Core.Resampling.AxisSRModel.Default != null;
            bool on = GUI.enabled;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < WidenNames.Length; i++)
            {
                var wm = WidenValues[i];
                bool sel = s.Widen == wm;
                bool available = modelLoaded || wm == WidenMethod.Kernel;
                GUI.enabled = available && on;
                if (GUILayout.Button(new GUIContent(WidenNames[i], WidenTips[i]), sel ? ui.ButtonSelected : ui.Button) && !sel)
                {
                    s.Widen = wm;
                    changed = true;
                }
                GUI.enabled = on;
            }
            GUILayout.EndHorizontal();
            if (s.Widen != WidenMethod.Kernel && !modelLoaded)
                GUILayout.Label("Detail model not found (" + AppConfig.ModelFileName + "); using the kernel below.", ui.Error);
            else
                GUILayout.Label(WidenTips[Array.IndexOf(WidenValues, s.Widen)], ui.MutedSmall);

            // ---------------------------------------------------------------- algorithm
            GUILayout.Label(s.Widen == WidenMethod.Kernel ? "RESAMPLING KERNEL" : "KERNEL (shortening)", ui.Section);
            var info = Info(s.Algorithm);
            if (GUILayout.Button(new GUIContent(info.Name + (_algoOpen ? "   ^" : "   v"), "Choose the resampling algorithm"), ui.Button))
                _defer(() => _algoOpen = !_algoOpen);
            if (_algoOpen)
            {
                foreach (var a in Algorithms)
                {
                    bool sel = a.Algorithm == s.Algorithm;
                    if (GUILayout.Button(new GUIContent(a.Name, a.Description), sel ? ui.ButtonSelected : ui.Button))
                    {
                        var chosen = a.Algorithm;
                        _defer(() =>
                        {
                            _algoOpen = false;
                            if (s.Algorithm != chosen)
                            {
                                s.Algorithm = chosen;
                                _onChanged();
                            }
                        });
                    }
                }
            }
            GUILayout.Label(info.Description, ui.MutedSmall);

            // ---------------------------------------------------------------- advanced
            if (GUILayout.Button(_advancedOpen ? "Advanced  ^" : "Advanced  v", ui.Button))
                _defer(() => _advancedOpen = !_advancedOpen);
            if (_advancedOpen)
            {
                changed |= LightRow(ui, "Shrinking axis", "Light space for the axis being shortened. Gamma measured best (linear light darkens edges).", ref s.ReduceLight);
                changed |= LightRow(ui, "Enlarging axis", "Light space for the axis being enlarged. Sigmoidal measured best (fewer halos).", ref s.EnlargeLight);

                GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Anti-ringing", "Clamps new pixels on the enlarged axis toward their nearest source pixels: removes halos, costs a little micro-contrast."), ui.Label, GUILayout.Width(ui.S(96)));
                float ar = GUILayout.HorizontalSlider(s.AntiRinging, 0f, 1f, ui.Slider, ui.Thumb, GUILayout.ExpandWidth(true));
                ar = Mathf.Round(ar * 20f) / 20f;
                GUILayout.Label(ar.ToString("0.00", CultureInfo.InvariantCulture), ui.Value, GUILayout.Width(ui.S(40)));
                GUILayout.EndHorizontal();
                if (Math.Abs(ar - s.AntiRinging) > 1e-6)
                {
                    s.AntiRinging = ar;
                    changed = true;
                }

                GUILayout.Label(new GUIContent("Distortion correction order", "When the lens profile corrects distortion. Together with the de-squeeze measured best."), ui.Label);
                GUILayout.BeginHorizontal();
                for (int i = 0; i < OrderValues.Length; i++)
                {
                    bool sel = s.DistortionOrder == OrderValues[i];
                    if (GUILayout.Button(new GUIContent(OrderNames[i], OrderTips[i]), sel ? ui.ButtonSelected : ui.Button) && !sel)
                    {
                        s.DistortionOrder = OrderValues[i];
                        changed = true;
                    }
                }
                GUILayout.EndHorizontal();

                if (GUILayout.Button(new GUIContent("Reset to defaults", "Restore the defaults from " + AppConfig.FileName), ui.Button))
                {
                    _defer(() =>
                    {
                        var d = _cfg.CreateDefaultSettings();
                        s.Factor = d.Factor;
                        s.WidthShare = d.WidthShare;
                        s.Algorithm = d.Algorithm;
                        s.ReduceLight = d.ReduceLight;
                        s.EnlargeLight = d.EnlargeLight;
                        s.AntiRinging = d.AntiRinging;
                        s.Widen = d.Widen;
                        s.Direction = d.Direction;
                        s.DistortionAmount = d.DistortionAmount;
                        s.DistortionEdges = d.DistortionEdges;
                        s.DistortionOrder = d.DistortionOrder;
                        _customMode = _cfg.PresetIndexOf(s.Factor) < 0;
                        SyncCustomText(s.Factor);
                        _onChanged();
                    });
                }
            }
            return changed;
        }

        private static bool SnapButton(DesqueezeSettings s, UiStyles ui, double value, string label, string tip)
        {
            bool sel = Math.Abs(s.WidthShare - value) < 1e-9;
            if (GUILayout.Button(new GUIContent(label, tip), sel ? ui.ButtonSelected : ui.Button) && !sel)
            {
                s.WidthShare = value;
                return true;
            }
            return false;
        }

        private static bool LightRow(UiStyles ui, string label, string tip, ref LightSpace value)
        {
            bool changed = false;
            GUILayout.Label(new GUIContent(label, tip), ui.Label);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < LightValues.Length; i++)
            {
                bool sel = value == LightValues[i];
                if (GUILayout.Button(new GUIContent(LightNames[i], tip), sel ? ui.ButtonSelected : ui.Button) && !sel)
                {
                    value = LightValues[i];
                    changed = true;
                }
            }
            GUILayout.EndHorizontal();
            return changed;
        }
    }
}
