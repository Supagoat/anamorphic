using System;
using System.Globalization;
using System.IO;
using Desqueeze.App.IO;
using Desqueeze.Core;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Lens;
using UnityEngine;

namespace Desqueeze.App.UI
{
    /// <summary>
    /// Side-panel sections for lens profiles (choose, create, save, import, export, delete) and for applying the profile's
    /// distortion correction (strength, auto-crop).
    /// </summary>
    internal sealed class LensPanel
    {
        private readonly LensProfiles _profiles;
        private readonly Action<Action> _defer;
        private readonly Action _onChanged;
        private readonly Action<string> _error;
        private bool _listOpen;
        private bool _notesOpen;
        private bool _renaming, _focusRename;
        private string _renameText = "";
        private LensProfile _renameTarget;
        private const string RenameField = "lensProfileRename";
        private float _confirmDeleteUntil;

        public LensPanel(LensProfiles profiles, Action<Action> defer, Action onChanged, Action<string> error)
        {
            _profiles = profiles;
            _defer = defer;
            _onChanged = onChanged;
            _error = error;
        }

        // ============================================================================ profiles

        /// <summary>Draws the LENS PROFILE section. Returns true if a setting changed.</summary>
        public bool DrawProfile(DesqueezeSettings s, UiStyles ui)
        {
            var p = _profiles.Working;
            bool on = GUI.enabled;
            GUILayout.Label("LENS PROFILE", ui.Section);

            string label = _profiles.DisplayName + (_profiles.Modified ? "  •" : "");
            if (GUILayout.Button(new GUIContent(label + (_listOpen ? "   ^" : "   v"),
                    "Choose a saved lens. A profile holds the squeeze factor and the distortion correction for one lens." +
                    (_profiles.Modified ? " • = changes not saved yet." : "")), ui.Button))
                _defer(() =>
                {
                    if (!_listOpen) _profiles.Library.Refresh();
                    _listOpen = !_listOpen;
                });
            if (_listOpen) DrawList(ui);

            if (_renaming && p != _renameTarget) _defer(CancelRename);    // another profile was chosen meanwhile
            if (p != null)
            {
                if (_renaming) DrawRename(ui);
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Name", ui.Label, GUILayout.Width(ui.S(52)));
                    GUILayout.Label(_profiles.DisplayName, ui.Value, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(new GUIContent("Rename", _profiles.Saved
                            ? "Give this profile a new name. Its file in the library is renamed at once; other unsaved changes stay unsaved."
                            : "Give this profile a name"), ui.Button, GUILayout.Width(ui.S(76))))
                    {
                        string current = p.name ?? "";
                        var target = p;
                        _defer(() =>
                        {
                            _renaming = true;
                            _renameTarget = target;
                            _renameText = current;
                            _focusRename = true;
                        });
                    }
                    GUILayout.EndHorizontal();
                }
                if (_notesOpen)
                {
                    string notes = GUILayout.TextArea(p.notes ?? "", 2000, ui.TextField, GUILayout.MinHeight(ui.S(44)));
                    if (notes != p.notes) _profiles.SetNotes(notes);
                }

                GUILayout.BeginHorizontal();
                GUI.enabled = on && _profiles.Modified;
                if (GUILayout.Button(new GUIContent("Save", _profiles.Saved ? "Save the changes to this profile" : "Save this profile in the library"),
                        _profiles.Modified ? ui.Primary : ui.Button))
                    _defer(_profiles.Save);
                GUI.enabled = on && _profiles.Modified && _profiles.Saved;
                if (GUILayout.Button(new GUIContent("Revert", "Discard the changes and go back to the saved profile"), ui.Button))
                    _defer(_profiles.Revert);
                GUI.enabled = on;
                if (GUILayout.Button(new GUIContent("Save copy", "Save as a new profile, leaving this one as it was saved"), ui.Button))
                    _defer(_profiles.SaveAsCopy);
                if (GUILayout.Button(new GUIContent(_notesOpen ? "Notes ^" : "Notes", "Notes about this lens (kept in the profile)"), ui.Button))
                    _defer(() => _notesOpen = !_notesOpen);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("New", "Start a new profile with the current factor and no distortion correction"), ui.Button))
                _defer(() =>
                {
                    _listOpen = false;
                    _profiles.New();
                });
            if (GUILayout.Button(new GUIContent("Import…", "Add a profile file (.json) to the library and use it"), ui.Button))
                _defer(ImportAsync);
            GUI.enabled = on && p != null;
            if (GUILayout.Button(new GUIContent("Export…", "Write this profile to a file to share or back up"), ui.Button))
                _defer(ExportAsync);
            bool confirming = Time.realtimeSinceStartup < _confirmDeleteUntil;
            GUI.enabled = on && p != null && _profiles.Saved;
            if (GUILayout.Button(new GUIContent(confirming ? "Sure?" : "Delete", confirming ? "Click again to delete the file from the library" : "Delete this profile from the library"),
                    confirming ? ui.ButtonSelected : ui.Button))
            {
                if (confirming)
                {
                    _confirmDeleteUntil = 0;
                    _defer(_profiles.DeleteActive);
                }
                else
                {
                    _confirmDeleteUntil = Time.realtimeSinceStartup + 4f;
                }
            }
            GUI.enabled = on;
            GUILayout.EndHorizontal();

