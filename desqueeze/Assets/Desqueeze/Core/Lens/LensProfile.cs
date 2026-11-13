using System;
using System.Collections.Generic;

namespace Desqueeze.Core.Lens
{
    /// <summary>
    /// Everything the app knows about one lens: its squeeze factor and, optionally, its distortion with the calibration
    /// lines it was measured from (so the measurement can be edited, or refitted when the factor changes).
    /// Plain serialisable fields so it reads and writes as JSON (Unity's JsonUtility).
    /// </summary>
    [Serializable]
    public sealed class LensProfile
    {
        public string format = "desqueeze-lens-profile";
        public int formatVersion = LensProfileJson.FormatVersion;
        public string name = "New lens";
        public double squeezeFactor = 1.5;
        public string notes = "";
        /// <summary>True when <see cref="distortion"/> holds a measurement.</summary>
        public bool hasDistortion;
        public DistortionModel distortion = new DistortionModel();
        /// <summary>The model the fit chose, in words, and its straightness error after correction (pixels).</summary>
        public string distortionKind = "";
        public double distortionRms;
        public LensCalibration calibration = new LensCalibration();
        public string created = "";
        public string modified = "";

        public LensProfile Clone()
        {
            return new LensProfile
            {
                format = format,
                formatVersion = formatVersion,
                name = name,
                squeezeFactor = squeezeFactor,
                notes = notes,
                hasDistortion = hasDistortion,
                distortion = distortion != null ? distortion.Clone() : new DistortionModel(),
                distortionKind = distortionKind,
                distortionRms = distortionRms,
                calibration = calibration != null ? calibration.Clone() : new LensCalibration(),
                created = created,
                modified = modified,
            };
        }

        /// <summary>The distortion to apply, or null.</summary>
        public DistortionModel Distortion => hasDistortion && distortion != null && !distortion.IsIdentity ? distortion : null;

        /// <summary>Same lens settings (ignores names, notes and dates).</summary>
        public bool SameSettings(LensProfile o)
        {
            if (o == null) return false;
            if (Math.Abs(squeezeFactor - o.squeezeFactor) > 1e-9 || hasDistortion != o.hasDistortion) return false;
            if (!hasDistortion) return true;
            return distortion.SameAs(o.distortion, 1e-12);
        }

        /// <summary>
        /// Refits the distortion from the stored calibration lines (needed when the squeeze factor changes, since the lines
        /// are measured on the squeezed image). Returns the fit, or null if there are no lines.
        /// </summary>
        public DistortionFitResult Refit()
        {
            var c = calibration;
            if (c == null || c.lines == null || c.lines.Count == 0 || c.imageWidth <= 0 || c.imageHeight <= 0) return null;
            var r = DistortionFit.Fit(c.lines, c.imageWidth, c.imageHeight, squeezeFactor);
            c.factor = squeezeFactor;
            if (r.Lines == 0)
            {
                hasDistortion = false;
                distortion = new DistortionModel();
                distortionKind = "";
                distortionRms = 0;
                return r;
            }
            distortion = r.Model;
            hasDistortion = true;
            distortionKind = r.Kind;
            distortionRms = r.RmsAfter;
            return r;
        }
    }

    /// <summary>The calibration lines a distortion was measured from, in the sensor orientation of the image.</summary>
    [Serializable]
    public sealed class LensCalibration
    {
        public int imageWidth, imageHeight;
        /// <summary>The squeeze factor the distortion was last fitted with.</summary>
        public double factor;
        public string sourceImage = "";
        public List<CalibrationLine> lines = new List<CalibrationLine>();

        public LensCalibration Clone()
        {
            var c = new LensCalibration { imageWidth = imageWidth, imageHeight = imageHeight, factor = factor, sourceImage = sourceImage };
            if (lines != null) foreach (var l in lines) c.lines.Add(l.Clone());
            return c;
        }

        public int PointCount
        {
            get
            {
                int n = 0;
                if (lines != null) foreach (var l in lines) n += l.points.Count;
                return n;
            }
        }
    }
}
