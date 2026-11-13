using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Desqueeze.App.IO;
using Desqueeze.Core;
using Desqueeze.Core.Lens;
using UnityEngine;

namespace Desqueeze.App
{
    /// <summary>
    /// The lens profile being used (the "working" profile) and its link to the shared settings.
    ///
    /// While a profile is active its squeeze factor is the factor in the settings (changing one changes the other) and its
    /// distortion is what gets corrected. Changes are kept in the working copy, which is remembered across restarts, until
    /// they are saved to the library; <see cref="Modified"/> says whether the working copy differs from the saved file.
    /// When the factor changes, a distortion measured from calibration lines is refitted for the new factor.
    /// </summary>
    internal sealed class LensProfiles
    {
        private const string WorkingFileName = "working-lens.json";

        private readonly DesqueezeApp _app;
        private string _savedJson;              // canonical text of ActivePath as last loaded or saved
        private int _version, _checkedVersion = -1, _autosavedVersion;
        private bool _modified;
        private float _autosaveAt;
        private bool _refitting;
        private int _linesVersion;

        public LensLibrary Library { get; }
        /// <summary>The profile in use, or null for none.</summary>
        public LensProfile Working { get; private set; }
        /// <summary>The library file the working profile was loaded from or saved to; null if it was never saved.</summary>
        public string ActivePath { get; private set; }
        /// <summary>The latest fit of the working profile's lines (null until something is fitted this session).</summary>
        public DistortionFitResult LastFit { get; private set; }
        /// <summary>Incremented whenever anything about the working profile changes.</summary>
        public int Version => _version;
        /// <summary>
        /// Incremented when a different profile is put in use (chosen, created, imported, reverted, or none), not when the
        /// working one is edited or saved under a new name. The calibration line editor reloads its lines when it changes.
        /// </summary>
        public int Selection { get; private set; }

        public LensProfiles(DesqueezeApp app)
        {
            _app = app;
            Library = new LensLibrary(app.Config.lensProfileFolder);
            Library.Refresh();
            RestoreWorking();
        }

        private static string WorkingPath => Path.Combine(Application.persistentDataPath, WorkingFileName);

        // ============================================================================ state

        public bool HasProfile => Working != null;

        public bool Saved => Working != null && ActivePath != null;

        /// <summary>True when the working profile has changes that are not in its library file (or was never saved).</summary>
        public bool Modified
        {
            get
            {
                if (Working == null) return false;
                if (_checkedVersion != _version)
                {
                    _modified = _savedJson == null || Canonical(Working) != _savedJson;
                    _checkedVersion = _version;
                }
                return _modified;
            }
        }

        public string DisplayName => Working == null ? "No profile" : (string.IsNullOrEmpty(Working.name) ? "Untitled lens" : Working.name);

        private void Changed()
        {
            _version++;
            _autosaveAt = Time.realtimeSinceStartup + 1f;
        }

        private static string Canonical(LensProfile p)
        {
            var c = p.Clone();
            c.created = c.modified = "";
            return LensProfileJson.Write(c);
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);
        }

        /// <summary>Puts the working profile's distortion into the settings.</summary>
        private void ApplyDistortionToSettings()
        {
            var d = Working != null ? Working.Distortion : null;
            _app.Settings.Distortion = d != null ? d.Clone() : null;
        }

        /// <summary>Called every frame: keeps the factor in step, refits after factor changes, autosaves.</summary>
        public void Tick()
        {
            var s = _app.Settings;
            if (Working != null)
            {
                if (Math.Abs(Working.squeezeFactor - s.Factor) > 1e-12)
                {
                    Working.squeezeFactor = s.Factor;
                    Changed();
                }
                var c = Working.calibration;
                if (!_refitting && c != null && c.lines != null && c.lines.Count > 0 && c.imageWidth > 0 && c.imageHeight > 0 &&
                    Math.Abs(c.factor - s.Factor) > 1e-9)
                    Refit();
            }
            if (_version != _autosavedVersion && Time.realtimeSinceStartup >= _autosaveAt) Autosave();
        }