            if (p != null)
            {
                string state = !_profiles.Modified ? "Saved" : _profiles.Saved ? "Changed since saved" : "Not saved yet";
                GUILayout.Label(state + "  ·  " + Summary(p), ui.MutedSmall);
            }
            else
            {
                GUILayout.Label("No lens profile: the squeeze factor is used as is, without distortion correction.", ui.MutedSmall);
            }
            return false;
        }

        /// <summary>The name being edited: Enter or OK renames, Esc or Cancel leaves it as it was.</summary>
        private void DrawRename(UiStyles ui)
        {
            var e = Event.current;
            bool focused = GUI.GetNameOfFocusedControl() == RenameField;
            if (e.type == EventType.KeyDown && focused && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                e.Use();
                string name = _renameText;
                _defer(() => CommitRename(name));
            }
            else if (e.type == EventType.KeyDown && focused && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                _defer(CancelRename);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", ui.Label, GUILayout.Width(ui.S(52)));
            GUI.SetNextControlName(RenameField);
            _renameText = GUILayout.TextField(_renameText ?? "", 80, ui.TextField, GUILayout.ExpandWidth(true));
            if (_focusRename && e.type == EventType.Repaint)
            {
                _focusRename = false;
                GUI.FocusControl(RenameField);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("OK", "Rename (Enter)"), ui.Primary, GUILayout.Width(ui.S(64))))
            {
                string name = _renameText;
                _defer(() => CommitRename(name));
            }
            if (GUILayout.Button(new GUIContent("Cancel", "Keep the current name (Esc)"), ui.Button, GUILayout.Width(ui.S(72))))
                _defer(CancelRename);
            GUILayout.EndHorizontal();
        }

        private void CommitRename(string name)
        {
            if (_profiles.Working != _renameTarget)
            {
                CancelRename();
                return;
            }
            if (!_profiles.Rename(name)) return;     // keeps the field open with the message
            _renaming = false;
            GUIUtility.keyboardControl = 0;
        }

        private void CancelRename()
        {
            _renaming = false;
            GUIUtility.keyboardControl = 0;
        }

        private void DrawList(UiStyles ui)
        {
            var lib = _profiles.Library;
            if (GUILayout.Button(new GUIContent("No profile", "Use the factor below without a lens profile or distortion correction"),
                    _profiles.Working == null ? ui.ButtonSelected : ui.Button))
                _defer(() =>
                {
                    _listOpen = false;
                    _profiles.SelectNone();
                });
            foreach (var e in lib.Entries)
            {
                bool sel = _profiles.ActivePath != null && LensLibrary.SamePath(e.Path, _profiles.ActivePath);
                var entry = e;
                if (GUILayout.Button(new GUIContent(e.Name, Summary(e.Profile) + (string.IsNullOrEmpty(e.Profile.notes) ? "" : "\n" + e.Profile.notes)),
                        sel ? ui.ButtonSelected : ui.Button))
                    _defer(() =>
                    {
                        _listOpen = false;
                        _profiles.Select(entry);
                    });
            }
            if (lib.Entries.Count == 0)
                GUILayout.Label("The library is empty. New starts a profile; Save puts it here.", ui.MutedSmall);
            if (lib.Unreadable.Count > 0)
                GUILayout.Label("Could not read: " + string.Join(", ", lib.Unreadable.ToArray()), ui.Error);
            if (GUILayout.Button(new GUIContent("Open profile folder", lib.Folder), ui.Button))
                _defer(_profiles.RevealFolder);
        }

        private async void ImportAsync()
        {
            string start = Prefs.GetString("ProfileDir", "");
            string path = await FileDialogs.OpenProfileAsync("Import a lens profile", start);
            if (string.IsNullOrEmpty(path)) return;
            Prefs.SetString("ProfileDir", Path.GetDirectoryName(path));
            _listOpen = false;
            _profiles.Import(path);
        }

        private async void ExportAsync()
        {
            var p = _profiles.Working;
            if (p == null) return;
            string start = Prefs.GetString("ProfileDir", "");
            string path = await FileDialogs.SaveProfileAsync("Export lens profile", start, LensLibrary.Slug(p.name) + LensProfileJson.FileExtension);
            if (string.IsNullOrEmpty(path)) return;
            if (string.IsNullOrEmpty(Path.GetExtension(path))) path += LensProfileJson.FileExtension;
            Prefs.SetString("ProfileDir", Path.GetDirectoryName(path));
            _profiles.Export(path);
        }

        // ============================================================================ distortion

        /// <summary>Draws the LENS DISTORTION section with the Distortion Correction strength. Returns true if a setting changed.</summary>
        public bool DrawDistortion(DesqueezeSettings s, UiStyles ui, PixelImage image)
        {
            bool changed = false;
            GUILayout.Label("LENS DISTORTION", ui.Section);
            var model = s.Distortion;
            var p = _profiles.Working;
            if (model == null || model.IsIdentity)
            {
                GUILayout.Label(p == null
                    ? "Choose or create a lens profile, then measure the distortion with Calibrate › Distortion: place points along lines that should be straight (a brick wall, a building)."
                    : "Not measured for this lens. Calibrate › Distortion measures it from a photo of straight lines (a brick wall, a building).", ui.MutedSmall);
                return false;
            }

            double hh = HalfHeight(s, image, p);
            double amountAtStart = s.DistortionAmount;   // what is drawn below must not change within one event
            GUILayout.Label(Describe(model, hh), ui.Label);
            if (p != null && p.calibration != null && p.calibration.lines != null && p.calibration.lines.Count > 0)
                GUILayout.Label("Measured from " + p.calibration.lines.Count + " line" + (p.calibration.lines.Count == 1 ? "" : "s") + " (" +
                                p.calibration.PointCount + " points), straight to " + p.distortionRms.ToString("0.00", CultureInfo.InvariantCulture) +
                                " px  ·  " + p.distortionKind, ui.MutedSmall);

            const string strengthTip = "How much of the measured distortion to correct. At 100% the lines you marked come out straight; less keeps " +
                                       "some of the lens' character, 0% turns the correction off, and more than 100% over-corrects.";
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Distortion Correction", strengthTip), ui.Label);
            GUILayout.FlexibleSpace();
            float amount = Mathf.Round((float)(s.DistortionAmount * 100));
            GUILayout.Label(amount.ToString("0", CultureInfo.InvariantCulture) + "%", ui.Value, GUILayout.Width(ui.S(48)));
            GUILayout.EndHorizontal();
            amount = GUILayout.HorizontalSlider(amount, 0f, 150f, ui.Slider, ui.Thumb, GUILayout.ExpandWidth(true));
            amount = Mathf.Round(amount);
            if (Mathf.Abs(amount - 100f) < 2f) amount = 100f;
            if (Math.Abs(amount / 100.0 - s.DistortionAmount) > 1e-9)
            {
                s.DistortionAmount = amount / 100.0;
                changed = true;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Auto-crop", "Crop to the largest rectangle (same shape as the photo) that the corrected picture fills completely"),
                    s.DistortionEdges == DistortionEdges.AutoCrop ? ui.ButtonSelected : ui.Button) && s.DistortionEdges != DistortionEdges.AutoCrop)
            {
                s.DistortionEdges = DistortionEdges.AutoCrop;
                changed = true;
            }
            if (GUILayout.Button(new GUIContent("Uncropped", "Keep every pixel of the photo; the corners or edges the corrected picture doesn't reach are black (transparent for PNGs with alpha)"),
                    s.DistortionEdges == DistortionEdges.Uncropped ? ui.ButtonSelected : ui.Button) && s.DistortionEdges != DistortionEdges.Uncropped)
            {
                s.DistortionEdges = DistortionEdges.Uncropped;
                changed = true;
            }
            GUILayout.EndHorizontal();

            if (image != null && amountAtStart > 0)
            {
                try
                {
                    int w, h;
                    OutputSize(image, s, out w, out h);
                    GUILayout.Label("Corrected output " + w + " x " + h + " px (" + ((double)w * h / 1e6).ToString("0.0", CultureInfo.InvariantCulture) +
                                    " MP), full resolution at the centre", ui.MutedSmall);
                }
                catch (Exception e)
                {
                    GUILayout.Label(e.Message, ui.Error);
                }
            }
            else
            {
                GUILayout.Label(amountAtStart <= 0 ? "Distortion Correction is at 0%: nothing is corrected." : "Applies to every image de-squeezed with this profile.", ui.MutedSmall);
            }
            return changed;
        }

        // Descriptions need a few hundred inverse-distortion solves each; the GUI asks several times per frame.
        private static readonly System.Collections.Generic.Dictionary<string, string> s_text = new System.Collections.Generic.Dictionary<string, string>();

        private static string Cached(string key, Func<string> make)
        {
            string v;
            if (s_text.TryGetValue(key, out v)) return v;
            if (s_text.Count > 256) s_text.Clear();
            v = make();
            s_text[key] = v;
            return v;
        }

        /// <summary><see cref="DistortionSummary.Describe"/>, cached.</summary>
        internal static string Describe(DistortionModel m, double halfHeight)
        {
            return Cached("d|" + m + "|" + halfHeight.ToString("R", CultureInfo.InvariantCulture), () => DistortionSummary.Describe(m, halfHeight));
        }

        /// <summary><see cref="LensProfiles.Summary"/>, cached.</summary>
        internal static string Summary(LensProfile p)
        {
            var d = p.Distortion;
            string key = "s|" + p.squeezeFactor.ToString("R", CultureInfo.InvariantCulture) + "|" + (d != null ? d.ToString() : "-") + "|" +
                         (p.calibration != null ? p.calibration.imageWidth + "x" + p.calibration.imageHeight : "");
            return Cached(key, () => LensProfiles.Summary(p));
        }

        private static string _sizeKey;
        private static int _sizeW, _sizeH;

        /// <summary><see cref="Desqueezer.OutputSize"/>, cached (it searches for the crop).</summary>
        private static void OutputSize(PixelImage image, DesqueezeSettings s, out int w, out int h)
        {
            var m = s.ActiveDistortion();
            string key = image.Width + "x" + image.Height + "|" + Desqueezer.SensorTurn(image, s.Direction) + "|" + s.Factor.ToString("R", CultureInfo.InvariantCulture) + "|" +
                         s.WidthShare.ToString("R", CultureInfo.InvariantCulture) + "|" + (m != null ? m.ToString() : "-") + "|" + s.DistortionEdges;
            if (key != _sizeKey)
            {
                Desqueezer.OutputSize(image, s, out _sizeW, out _sizeH);
                _sizeKey = key;
            }
            w = _sizeW;
            h = _sizeH;
        }

        /// <summary>Half the frame height in the model's units, for the open image (or the image the lens was measured on).</summary>
        internal static double HalfHeight(DesqueezeSettings s, PixelImage image, LensProfile p)
        {
            if (image != null)
            {
                bool swap = Orientation.SwapsAxes(Desqueezer.SensorTurn(image, s.Direction));
                double sw = swap ? image.Height : image.Width, sh = swap ? image.Width : image.Height;
                return sh / (sw * s.Factor);
            }
            if (p != null && p.calibration != null && p.calibration.imageWidth > 0)
                return (double)p.calibration.imageHeight / (p.calibration.imageWidth * s.Factor);
            return 2.0 / 3.0 / s.Factor;
        }
    }
}
