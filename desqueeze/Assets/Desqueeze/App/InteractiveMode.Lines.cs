using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Desqueeze.App.UI;
using Desqueeze.Core;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Lens;
using Desqueeze.Core.Resampling;
using UnityEngine;

namespace Desqueeze.App
{
    /// <summary>
    /// Calibration › Distortion: measure a lens' distortion from lines that are straight in reality.
    ///
    /// The user starts a horizontal or a vertical line and clicks points along it (a mortar joint, a window frame, the edge
    /// of a building), as many lines and points as they like. Points can be dragged, nudged with the arrow keys and deleted;
    /// with Snap on, each point moves onto the nearest edge to a fraction of a pixel. After every change the distortion is
    /// fitted (<see cref="DistortionFit"/>) and stored in the working lens profile; the overlay shows where the fitted lens
    /// puts each line, and Preview shows the corrected photo with straight lines through the corrected points.
    ///
    /// Points are edited on the photo as it is shown (upright) and stored in the profile in the sensor's orientation, the
    /// frame the de-squeeze works in (see <see cref="Desqueezer.SensorTurn"/>).
    /// </summary>
    internal sealed partial class InteractiveMode
    {
        private static readonly Color HorizontalColor = new Color(0.30f, 0.82f, 1f, 1f);
        private static readonly Color VerticalColor = new Color(1f, 0.62f, 0.22f, 1f);
        private static readonly Color CurveColor = new Color(0.45f, 1f, 0.45f, 0.9f);
        private static readonly Color OutlierColor = new Color(1f, 0.2f, 0.2f, 1f);

        private sealed class EditLine
        {
            public bool Horizontal;              // as shown (upright)
            public List<Vector2d> Points = new List<Vector2d>();
            public int Polarity;                 // edge polarity the snapped points share (0 = not known yet)

            public EditLine Clone()
            {
                return new EditLine { Horizontal = Horizontal, Points = new List<Vector2d>(Points), Polarity = Polarity };
            }
        }

        internal struct Vector2d
        {
            public double X, Y;

