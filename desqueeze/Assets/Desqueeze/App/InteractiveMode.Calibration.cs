using System;
using System.Globalization;
using System.Threading.Tasks;
using Desqueeze.App.UI;
using Desqueeze.Core;
using Desqueeze.Core.Calibration;
using UnityEngine;

namespace Desqueeze.App
{
    /// <summary>
    /// Calibration: find a lens' exact squeeze factor from a photographed circle.
    ///
    /// The user clicks inside a squeezed circle; <see cref="CircleFinder"/> measures it. The view then shows a live,
    /// de-squeezed close-up of the circle with a thin red ring whose inside edge sits just outside the radius the circle
    /// really has (its size along the axis the lens did not squeeze). The user changes the factor until the circle
    /// fills the ring exactly; the measured factor is offered as well.
    /// </summary>
    internal sealed partial class InteractiveMode
    {
        private static readonly Color RingColor = new Color(1f, 0.12f, 0.1f, 1f);

        private enum CalTool
        {
            Squeeze = 0,
            Distortion = 1,
        }

        private CalTool _calTool = Prefs.GetString("CalTool", "Squeeze") == "Distortion" ? CalTool.Distortion : CalTool.Squeeze;

        private bool _calibrating;
        private EllipseFit _calFit;
        private CalibrationPreview _calPreview;
        private ImageLayer _calLayer;
        private bool _calRendering;
        private string _calRenderedKey;
        private Vector2 _calDown;
        private bool _calArmed;
        private ImageViewer.ViewState _calPickView;

        public bool Calibrating => _calibrating;

        public void StartCalibration()
        {
            if (Busy || _calibrating) return;
            _calibrating = true;
            _calFit = null;
            _calPreview = null;
            _calRenderedKey = null;
            if (_original != null)
            {
                // Pick on the captured image, at the same spot and zoom the user was looking at.
                bool keep = _viewer.Layer != null;
                _showResult = false;
                _viewer.Show(_originalLayer, keep);
            }
            if (_calTool == CalTool.Distortion) EnterLineTool();
            ShowToolHint();
        }

        private void ShowToolHint()
        {
            if (_calTool == CalTool.Distortion)
                _app.ShowInfo(_original != null
                    ? "Calibration › Distortion: start a line (+ Horizontal / + Vertical) and click along edges that are straight in reality."
                    : "Calibration › Distortion: open a photo of straight lines (a brick wall, a building) first.");
            else
                _app.ShowInfo(_original != null ? "Calibration › Squeeze factor: click inside a squeezed circle." : "Calibration: open a photo of a circle first.");
        }

        private void SetCalTool(CalTool t)
        {
            if (!_calibrating || t == _calTool || Busy) return;
            if (_calTool == CalTool.Squeeze)
            {
                _calFit = null;
                _calPreview = null;
                _calRenderedKey = null;
                DisposeCalibrationLayer();
                if (_original != null) _viewer.Show(_originalLayer, false);
            }
            else
            {
                LeaveLineTool();
            }
            _calTool = t;
            Prefs.SetString("CalTool", t.ToString());
            if (t == CalTool.Distortion) EnterLineTool();
            ShowToolHint();
        }

        /// <summary>Called after a new image is opened while calibrating.</summary>
        private void OnCalibrationImageOpened()
        {
            if (_calTool == CalTool.Distortion) EnterLineTool();
        }

        public void StopCalibration()
        {
            if (!_calibrating) return;
            if (_calTool == CalTool.Distortion) LeaveLineTool();
            _calibrating = false;
            _calFit = null;
            _calPreview = null;
            DisposeCalibrationLayer();
            var layer = _showResult && _result != null ? _resultLayer : _originalLayer;
            _viewer.Show(layer, false);
            if (_original == null) return;
            var lens = _app.Lenses;
            string save = lens.HasProfile && lens.Modified ? " It is in the lens profile \"" + lens.DisplayName + "\": Save it there to keep it." : "";
            if (_calTool == CalTool.Distortion)
            {
                var d = _app.Settings.Distortion;
                _app.ShowInfo(d == null
                    ? "Calibration done: no distortion measured."
                    : "Distortion measured: " + DistortionSummaryText() + "." + save);
            }
            else
            {
                _app.ShowInfo("Calibration done: factor " + F3(_app.Settings.Factor) + "x. Apply De-squeeze uses it for the whole image." + save);
            }
        }