        private async void Refit()
        {
            _refitting = true;
            var copy = Working.Clone();
            var target = Working;
            int lines = _linesVersion;
            try
            {
                var r = await Task.Run(() => copy.Refit());
                if (target != Working || lines != _linesVersion || r == null) return;
                Working.distortion = copy.distortion;
                Working.hasDistortion = copy.hasDistortion;
                Working.distortionKind = copy.distortionKind;
                Working.distortionRms = copy.distortionRms;
                Working.calibration.factor = copy.calibration.factor;
                LastFit = r;
                ApplyDistortionToSettings();
                Changed();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Desqueeze] Distortion refit failed: " + e.Message);
                if (target == Working && Working.calibration != null) Working.calibration.factor = _app.Settings.Factor; // don't retry in a loop
            }
            finally
            {
                _refitting = false;
            }
        }

        // ============================================================================ choosing

        public void Select(LensLibrary.Entry e)
        {
            if (e == null) return;
            LensProfile p;
            try
            {
                p = LensLibrary.Load(e.Path);       // the file as it is now, not as it was when the list was read
            }
            catch (Exception ex)
            {
                _app.ShowError("Could not read " + Path.GetFileName(e.Path) + ": " + ex.Message);
                Library.Refresh();
                return;
            }
            Activate(p, e.Path);
            _app.ShowInfo("Lens profile \"" + DisplayName + "\": " + Summary(p));
        }

        private void Activate(LensProfile p, string path)
        {
            Working = p;
            ActivePath = path;
            _savedJson = path != null ? Canonical(p) : null;
            LastFit = null;
            _linesVersion++;
            Selection++;
            _app.Settings.Factor = p.squeezeFactor;
            ApplyDistortionToSettings();
            Prefs.SaveSettings(_app.Settings);
            Changed();
        }

        /// <summary>Stops using a profile: no distortion correction; the factor stays where it is.</summary>
        public void SelectNone()
        {
            Working = null;
            ActivePath = null;
            _savedJson = null;
            LastFit = null;
            _linesVersion++;
            Selection++;
            ApplyDistortionToSettings();
            Changed();
        }

        /// <summary>A new, unsaved profile with the current factor and no distortion.</summary>
        public void New()
        {
            var p = NewProfile();
            Activate(p, null);
            _app.ShowInfo("New lens profile. Name it, measure the distortion with Calibrate if you like, and Save.");
        }

        private LensProfile NewProfile()
        {
            return new LensProfile
            {
                name = Library.UniqueName("New lens"),
                squeezeFactor = _app.Settings.Factor,
                created = Now(),
                distortion = new DistortionModel(),
                calibration = new LensCalibration(),
            };
        }

        /// <summary>The working profile, creating an unsaved one if there is none.</summary>
        public LensProfile EnsureWorking()
        {
            if (Working == null)
            {
                Working = NewProfile();
                ActivePath = null;
                _savedJson = null;
                Changed();
            }
            return Working;
        }

        /// <summary>
        /// Renames the profile in use. A profile saved in the library is renamed there at once (its file follows the new
        /// name), without saving any other change that is still pending; an unsaved one just takes the name. Returns false,
        /// with a message, if the name is empty or another profile has it.
        /// </summary>
        public bool Rename(string name)
        {
            if (Working == null) return false;
            name = (name ?? "").Trim();
            if (name.Length == 0)
            {
                _app.ShowError("A lens profile needs a name.");
                return false;
            }
            if (name == Working.name) return true;
            Library.Refresh();
            if (Library.NameTaken(name, ActivePath))
            {
                _app.ShowError("There is already a lens profile called \"" + name + "\". Choose another name.");
                return false;
            }
            string old = DisplayName;
            if (ActivePath == null || !File.Exists(ActivePath))
            {
                Working.name = name;
                Changed();
                _app.ShowInfo("Renamed to \"" + name + "\". Save puts it in the library.");
                return true;
            }
            try
            {
                var saved = LensLibrary.Load(ActivePath);       // the file as saved: other pending changes stay pending
                saved.name = name;
                saved.modified = Now();
                ActivePath = Library.Save(saved, ActivePath);
                _savedJson = Canonical(saved);
                Working.name = name;
                Working.modified = saved.modified;
                Changed();
                Autosave();
                _app.ShowInfo("Renamed \"" + old + "\" to \"" + name + "\"" + (Modified ? ". Its other changes are not saved yet." : "."));
                return true;
            }
            catch (Exception e)
            {
                _app.ShowError("Could not rename the profile: " + e.Message);
                return false;
            }
        }

        public void SetNotes(string notes)
        {
            if (Working == null || notes == Working.notes) return;
            Working.notes = notes;
            Changed();
        }

        // ============================================================================ library operations

        public void Save()
        {
            if (Working == null) return;
            try
            {
                if (string.IsNullOrEmpty(Working.name) || Working.name.Trim().Length == 0) Working.name = "Untitled lens";
                Working.name = Working.name.Trim();
                if (Library.NameTaken(Working.name, ActivePath)) Working.name = Library.UniqueName(Working.name);
                if (string.IsNullOrEmpty(Working.created)) Working.created = Now();
                Working.modified = Now();
                ActivePath = Library.Save(Working, ActivePath);
                _savedJson = Canonical(Working);
                Changed();
                Autosave();
                _app.ShowInfo("Saved lens profile \"" + Working.name + "\" (" + ActivePath + ")");
            }
            catch (Exception e)
            {
                _app.ShowError("Could not save the lens profile: " + e.Message);
            }
        }

        /// <summary>Saves the working profile as a new library entry and switches to it.</summary>
        public void SaveAsCopy()
        {
            if (Working == null) return;
            var copy = Working.Clone();
            copy.name = Library.UniqueName(Working.name + " copy");
            copy.created = Now();
            Working = copy;
            ActivePath = null;
            _savedJson = null;
            Save();
        }

        /// <summary>Discards changes: back to the saved file.</summary>
        public void Revert()
        {
            if (Working == null || ActivePath == null) return;
            var e = Library.Find(ActivePath);
            if (e == null && File.Exists(ActivePath)) e = new LensLibrary.Entry { Path = ActivePath };
            if (e == null)
            {
                _app.ShowError("The saved profile is no longer in the library.");
                return;
            }
            Select(e);
        }

        /// <summary>Removes the active profile's file from the library. It stays loaded, as an unsaved profile.</summary>
        public void DeleteActive()
        {
            if (Working == null || ActivePath == null) return;
            try
            {
                string name = Working.name;
                Library.Delete(ActivePath);
                ActivePath = null;
                _savedJson = null;
                Changed();
                Autosave();
                _app.ShowInfo("Deleted \"" + name + "\" from the library. It is still loaded; Save puts it back.");
            }
            catch (Exception e)
            {
                _app.ShowError("Could not delete the profile: " + e.Message);
            }
        }

        /// <summary>Copies a profile file into the library and uses it.</summary>
        public void Import(string path)
        {
            try
            {
                var p = LensLibrary.Load(path);
                if (Library.NameTaken(p.name, null)) p.name = Library.UniqueName(p.name);
                string saved = Library.Save(p, null);
                Activate(LensLibrary.Load(saved), saved);
                _app.ShowInfo("Imported \"" + p.name + "\" into the library: " + Summary(p));
            }
            catch (Exception e)
            {
                _app.ShowError("Could not import " + Path.GetFileName(path) + ": " + e.Message);
            }
        }

        public void Export(string path)
        {
            if (Working == null) return;
            try
            {
                var p = Working.Clone();
                if (string.IsNullOrEmpty(p.created)) p.created = Now();
                p.modified = Now();
                LensLibrary.Write(p, path);
                _app.ShowInfo("Exported \"" + p.name + "\" to " + path);
            }
            catch (Exception e)
            {
                _app.ShowError("Could not export the profile: " + e.Message);
            }
        }

        public void RevealFolder()
        {
            try
            {
                Directory.CreateDirectory(Library.Folder);
                Application.OpenURL(new Uri(Library.Folder).AbsoluteUri);
                _app.ShowInfo("Lens profiles: " + Library.Folder);
            }
            catch (Exception e)
            {
                _app.ShowError("Could not open the profile folder: " + e.Message);
            }
        }

        // ============================================================================ distortion

        /// <summary>
        /// Stores calibration lines (sensor orientation) and their fit in the working profile (creating one if needed) and
        /// applies the fitted distortion.
        /// </summary>
        public void SetCalibration(LensCalibration calibration, DistortionFitResult fit)
        {
            var p = EnsureWorking();
            p.calibration = calibration;
            if (fit != null && fit.Lines > 0)
            {
                p.distortion = fit.Model.Clone();
                p.hasDistortion = !fit.Model.IsIdentity;
                p.distortionKind = fit.Kind;
                p.distortionRms = fit.RmsAfter;
            }
            else
            {
                p.distortion = new DistortionModel();
                p.hasDistortion = false;
                p.distortionKind = "";
                p.distortionRms = 0;
            }
            LastFit = fit;
            _linesVersion++;
            ApplyDistortionToSettings();
            Changed();
        }

        public void ClearDistortion()
        {
            if (Working == null) return;
            Working.distortion = new DistortionModel();
            Working.hasDistortion = false;
            Working.distortionKind = "";
            Working.distortionRms = 0;
            Working.calibration = new LensCalibration();
            LastFit = null;
            _linesVersion++;
            ApplyDistortionToSettings();
            Changed();
        }

        /// <summary>One line about a profile's contents.</summary>
        public static string Summary(LensProfile p)
        {
            string s = p.squeezeFactor.ToString("0.###", CultureInfo.InvariantCulture) + "x";
            var d = p.Distortion;
            if (d == null) return s + ", no distortion correction";
            double hh = p.calibration != null && p.calibration.imageWidth > 0
                ? (double)p.calibration.imageHeight / (p.calibration.imageWidth * p.squeezeFactor)
                : 2.0 / 3.0 / p.squeezeFactor;
            return s + ", " + DistortionSummary.Describe(d, hh).ToLowerInvariant();
        }

        // ============================================================================ persistence

        public void Autosave()
        {
            _autosavedVersion = _version;
            try
            {
                if (Working == null)
                {
                    if (File.Exists(WorkingPath)) File.Delete(WorkingPath);
                    Prefs.SetString("LensProfilePath", "");
                    return;
                }
                LensLibrary.Write(Working, WorkingPath);
                Prefs.SetString("LensProfilePath", ActivePath ?? "");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Desqueeze] Could not remember the working lens profile: " + e.Message);
            }
        }

        private void RestoreWorking()
        {
            try
            {
                if (!File.Exists(WorkingPath)) return;
                var p = LensLibrary.Load(WorkingPath);
                string path = Prefs.GetString("LensProfilePath", "");
                string saved = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        saved = Canonical(LensLibrary.Load(path));
                    }
                    catch (Exception)
                    {
                        path = null;
                    }
                }
                else
                {
                    path = null;
                }
                Working = p;
                ActivePath = path;
                _savedJson = saved;
                ApplyDistortionToSettings();
                _version++;
                _autosavedVersion = _version;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Desqueeze] Could not restore the working lens profile: " + e.Message);
            }
        }
    }
}