            public Vector2d(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        private readonly List<EditLine> _lines = new List<EditLine>();
        private readonly List<List<EditLine>> _undo = new List<List<EditLine>>();
        private int _activeLine = -1;
        private int _selLine = -1, _selPt = -1;
        private bool _lnDragging, _lnDragMoved;
        private Vector2 _lnDragOffset;
        private Vector2 _lnDown;
        private bool _lnArmed;
        private bool _snap = true;
        private int _lnEditVersion, _lnFitVersion;
        private bool _lnFitting;
        private int _lnTurnUsed;
        private float _lnConfirmClearUntil;

        private bool _lnPreview;
        private ImageLayer _lnPreviewLayer;
        private string _lnPreviewKey;
        private bool _lnPreviewRendering;

        // fitted curves for the overlay, cached
        private List<List<Vector2d>> _lnCurves;
        private string _lnCurvesKey;

        private bool LineTool => _calibrating && _calTool == CalTool.Distortion;

        // ============================================================================ coordinates

        private int SensorTurnNow => _original != null ? Desqueezer.SensorTurn(_original, _app.Settings.Direction) : Orientation.Normal;

        private void SensorSize(int turn, out int sw, out int sh)
        {
            bool swap = Orientation.SwapsAxes(turn);
            sw = swap ? _original.Height : _original.Width;
            sh = swap ? _original.Width : _original.Height;
        }

        private void ToSensor(int turn, double x, double y, out double sx, out double sy)
        {
            Orientation.MapPoint(turn, _original.Width, _original.Height, x, y, out sx, out sy);
        }

        private void FromSensor(int turn, double sx, double sy, out double x, out double y)
        {
            int sw, sh;
            SensorSize(turn, out sw, out sh);
            Orientation.MapPoint(Orientation.Inverse(turn), sw, sh, sx, sy, out x, out y);
        }

        // ============================================================================ entering and leaving

        /// <summary>Called when the distortion tool becomes active or a new image is opened in it.</summary>
        private void EnterLineTool()
        {
            _lnArmed = false;
            _lnDragging = false;
            SetLinePreview(false);
            if (_original != null && _viewer.Layer != _originalLayer) _viewer.Show(_originalLayer, true);
            LoadLinesFromProfile();
        }

        private void LeaveLineTool()
        {
            FinishLine();
            _lnArmed = false;
            _lnDragging = false;
            SetLinePreview(false);
            DisposeLinePreviewLayer();
        }

        /// <summary>Shows the working profile's calibration lines if they were placed on an image of this size.</summary>
        private void LoadLinesFromProfile()
        {
            _lines.Clear();
            _undo.Clear();
            _activeLine = _selLine = _selPt = -1;
            _lnCurves = null;
            _lnCurvesKey = null;
            if (_original == null) return;
            int turn = SensorTurnNow;
            _lnTurnUsed = turn;
            var p = _app.Lenses.Working;
            var c = p != null ? p.calibration : null;
            if (c == null || c.lines == null || c.lines.Count == 0)
            {
                _lnFitVersion = _lnEditVersion;
                return;
            }
            int sw, sh;
            SensorSize(turn, out sw, out sh);
            if (c.imageWidth != sw || c.imageHeight != sh)
            {
                _lnFitVersion = _lnEditVersion;
                _app.ShowInfo("\"" + p.name + "\" was measured on a " + c.imageWidth + " x " + c.imageHeight + " photo; this one is " + sw + " x " + sh +
                              " (as the lens saw it). Lines you place here replace that measurement.");
                return;
            }
            bool swap = Orientation.SwapsAxes(turn);
            foreach (var cl in c.lines)
            {
                var l = new EditLine { Horizontal = cl.IsHorizontal != swap };
                foreach (var pt in cl.points)
                {
                    double x, y;
                    FromSensor(turn, pt.x, pt.y, out x, out y);
                    l.Points.Add(new Vector2d(x, y));
                }
                SortLine(l);
                _lines.Add(l);
            }
            _lnEditVersion++;     // fit once so the overlay has residuals
        }

        // ============================================================================ editing

        private List<EditLine> Snapshot()
        {
            var s = new List<EditLine>(_lines.Count);
            foreach (var l in _lines) s.Add(l.Clone());
            return s;
        }

        private void PushUndo()
        {
            _undo.Add(Snapshot());
            if (_undo.Count > 200) _undo.RemoveAt(0);
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;
            var s = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _lines.Clear();
            _lines.AddRange(s);
            if (_activeLine >= _lines.Count) _activeLine = -1;
            _selLine = _selPt = -1;
            LinesEdited();
        }

        private void LinesEdited()
        {
            _lnEditVersion++;
            if (_lnPreview) SetLinePreview(false);
        }

        private void NewLine(bool horizontal)
        {
            if (_original == null) return;
            FinishLine();
            PushUndo();
            _lines.Add(new EditLine { Horizontal = horizontal });
            _activeLine = _lines.Count - 1;
            _selLine = _selPt = -1;
            if (_lnPreview) SetLinePreview(false);
            _app.ShowInfo("New " + (horizontal ? "horizontal" : "vertical") + " line: click points along something " + (horizontal ? "level" : "upright") +
                          " that is straight in reality, from one end to the other. Enter finishes the line.");
        }

        /// <summary>Stops adding points to the active line; an empty line is removed.</summary>
        private void FinishLine()
        {
            if (_activeLine < 0 || _activeLine >= _lines.Count)
            {
                _activeLine = -1;
                return;
            }
            var l = _lines[_activeLine];
            if (l.Points.Count == 0)
            {
                _lines.RemoveAt(_activeLine);
                _selLine = _selPt = -1;
            }
            else if (l.Points.Count < 3)
            {
                _app.ShowInfo("A line needs at least 3 points to count; this one has " + l.Points.Count + ". Select one of its points to add more.");
            }
            _activeLine = -1;
        }

        private void DeleteSelectedPoint()
        {
            if (_selLine < 0 || _selLine >= _lines.Count) return;
            var l = _lines[_selLine];
            if (_selPt < 0 || _selPt >= l.Points.Count) return;
            PushUndo();
            l.Points.RemoveAt(_selPt);
            if (l.Points.Count == 0 && _activeLine != _selLine)
            {
                _lines.RemoveAt(_selLine);
                if (_activeLine > _selLine) _activeLine--;
                _selLine = _selPt = -1;
            }
            else
            {
                _selPt = Math.Min(_selPt, l.Points.Count - 1);
                if (_selPt < 0) _selLine = -1;
            }
            LinesEdited();
        }

        /// <summary>Deletes the line being drawn, or the line of the selected point.</summary>
        private void DeleteCurrentLine()
        {
            int i = _activeLine >= 0 ? _activeLine : _selLine;
            if (i < 0 || i >= _lines.Count) return;
            PushUndo();
            _lines.RemoveAt(i);
            _activeLine = -1;
            _selLine = _selPt = -1;
            LinesEdited();
        }

        private void ClearLines()
        {
            if (_lines.Count == 0) return;
            PushUndo();
            _lines.Clear();
            _activeLine = _selLine = _selPt = -1;
            LinesEdited();
            _app.ShowInfo("All lines removed (Undo brings them back). The profile has no distortion correction now.");
        }

        private static void SortLine(EditLine l)
        {
            if (l.Horizontal) l.Points.Sort((a, b) => a.X.CompareTo(b.X));
            else l.Points.Sort((a, b) => a.Y.CompareTo(b.Y));
        }

        /// <summary>Inserts a point in order along its line; returns its index.</summary>
        private static int InsertSorted(EditLine l, Vector2d p)
        {
            int i = 0;
            while (i < l.Points.Count && (l.Horizontal ? l.Points[i].X < p.X : l.Points[i].Y < p.Y)) i++;
            l.Points.Insert(i, p);
            return i;
        }

        /// <summary>Moves (x, y) onto the nearest edge across the line, keeping the line's edge polarity.</summary>
        private void SnapPoint(EditLine l, ref double x, ref double y)
        {
            if (!_snap || _original == null) return;
            double scale = Math.Max(1e-6, _viewer.ScreenPixelsPerImagePixel);
            double radius = Math.Max(3, Math.Min(40, 8 / scale * UiStyles.PixelsPerPoint));
            double sx, sy;
            int pol;
            if (EdgeSnap.Snap(_original, x, y, l.Horizontal, radius, l.Polarity, out sx, out sy, out pol))
            {
                x = sx;
                y = sy;
                if (l.Polarity == 0) l.Polarity = pol;
            }
        }

        private void AddPoint(double x, double y)
        {
            if (_activeLine < 0 || _activeLine >= _lines.Count)
            {
                _app.ShowInfo("Start a line first: + Horizontal (H) or + Vertical (V), then click along it. Click a point to select it.");
                return;
            }
            var l = _lines[_activeLine];
            PushUndo();
            SnapPoint(l, ref x, ref y);
            x = Math.Max(0, Math.Min(_original.Width - 1, x));
            y = Math.Max(0, Math.Min(_original.Height - 1, y));
            _selLine = _activeLine;
            _selPt = InsertSorted(l, new Vector2d(x, y));
            LinesEdited();
        }

        private void NudgeSelected(double dx, double dy)
        {
            if (_selLine < 0 || _selLine >= _lines.Count || _selPt < 0 || _selPt >= _lines[_selLine].Points.Count) return;
            PushUndo();
            var l = _lines[_selLine];
            var p = l.Points[_selPt];
            l.Points[_selPt] = new Vector2d(Math.Max(0, Math.Min(_original.Width - 1, p.X + dx)), Math.Max(0, Math.Min(_original.Height - 1, p.Y + dy)));
            LinesEdited();
        }

        // ============================================================================ fitting

        /// <summary>The editor's lines as a calibration in the sensor's orientation.</summary>
        private LensCalibration BuildCalibration(int turn)
        {
            int sw, sh;
            SensorSize(turn, out sw, out sh);
            bool swap = Orientation.SwapsAxes(turn);
            var c = new LensCalibration
            {
                imageWidth = sw,
                imageHeight = sh,
                factor = _app.Settings.Factor,
                sourceImage = System.IO.Path.GetFileName(_path),
            };
            foreach (var l in _lines)
            {
                var cl = new CalibrationLine { orientation = (l.Horizontal != swap) ? "horizontal" : "vertical" };
                foreach (var p in l.Points)
                {
                    double x, y;
                    ToSensor(turn, p.X, p.Y, out x, out y);
                    cl.points.Add(new LinePoint(x, y));
                }
                c.lines.Add(cl);
            }
            return c;
        }

        /// <summary>Per frame: refits after edits (not while a point is being dragged) and keeps the preview current.</summary>
        private void UpdateLineTool()
        {
            if (_original == null) return;
            int turn = SensorTurnNow;
            if (turn != _lnTurnUsed)
            {
                _lnTurnUsed = turn;
                if (_lines.Count > 0) _lnEditVersion++;     // the direction setting changed: same points, another frame
            }
            if (_lnEditVersion != _lnFitVersion && !_lnFitting && !_lnDragging) FitLines();
            if (_lnPreview && !_lnPreviewRendering && !Busy)
            {
                string key = LinePreviewKey();
                if (key != _lnPreviewKey)
                {
                    // Start outside OnGUI: rendering makes the app busy, which changes the side panel's controls.
                    _lnPreviewRendering = true;
                    _app.Defer(() => RenderLinePreview(key));
                }
            }
        }

        private async void FitLines()
        {
            _lnFitting = true;
            int version = _lnEditVersion;
            int turn = SensorTurnNow;
            var cal = BuildCalibration(turn);
            double f = _app.Settings.Factor;
            try
            {
                var fit = await Task.Run(() => DistortionFit.Fit(cal.lines, cal.imageWidth, cal.imageHeight, f));
                if (!LineTool || version != _lnEditVersion) return;    // edited meanwhile: the next frame fits again
                cal.factor = f;
                _app.Lenses.SetCalibration(cal, fit);
                _lnFitVersion = version;
            }
            catch (Exception e)
            {
                _lnFitVersion = version;
                _app.ShowError("Distortion fit failed: " + e.Message);
            }
            finally
            {
                _lnFitting = false;
            }
        }

        /// <summary>The fit of the lines on screen, if the profile's latest fit is of exactly these lines.</summary>
        private DistortionFitResult CurrentFit()
        {
            var fit = _app.Lenses.LastFit;
            if (fit == null || _lnFitVersion != _lnEditVersion || fit.PointError == null || fit.PointError.Length != _lines.Count) return null;
            return fit;
        }

        // ============================================================================ preview

        private string LinePreviewKey()
        {
            var s = _app.Settings;
            var m = s.ActiveDistortion();
            return (m != null ? m.ToString() : "none") + "|" + s.Factor.ToString("R", CultureInfo.InvariantCulture) + "|" + SensorTurnNow;
        }

        private void SetLinePreview(bool on)
        {
            if (on && (_original == null || _app.Settings.ActiveDistortion() == null))
            {
                _app.ShowInfo(_original == null ? "Open a photo first." : "Nothing to preview yet: add lines (3+ points each) so a distortion can be measured, and set Amount above 0%.");
                on = false;
            }
            if (on == _lnPreview) return;
            _lnPreview = on;
            if (on)
            {
                FinishLine();
                _lnPreviewKey = null;      // render on the next frame
            }
            else
            {
                if (_original != null && _viewer.Layer != _originalLayer) _viewer.Show(_originalLayer, true);
                DisposeLinePreviewLayer();     // a full-size image; render again when asked
            }
        }

        private void DisposeLinePreviewLayer()
        {
            if (_lnPreviewLayer == null) return;
            if (_viewer.Layer == _lnPreviewLayer) _viewer.Show(_originalLayer, true);
            _lnPreviewLayer.Dispose();
            _lnPreviewLayer = null;
            _lnPreviewKey = null;
        }

        /// <summary>
        /// The photo corrected at its own resolution, squeeze left in (straight lines stay straight under the de-squeeze, so
        /// this is enough to judge them), on the same frame so the view and the points line up.
        /// </summary>
        private async void RenderLinePreview(string key)
        {
            _lnPreviewRendering = true;
            if (Busy || !LineTool || !_lnPreview || _original == null)
            {
                _lnPreviewRendering = false;
                return;
            }
            var s = _app.Settings.Clone();
            var model = s.ActiveDistortion();
            var src = _original;
            int turn = SensorTurnNow;
            _busy = "Rendering the corrected preview";
            _progress.BeginStage("Correcting", 0f, 0.9f);
            _cts = new System.Threading.CancellationTokenSource();
            var ct = _cts.Token;
            try
            {
                if (model == null) return;
                var img = await Task.Run(() =>
                {
                    var sensor = Orientation.Apply(src, turn);
                    double hh = (double)sensor.Height / (sensor.Width * s.Factor);
                    var canvas = new CanvasRect(-1, -hh, 1, hh);
                    var opts = new WarpOptions { Kernel = WarpKernel.Bicubic, Light = LightSpace.Gamma, OutputBitDepth = 8 };
                    var corrected = LensCorrection.Correct(sensor, s.Factor, model, canvas, sensor.Width, sensor.Height, opts, _progress, ct);
                    return turn == Orientation.Normal ? corrected : Orientation.Apply(corrected, Orientation.Inverse(turn));
                }, ct);
                if (!LineTool || !_lnPreview || src != _original) return;
                _progress.BeginStage("Preparing view", 0.9f, 0.1f);
                var layer = await ImageLayer.CreateAsync(img);
                if (!LineTool || !_lnPreview || src != _original)
                {
                    layer.Dispose();
                    return;
                }
                var old = _lnPreviewLayer;
                _lnPreviewLayer = layer;
                _viewer.Show(layer, true);
                if (old != null) old.Dispose();
                _lnPreviewKey = key;
            }
            catch (OperationCanceledException)
            {
                _lnPreview = false;
                _lnPreviewKey = null;
                if (_original != null) _viewer.Show(_originalLayer, true);
            }
            catch (Exception e)
            {
                _lnPreviewKey = key;
                _app.ShowError("Preview failed: " + e.Message);
            }
            finally
            {
                _lnPreviewRendering = false;
                _busy = null;
                _cts = null;
            }
        }

        /// <summary>Where the preview shows an upright point of the original.</summary>
        private bool PreviewPoint(DistortionModel model, int turn, double x, double y, out double ox, out double oy)
        {
            int sw, sh;
            SensorSize(turn, out sw, out sh);
            var frame = new LensFrame(sw, sh, _app.Settings.Factor);
            double sx, sy, xd, yd, xu, yu;
            ToSensor(turn, x, y, out sx, out sy);
            frame.ToNormalised(sx, sy, out xd, out yd);
            ox = x;
            oy = y;
            if (!model.Undistort(xd, yd, out xu, out yu)) return false;
            frame.ToPixel(xu, yu, out sx, out sy);
            FromSensor(turn, sx, sy, out ox, out oy);
            return true;
        }

        // ============================================================================ input

        /// <summary>Mouse handling for the line tool; runs before the viewer so point drags don't pan.</summary>
        private void HandleLineInput(Rect view)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (_original == null || Busy || _lnPreview || _viewer.Layer != _originalLayer) return;

            if (GUIUtility.hotControl == id && _lnDragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    double x, y;
                    _viewer.GuiToImage(view, e.mousePosition + _lnDragOffset, out x, out y);
                    var l = _lines[_selLine];
                    l.Points[_selPt] = new Vector2d(Math.Max(0, Math.Min(_original.Width - 1, x)), Math.Max(0, Math.Min(_original.Height - 1, y)));
                    _lnDragMoved = true;
                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    GUIUtility.hotControl = 0;
                    _lnDragging = false;
                    e.Use();
                    if (!_lnDragMoved)
                    {
                        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);   // only selected: nothing to undo
                        return;
                    }
                    var l = _lines[_selLine];
                    var p = l.Points[_selPt];
                    double x = p.X, y = p.Y;
                    SnapPoint(l, ref x, ref y);
                    l.Points.RemoveAt(_selPt);
                    _selPt = InsertSorted(l, new Vector2d(x, y));
                    LinesEdited();
                }
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && view.Contains(e.mousePosition))
            {
                int li, pi;
                if (HitPoint(view, e.mousePosition, out li, out pi))
                {
                    if (_activeLine >= 0 && _activeLine != li)
                    {
                        FinishLine();                                           // may remove an empty line, shifting indices
                        if (!HitPoint(view, e.mousePosition, out li, out pi)) return;
                    }
                    _selLine = li;
                    _selPt = pi;
                    if (_activeLine < 0) _activeLine = li;                     // clicking a point continues its line
                    Vector2 g;
                    _viewer.ImageToGui(view, _lines[_selLine].Points[_selPt].X, _lines[_selLine].Points[_selPt].Y, out g);
                    _lnDragOffset = g - e.mousePosition;
                    PushUndo();
                    _lnDragging = true;
                    _lnDragMoved = false;
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = 0;
                    e.Use();
                    return;
                }
                if (e.clickCount == 1)
                {
                    _lnDown = e.mousePosition;
                    _lnArmed = true;
                }
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _lnArmed)
            {
                _lnArmed = false;
                double x, y;
                if ((e.mousePosition - _lnDown).magnitude <= 5f && _viewer.GuiToImage(view, _lnDown, out x, out y))
                    _app.Defer(() => AddPoint(x, y));
            }
        }