        private string DistortionSummaryText()
        {
            var d = _app.Settings.Distortion;
            if (d == null || _original == null) return "none";
            return LensPanel.Describe(d, LensPanel.HalfHeight(_app.Settings, _original, _app.Lenses.Working)).ToLowerInvariant();
        }

        /// <summary>Back to picking a circle on the captured image, where the last one was picked.</summary>
        private void PickAnotherCircle()
        {
            if (!_calibrating) return;
            _calFit = null;
            _calPreview = null;
            _calRenderedKey = null;
            DisposeCalibrationLayer();
            _viewer.Show(_originalLayer, false);
            _viewer.Restore(_calPickView);
            _app.ShowInfo("Click inside a squeezed circle.");
        }

        /// <summary>Called when a new image is opened while calibrating.</summary>
        private void ResetCalibrationForNewImage()
        {
            _calFit = null;
            _calPreview = null;
            _calRenderedKey = null;
            DisposeCalibrationLayer();
            _lnPreview = false;
            DisposeLinePreviewLayer();
            _lines.Clear();
            _undo.Clear();
            _activeLine = _selLine = _selPt = -1;
        }

        private void DisposeCalibrationLayer()
        {
            if (_calLayer == null) return;
            if (_viewer.Layer == _calLayer) _viewer.Show(null, false);
            _calLayer.Dispose();
            _calLayer = null;
        }

        // ============================================================================ picking

        /// <summary>A click without dragging on the captured image picks the circle (dragging still pans).</summary>
        private void HandleCalibrationClick(Rect view)
        {
            if (_calFit != null || _original == null || Busy || _viewer.Layer != _originalLayer) return;
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 1 && view.Contains(e.mousePosition))
            {
                _calDown = e.mousePosition;
                _calArmed = true;
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _calArmed)
            {
                _calArmed = false;
                double x, y;
                if ((e.mousePosition - _calDown).magnitude <= 5f && _viewer.GuiToImage(view, _calDown, out x, out y))
                    _app.Defer(() => FindCircle(x, y));
            }
        }

        private async void FindCircle(double x, double y)
        {
            if (Busy || !_calibrating || _original == null || _calFit != null) return;
            var src = _original;
            _busy = "Finding the circle";
            _progress.BeginStage("Finding the circle", 0f, 1f);
            _cts = null;
            try
            {
                string err = null;
                var fit = await Task.Run(() => CircleFinder.Find(src, x, y, out err));
                if (!_calibrating || src != _original) return;
                if (fit == null)
                {
                    _app.ShowError(err);
                    return;
                }
                _calPickView = _viewer.Capture();
                _calFit = fit;
                _calRenderedKey = null; // the next frame renders the close-up
                var axis = AxisChooser.Choose(_app.Settings.Direction, src).Axis;
                string warn = fit.Warning;
                _app.ShowInfo("Circle found: " + F1(fit.RadiusX * 2) + " x " + F1(fit.RadiusY * 2) + " px as captured, which means a " +
                              F3(fit.MeasuredFactor(axis)) + "x squeeze." + (warn != null ? " " + warn : ""));
            }
            catch (Exception e)
            {
                _app.ShowError("Could not measure the circle: " + e.Message);
            }
            finally
            {
                _busy = null;
            }
        }

        // ============================================================================ live close-up