        /// <summary>The point under the mouse (within a few screen points), preferring the active line.</summary>
        private bool HitPoint(Rect view, Vector2 mouse, out int line, out int point)
        {
            line = point = -1;
            float best = Mathf.Max(7f, 7f * UiStyles.PixelsPerPoint);
            best *= best;
            for (int i = 0; i < _lines.Count; i++)
            {
                var l = _lines[i];
                for (int j = 0; j < l.Points.Count; j++)
                {
                    Vector2 g;
                    if (!_viewer.ImageToGui(view, l.Points[j].X, l.Points[j].Y, out g)) continue;
                    float d = (g - mouse).sqrMagnitude * (i == _activeLine ? 0.8f : 1f);
                    if (d < best)
                    {
                        best = d;
                        line = i;
                        point = j;
                    }
                }
            }
            return line >= 0;
        }

        /// <summary>Keys for the line tool. Returns true if the key was used.</summary>
        private bool HandleLineKeys(Event e)
        {
            bool cmd = e.command || e.control;
            if (cmd && e.keyCode == KeyCode.Z)
            {
                if (!Busy) _app.Defer(Undo);
                e.Use();
                return true;
            }
            if (cmd || GUIUtility.keyboardControl != 0 || Busy) return false;
            switch (e.keyCode)
            {
                case KeyCode.H:
                    _app.Defer(() => NewLine(true));
                    e.Use();
                    return true;
                case KeyCode.V:
                    _app.Defer(() => NewLine(false));
                    e.Use();
                    return true;
                case KeyCode.P:
                    _app.Defer(() => SetLinePreview(!_lnPreview));
                    e.Use();
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    _app.Defer(FinishLine);
                    e.Use();
                    return true;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (e.shift) _app.Defer(DeleteCurrentLine);
                    else _app.Defer(DeleteSelectedPoint);
                    e.Use();
                    return true;
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                    if (_selLine < 0 || _lnPreview) return false;   // arrows pan the view
                    double step = e.shift ? 2 : 0.25;
                    double dx = e.keyCode == KeyCode.LeftArrow ? -step : e.keyCode == KeyCode.RightArrow ? step : 0;
                    double dy = e.keyCode == KeyCode.UpArrow ? -step : e.keyCode == KeyCode.DownArrow ? step : 0;
                    _app.Defer(() => NudgeSelected(dx, dy));
                    e.Use();
                    return true;
            }
            return false;
        }