        private string PreviewKey(DesqueezeSettings s)
        {
            return s.Factor.ToString("R", CultureInfo.InvariantCulture) + "|" + s.WidthShare.ToString("R", CultureInfo.InvariantCulture) + "|" +
                   AxisChooser.Choose(s.Direction, _original).Axis + "|" + s.Algorithm + "|" + s.ReduceLight + "|" + s.EnlargeLight + "|" +
                   s.AntiRinging.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Re-renders the close-up whenever the settings differ from the ones it shows (one render at a time).</summary>
        private void UpdateCalibrationPreview()
        {
            if (!_calibrating || _calFit == null || _calRendering || _original == null) return;
            string key = PreviewKey(_app.Settings);
            if (key != _calRenderedKey) RenderCalibrationPreview(key);
        }

        private async void RenderCalibrationPreview(string key)
        {
            _calRendering = true;
            var settings = _app.Settings.Clone();
            var fit = _calFit;
            var src = _original;
            var axis = AxisChooser.Choose(settings.Direction, src).Axis;
            try
            {
                var preview = await Task.Run(() => CalibrationPreview.Render(src, fit, settings, axis));
                if (fit != _calFit || !_calibrating) return;
                var layer = await ImageLayer.CreateAsync(preview.Image);
                if (fit != _calFit || !_calibrating)
                {
                    layer.Dispose();
                    return;
                }
                var old = _calLayer;
                var oldPreview = _calPreview;
                _calLayer = layer;
                _calPreview = preview;
                if (old == null || oldPreview == null)
                {
                    _viewer.Show(layer, false); // first close-up: fit it to the view
                }
                else
                {
                    // Keep the zoom, and keep the circle's centre where it was on screen.
                    double vx, vy;
                    _viewer.GetCentre(out vx, out vy);
                    double dx = vx - (oldPreview.CenterX + 0.5), dy = vy - (oldPreview.CenterY + 0.5);
                    _viewer.Show(layer, true);
                    _viewer.SetCentre(preview.CenterX + 0.5 + dx, preview.CenterY + 0.5 + dy);
                    old.Dispose();
                }
                _calRenderedKey = key;
            }
            catch (Exception e)
            {
                _calRenderedKey = key; // don't retry the same settings in a loop
                _app.ShowError("Calibration preview failed: " + e.Message);
            }
            finally
            {
                _calRendering = false;
            }
        }

        /// <summary>The red ring: its inside edge just outside the radius the de-squeezed circle must have.</summary>
        private void DrawCalibrationRing(Rect view, UiStyles ui)
        {
            if (Event.current.type != EventType.Repaint) return;
            var p = _calPreview;
            if (p == null || _calLayer == null || _viewer.Layer != _calLayer) return;
            Vector2 c;
            if (!_viewer.ImageToGui(view, p.CenterX, p.CenterY, out c)) return;
            float s = (float)_viewer.ScreenPixelsPerImagePixel;
            float thickness = Mathf.Max(1.5f, ui.S(1.5f));
            float gap = Mathf.Max(1f, 0.5f * s);              // half an image pixel (at least one screen pixel) outside the edge
            float inner = (float)p.RingRadius * s + gap;
            float outer = inner + thickness;
            GUI.BeginClip(view);
            Vector2 l = c - view.position;
            GUI.DrawTexture(new Rect(l.x - outer, l.y - outer, 2 * outer, 2 * outer), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, true, 0f, RingColor, thickness, outer);
            // A small cross at the centre that was found.
            float arm = ui.S(5), w = Mathf.Max(1f, ui.S(1));
            var old = GUI.color;
            GUI.color = RingColor;
            GUI.DrawTexture(new Rect(l.x - arm, l.y - w * 0.5f, 2 * arm, w), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(l.x - w * 0.5f, l.y - arm, w, 2 * arm), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.EndClip();
        }

        // ============================================================================ controls

        private float CalibrationBarHeight(UiStyles ui)
        {
            return ui.S(78);
        }

        private void DrawCalibrationBar(Rect r, UiStyles ui)
        {
            GUILayout.BeginArea(r, ui.Bar);
            GUILayout.BeginHorizontal();
            var title = new GUIStyle(ui.Section) { wordWrap = false };
            title.normal.textColor = RingColor;
            GUILayout.Label("CALIBRATION", title, GUILayout.Width(ui.S(104)));
            GUI.enabled = !Busy;
            if (GUILayout.Button(new GUIContent("Squeeze factor", "Find the exact squeeze factor from a photographed circle"),
                    _calTool == CalTool.Squeeze ? ui.TabSelected : ui.Tab, GUILayout.Width(ui.S(116))))
                _app.Defer(() => SetCalTool(CalTool.Squeeze));
            if (GUILayout.Button(new GUIContent("Distortion", "Measure the lens' distortion from lines that are straight in reality"),
                    _calTool == CalTool.Distortion ? ui.TabSelected : ui.Tab, GUILayout.Width(ui.S(96))))
                _app.Defer(() => SetCalTool(CalTool.Distortion));
            GUI.enabled = true;
            GUILayout.Space(ui.S(8));
            string prompt = _calTool == CalTool.Distortion ? LinePrompt() : _original == null
                ? "Open a photo of a circle: a round object shot straight on, near the middle of the frame."
                : _calFit == null
                    ? "Click inside a squeezed circle: a round object shot straight on, near the middle of the frame."
                    : "Change the factor until the circle exactly fills the red ring, touching its inside all the way round.";
            var promptStyle = new GUIStyle(ui.Label) { wordWrap = true };
            GUILayout.Label(prompt, promptStyle, GUILayout.ExpandWidth(true));
            GUI.enabled = !Busy;
            if (_original == null && GUILayout.Button(new GUIContent("Open…", _calTool == CalTool.Distortion ? "Open a photo of straight lines (Ctrl/Cmd+O)" : "Open a photo of a circle (Ctrl/Cmd+O)"), ui.Button, GUILayout.Width(ui.S(80))))
                _app.Defer(OpenWithDialog);
            if (_calTool == CalTool.Squeeze && _calFit != null && GUILayout.Button(new GUIContent("Pick another circle", "Measure a different circle"), ui.Button, GUILayout.Width(ui.S(150))))
                _app.Defer(PickAnotherCircle);
            if (GUILayout.Button(new GUIContent("Done", "Leave calibration and keep the factor (Esc)"), ui.Primary, GUILayout.Width(ui.S(80))))
                _app.Defer(StopCalibration);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (_calTool == CalTool.Distortion)
            {
                DrawLineToolRow(ui);
            }
            else if (_calFit != null && _original != null)
            {
                var axis = AxisChooser.Choose(_app.Settings.Direction, _original).Axis;
                GUILayout.Label("Factor", ui.Label);
                GUILayout.Label(F3(_app.Settings.Factor) + "x", ui.Value, GUILayout.Width(ui.S(62)));
                NudgeButton(ui, -0.01, "−0.01");
                NudgeButton(ui, -0.001, "−0.001");
                NudgeButton(ui, +0.001, "+0.001");
                NudgeButton(ui, +0.01, "+0.01");
                GUILayout.Space(ui.S(16));
                double measured = _calFit.MeasuredFactor(axis);
                GUILayout.Label(new GUIContent("Measured " + F3(measured) + "x",
                    "From the circle's " + (axis == SqueezeAxis.Horizontal ? "height / width" : "width / height") +
                    " as captured. Lens distortion and a circle that isn't facing the camera squarely both change it."), ui.Label);
                double rounded = Math.Round(measured, 3);
                GUI.enabled = !Busy && Math.Abs(rounded - _app.Settings.Factor) > 1e-9;
                if (GUILayout.Button(new GUIContent("Use", "Set the factor to the measured value"), ui.Button, GUILayout.Width(ui.S(52))))
                    _app.Defer(() => SetCalibrationFactor(rounded));
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("Drag to pan, scroll to zoom. A click without dragging picks the circle.", ui.MutedSmall);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("-", "Zoom out (-)"), ui.Button, GUILayout.Width(ui.S(30)))) _viewer.ZoomOut();
            GUILayout.Label(_viewer.ZoomLabel, new GUIStyle(ui.Label) { alignment = TextAnchor.MiddleCenter }, GUILayout.Width(ui.S(92)));
            if (GUILayout.Button(new GUIContent("+", "Zoom in (+)"), ui.Button, GUILayout.Width(ui.S(30)))) _viewer.ZoomIn();
            if (GUILayout.Button(new GUIContent("Fit", "Largest exact zoom that fits (F)"), ui.Button, GUILayout.Width(ui.S(44)))) _viewer.Fit();
            GUILayout.EndHorizontal();
            _app.CaptureTooltip();
            GUILayout.EndArea();
        }

        private void NudgeButton(UiStyles ui, double delta, string label)
        {
            GUI.enabled = !Busy;
            if (GUILayout.Button(new GUIContent(label, "Change the factor by " + label.Replace('−', '-') + " ([ and ], Shift for 0.01)"), ui.Button, GUILayout.Width(ui.S(56))))
                _app.Defer(() => SetCalibrationFactor(_app.Settings.Factor + delta));
            GUI.enabled = true;
        }

        private void SetCalibrationFactor(double f)
        {
            f = Math.Round(f, 3);
            f = Math.Max(_app.Config.customFactorMin, Math.Min(_app.Config.customFactorMax, f));
            if (Math.Abs(f - _app.Settings.Factor) < 1e-12) return;
            _app.Settings.Factor = f;
            Prefs.SaveSettings(_app.Settings);
        }

        /// <summary>Keys while calibrating: Esc leaves, [ and ] change the factor by 0.001 (0.01 with Shift).</summary>
        private bool HandleCalibrationKeys(Event e)
        {
            if (!_calibrating || e.type != EventType.KeyDown) return false;
            if (e.keyCode == KeyCode.Escape)
            {
                if (!(_calTool == CalTool.Distortion && LineToolEscape())) _app.Defer(StopCalibration);
                e.Use();
                return true;
            }
            if (_calTool == CalTool.Distortion) return HandleLineKeys(e);
            if (GUIUtility.keyboardControl != 0 || _calFit == null || Busy) return false;
            double step = e.shift ? 0.01 : 0.001;
            if (e.keyCode == KeyCode.LeftBracket) { _app.Defer(() => SetCalibrationFactor(_app.Settings.Factor - step)); e.Use(); return true; }
            if (e.keyCode == KeyCode.RightBracket) { _app.Defer(() => SetCalibrationFactor(_app.Settings.Factor + step)); e.Use(); return true; }
            return false;
        }

        private string CalibrationStatus()
        {
            if (_calTool == CalTool.Distortion) return LineStatus();
            if (_original == null) return "Calibration: no image open";
            if (_calFit == null) return "Calibration: click inside a circle";
            var f = _calFit;
            var p = _calPreview;
            string text = "Circle centre (" + F1(f.CenterX) + ", " + F1(f.CenterY) + ")  ·  " + F1(2 * f.RadiusX) + " x " + F1(2 * f.RadiusY) +
                          " px as captured  ·  edge fit " + f.RmsError.ToString("0.00", CultureInfo.InvariantCulture) + " px RMS over " + f.EdgePoints + " points";
            if (p != null)
            {
                // The unsqueezed axis always matches the ring; the squeezed one only at the right factor.
                bool horiz = p.Axis == SqueezeAxis.Horizontal;
                double off = (horiz ? p.ImageRadiusX : p.ImageRadiusY) / p.RingRadius - 1;
                text += "  ·  at " + F3(p.Factor) + "x the circle is ";
                if (Math.Abs(off) < 0.0005) text += "round";
                else
                    text += (Math.Abs(off) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "% too " +
                            (horiz ? (off < 0 ? "narrow" : "wide") : (off < 0 ? "short" : "tall")) +
                            (off < 0 ? " (raise the factor)" : " (lower the factor)");
            }
            if (_calRendering) text += "  ·  updating…";
            return text;
        }

        private static string F1(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string F3(double v)
        {
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