        /// <summary>Esc in the line tool: finish the line, then clear the selection, then (returning false) leave calibration.</summary>
        private bool LineToolEscape()
        {
            if (_lnPreview)
            {
                _app.Defer(() => SetLinePreview(false));
                return true;
            }
            if (_activeLine >= 0)
            {
                _app.Defer(FinishLine);
                return true;
            }
            if (_selLine >= 0)
            {
                _selLine = _selPt = -1;
                return true;
            }
            return false;
        }

        // ============================================================================ drawing

        private void DrawLineOverlay(Rect view, UiStyles ui)
        {
            if (Event.current.type != EventType.Repaint || _original == null || _lines.Count == 0) return;
            if (_lnPreview)
            {
                if (_lnPreviewLayer != null && _viewer.Layer == _lnPreviewLayer) DrawPreviewOverlay(view, ui);
                return;
            }
            if (_viewer.Layer != _originalLayer) return;
            var fit = CurrentFit();
            UpdateCurves();
            float lw = Mathf.Max(1f, ui.S(1f));
            float half = Mathf.Max(2.5f, ui.S(3f));
            if (!Overlay.Begin(view)) return;
            try
            {
                // fitted curves: where the measured lens puts a straight line through each line's points
                if (_lnCurves != null)
                {
                    foreach (var curve in _lnCurves)
                    {
                        if (curve == null) continue;
                        Vector2 prev = default(Vector2);
                        for (int k = 0; k < curve.Count; k++)
                        {
                            Vector2 g;
                            _viewer.ImageToGui(view, curve[k].X, curve[k].Y, out g);
                            if (k > 0) Overlay.Line(prev, g, lw, CurveColor);
                            prev = g;
                        }
                    }
                }
                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    var col = l.Horizontal ? HorizontalColor : VerticalColor;
                    bool active = i == _activeLine;
                    var lineCol = new Color(col.r, col.g, col.b, active ? 0.95f : (l.Points.Count < 3 ? 0.45f : 0.7f));
                    Vector2 prev = default(Vector2);
                    for (int j = 0; j < l.Points.Count; j++)
                    {
                        Vector2 g;
                        _viewer.ImageToGui(view, l.Points[j].X, l.Points[j].Y, out g);
                        if (j > 0) Overlay.Line(prev, g, active ? lw * 1.6f : lw, lineCol);
                        prev = g;
                    }
                }
                double limit = fit != null ? Math.Max(1.5, 3 * fit.RmsAfter) : double.MaxValue;
                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    var col = l.Horizontal ? HorizontalColor : VerticalColor;
                    var errs = fit != null ? fit.PointError[i] : null;
                    for (int j = 0; j < l.Points.Count; j++)
                    {
                        Vector2 g;
                        _viewer.ImageToGui(view, l.Points[j].X, l.Points[j].Y, out g);
                        bool sel = i == _selLine && j == _selPt;
                        bool outlier = errs != null && j < errs.Length && Math.Abs(errs[j]) > limit;
                        Overlay.Square(g, half + lw, new Color(0, 0, 0, 0.75f));
                        Overlay.Square(g, half, outlier ? OutlierColor : col);
                        if (sel) Overlay.Box(g, half + 3 * lw, lw * 1.5f, Color.white);
                    }
                }
            }
            finally
            {
                Overlay.End();
            }
        }

        /// <summary>On the corrected preview: the corrected points and the straight line through each line's points.</summary>
        private void DrawPreviewOverlay(Rect view, UiStyles ui)
        {
            var model = _app.Settings.ActiveDistortion();
            if (model == null) return;
            int turn = SensorTurnNow;
            float lw = Mathf.Max(1f, ui.S(1f));
            float half = Mathf.Max(2f, ui.S(2.5f));
            if (!Overlay.Begin(view)) return;
            try
            {
                foreach (var l in _lines)
                {
                    if (l.Points.Count < 2) continue;
                    var col = l.Horizontal ? HorizontalColor : VerticalColor;
                    var pts = new List<Vector2d>(l.Points.Count);
                    foreach (var p in l.Points)
                    {
                        double ox, oy;
                        if (PreviewPoint(model, turn, p.X, p.Y, out ox, out oy)) pts.Add(new Vector2d(ox, oy));
                    }
                    if (pts.Count < 2) continue;
                    Vector2d a, b;
                    StraightThrough(pts, l.Horizontal, out a, out b);
                    Vector2 ga, gb;
                    _viewer.ImageToGui(view, a.X, a.Y, out ga);
                    _viewer.ImageToGui(view, b.X, b.Y, out gb);
                    Overlay.Line(ga, gb, lw, CurveColor);
                    foreach (var p in pts)
                    {
                        Vector2 g;
                        _viewer.ImageToGui(view, p.X, p.Y, out g);
                        Overlay.Square(g, half + lw, new Color(0, 0, 0, 0.75f));
                        Overlay.Square(g, half, col);
                    }
                }
            }
            finally
            {
                Overlay.End();
            }
        }

        /// <summary>The total-least-squares line through points, from the first to the last point's extent (extended a little).</summary>
        private static void StraightThrough(List<Vector2d> pts, bool horizontal, out Vector2d a, out Vector2d b)
        {
            double mx = 0, my = 0;
            foreach (var p in pts) { mx += p.X; my += p.Y; }
            mx /= pts.Count;
            my /= pts.Count;
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var p in pts)
            {
                double dx = p.X - mx, dy = p.Y - my;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
            }
            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double ux = Math.Cos(angle), uy = Math.Sin(angle);
            double t0 = double.MaxValue, t1 = double.MinValue;
            foreach (var p in pts)
            {
                double t = (p.X - mx) * ux + (p.Y - my) * uy;
                t0 = Math.Min(t0, t);
                t1 = Math.Max(t1, t);
            }
            double ext = 0.05 * (t1 - t0) + 10;
            a = new Vector2d(mx + (t0 - ext) * ux, my + (t0 - ext) * uy);
            b = new Vector2d(mx + (t1 + ext) * ux, my + (t1 + ext) * uy);
        }

        /// <summary>Recomputes the fitted curves when the lines, the model or the frame changed.</summary>
        private void UpdateCurves()
        {
            var p = _app.Lenses.Working;
            var model = p != null ? p.Distortion : null;
            int turn = SensorTurnNow;
            string key = _lnEditVersion + "|" + _app.Lenses.Version + "|" + turn + "|" + _app.Settings.Factor.ToString("R", CultureInfo.InvariantCulture);
            if (key == _lnCurvesKey) return;
            _lnCurvesKey = key;
            _lnCurves = null;
            if (model == null || _lnFitVersion != _lnEditVersion) return;
            int sw, sh;
            SensorSize(turn, out sw, out sh);
            var cal = BuildCalibration(turn);
            _lnCurves = new List<List<Vector2d>>(cal.lines.Count);
            foreach (var cl in cal.lines)
            {
                if (cl.points.Count < 3)
                {
                    _lnCurves.Add(null);
                    continue;
                }
                var curve = DistortionFit.FittedCurve(cl, model, sw, sh, _app.Settings.Factor, 96);
                var up = new List<Vector2d>(curve != null ? curve.Count : 0);
                if (curve != null)
                {
                    foreach (var c in curve)
                    {
                        double x, y;
                        FromSensor(turn, c.x, c.y, out x, out y);
                        up.Add(new Vector2d(x, y));
                    }
                }
                _lnCurves.Add(up);
            }
        }

        // ============================================================================ toolbar and status

        private void DrawLineToolRow(UiStyles ui)
        {
            bool on = !Busy && _original != null;
            GUI.enabled = on && !_lnPreview;
            if (GUILayout.Button(new GUIContent("+ Horizontal", "Start a horizontal line (H): click points along something level that is straight in reality"), ui.Button, GUILayout.Width(ui.S(104))))
                _app.Defer(() => NewLine(true));
            if (GUILayout.Button(new GUIContent("+ Vertical", "Start a vertical line (V): click points along something upright that is straight in reality"), ui.Button, GUILayout.Width(ui.S(90))))
                _app.Defer(() => NewLine(false));
            GUI.enabled = on && !_lnPreview && _activeLine >= 0;
            if (GUILayout.Button(new GUIContent("Finish line", "Stop adding points to this line (Enter)"), ui.Button, GUILayout.Width(ui.S(88))))
                _app.Defer(FinishLine);
            GUI.enabled = on && !_lnPreview && (_activeLine >= 0 || _selLine >= 0);
            if (GUILayout.Button(new GUIContent("Delete line", "Delete the line being drawn or the selected point's line (Shift+Delete). Delete alone removes the selected point."), ui.Button, GUILayout.Width(ui.S(88))))
                _app.Defer(DeleteCurrentLine);
            GUI.enabled = on && _undo.Count > 0;
            if (GUILayout.Button(new GUIContent("Undo", "Undo the last change to the lines (Ctrl/Cmd+Z)"), ui.Button, GUILayout.Width(ui.S(56))))
                _app.Defer(Undo);
            bool confirming = Time.realtimeSinceStartup < _lnConfirmClearUntil;
            GUI.enabled = on && !_lnPreview && _lines.Count > 0;
            if (GUILayout.Button(new GUIContent(confirming ? "Sure?" : "Clear", confirming ? "Click again to remove every line" : "Remove every line (and the distortion measured from them)"),
                    confirming ? ui.ButtonSelected : ui.Button, GUILayout.Width(ui.S(60))))
            {
                if (confirming)
                {
                    _lnConfirmClearUntil = 0;
                    _app.Defer(ClearLines);
                }
                else
                {
                    _lnConfirmClearUntil = Time.realtimeSinceStartup + 4f;
                }
            }
            GUI.enabled = on;
            GUILayout.Space(ui.S(6));
            bool snap = ui.Toggle(_snap, new GUIContent("Snap", "Move each point onto the nearest edge, to a fraction of a pixel, keeping all of a line's points on the same side of it (a mortar joint, a frame)"));
            if (snap != _snap) _snap = snap;
            GUILayout.Space(ui.S(6));
            GUI.enabled = on && (_lnPreview || _app.Settings.ActiveDistortion() != null);
            if (GUILayout.Button(new GUIContent("Preview", "Show the photo corrected with the measured distortion and the current amount, with straight lines through the corrected points (P)"),
                    _lnPreview ? ui.ButtonSelected : ui.Button, GUILayout.Width(ui.S(72))))
                _app.Defer(() => SetLinePreview(!_lnPreview));
            GUI.enabled = true;
        }

        private string LinePrompt()
        {
            if (_original == null) return "Open a photo of straight lines taken through the lens: a brick wall, a building, a grid. Fill the frame, corners included.";
            if (_lnPreview) return "Corrected preview: the points should sit on the straight lines. Esc or Preview returns to editing.";
            if (_activeLine >= 0)
            {
                var l = _lines[_activeLine];
                return "Click along the " + (l.Horizontal ? "horizontal" : "vertical") + " line from end to end (" + l.Points.Count +
                       " point" + (l.Points.Count == 1 ? "" : "s") + " so far; 5-10 spread along it is plenty). Enter finishes it.";
            }
            if (_lines.Count == 0) return "Start a line (H or V), then click points along an edge that is straight in reality. Several lines, spread across the frame and near its edges, measure best.";
            return "Add lines near the edges and corners, where distortion is strongest. Drag a point to move it; click one to continue its line.";
        }

        private string LineStatus()
        {
            if (_original == null) return "Distortion: no image open";
            int h = 0, v = 0, pts = 0, usable = 0;
            foreach (var l in _lines)
            {
                if (l.Horizontal) h++; else v++;
                pts += l.Points.Count;
                if (l.Points.Count >= 3) usable++;
            }
            string text = _lines.Count + " line" + (_lines.Count == 1 ? "" : "s") + " (" + h + " horizontal, " + v + " vertical), " + pts + " points";
            var fit = CurrentFit();
            if (_lnFitting || _lnEditVersion != _lnFitVersion) text += "  ·  fitting…";
            else if (fit != null && fit.Lines > 0)
            {
                text += "  ·  straightness " + fit.RmsBefore.ToString("0.00", CultureInfo.InvariantCulture) + " → " +
                        fit.RmsAfter.ToString("0.00", CultureInfo.InvariantCulture) + " px RMS  ·  " + fit.Kind;
                var p = _app.Lenses.Working;
                if (p != null && p.Distortion != null)
                    text += "  ·  " + LensPanel.Describe(p.Distortion, LensPanel.HalfHeight(_app.Settings, _original, p));
                if (!string.IsNullOrEmpty(fit.Warning)) text += "  ·  " + fit.Warning;
            }
            else if (usable == 0 && _lines.Count > 0) text += "  ·  each line needs 3+ points";
            if (_selLine >= 0 && _selPt >= 0 && _selLine < _lines.Count && _selPt < _lines[_selLine].Points.Count)
            {
                var p = _lines[_selLine].Points[_selPt];
                text += "  ·  point (" + p.X.ToString("0.00", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.00", CultureInfo.InvariantCulture) + ")";
                if (fit != null && fit.PointError[_selLine] != null && _selPt < fit.PointError[_selLine].Length)
                    text += " off its line by " + Math.Abs(fit.PointError[_selLine][_selPt]).ToString("0.00", CultureInfo.InvariantCulture) + " px";
            }
            return text;
        }
    }
}
