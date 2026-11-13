using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Desqueeze.Core.Lens
{
    /// <summary>
    /// Reads and writes lens profiles as JSON (a small self-contained reader/writer, so profiles round-trip exactly
    /// and the format is the same in the app, the bench and the tests).
    /// <code>
    /// {
    ///   "format": "desqueeze-lens-profile", "formatVersion": 1,
    ///   "name": "...", "squeezeFactor": 1.54, "notes": "", "created": "...", "modified": "...",
    ///   "distortion": { "kx": [k1, k2, k3], "ky": [k1, k2, k3],
    ///                   "terms": [ { "i": 1, "j": 2, "x": -0.02, "y": 0 }, ... ],      (optional: general terms x^i y^j)
    ///                   "kind": "...", "rms": 0.9 } | null,
    ///   "calibration": { "imageWidth": 6000, "imageHeight": 4000, "factor": 1.54, "sourceImage": "...",
    ///                    "lines": [ { "orientation": "horizontal", "points": [[x, y], ...] } ] } | null
    /// }
    /// </code>
    /// Distortion coefficients are for the model in <see cref="DistortionModel"/>; calibration points are in pixels of
    /// the image in its sensor orientation (squeeze horizontal), pixel centres at whole numbers.
    /// </summary>
    public static class LensProfileJson
    {
        public const string Format = "desqueeze-lens-profile";
        /// <summary>2 added the general terms; a profile without them is written as version 1, which older versions read.</summary>
        public const int FormatVersion = 2;
        public const string FileExtension = ".json";

        // ============================================================================ writing

        public static string Write(LensProfile p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var sb = new StringBuilder();
            sb.Append("{\n");
            Prop(sb, 1, "format", Str(Format), true);
            Prop(sb, 1, "formatVersion", (p.Distortion != null && p.Distortion.HasTerms ? FormatVersion : 1).ToString(CultureInfo.InvariantCulture), true);
            Prop(sb, 1, "name", Str(p.name ?? ""), true);
            Prop(sb, 1, "squeezeFactor", Num(p.squeezeFactor), true);
            Prop(sb, 1, "notes", Str(p.notes ?? ""), true);
            Prop(sb, 1, "created", Str(p.created ?? ""), true);
            Prop(sb, 1, "modified", Str(p.modified ?? ""), true);
            var d = p.Distortion;
            if (d == null)
            {
                Prop(sb, 1, "distortion", "null", true);
            }
            else
            {
                sb.Append("  \"distortion\": {\n");
                Prop(sb, 2, "kx", NumArray(d.Kx), true);
                Prop(sb, 2, "ky", NumArray(d.Ky), true);
                if (d.HasTerms)
                {
                    sb.Append("    \"terms\": [");
                    bool first = true;
                    foreach (var t in d.Terms)
                    {
                        if (t.X == 0 && t.Y == 0) continue;
                        sb.Append(first ? "\n" : ",\n");
                        first = false;
                        sb.Append("      { \"i\": ").Append(t.I.ToString(CultureInfo.InvariantCulture)).Append(", \"j\": ").Append(t.J.ToString(CultureInfo.InvariantCulture))
                            .Append(", \"x\": ").Append(Num(t.X)).Append(", \"y\": ").Append(Num(t.Y)).Append(" }");
                    }
                    sb.Append("\n    ],\n");
                }
                Prop(sb, 2, "kind", Str(p.distortionKind ?? ""), true);
                Prop(sb, 2, "rms", Num(p.distortionRms), false);
                sb.Append("  },\n");
            }
            var c = p.calibration;
            var lines = new List<CalibrationLine>();
            if (c != null && c.lines != null)
                foreach (var l in c.lines)
                    if (l != null && l.points != null && l.points.Count > 0) lines.Add(l);
            if (lines.Count == 0)
            {
                Prop(sb, 1, "calibration", "null", false);
            }
            else
            {
                sb.Append("  \"calibration\": {\n");
                Prop(sb, 2, "imageWidth", c.imageWidth.ToString(CultureInfo.InvariantCulture), true);
                Prop(sb, 2, "imageHeight", c.imageHeight.ToString(CultureInfo.InvariantCulture), true);
                Prop(sb, 2, "factor", Num(c.factor), true);
                Prop(sb, 2, "sourceImage", Str(c.sourceImage ?? ""), true);
                sb.Append("    \"lines\": [");
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    sb.Append(i == 0 ? "\n" : ",\n");
                    sb.Append("      { \"orientation\": ").Append(Str(l.IsHorizontal ? "horizontal" : "vertical")).Append(", \"points\": [");
                    for (int j = 0; j < l.points.Count; j++)
                    {
                        if (j > 0) sb.Append(", ");
                        sb.Append('[').Append(Num(Math.Round(l.points[j].x, 4))).Append(", ").Append(Num(Math.Round(l.points[j].y, 4))).Append(']');
                    }
                    sb.Append("] }");
                }
                sb.Append("\n    ]\n");
                sb.Append("  }\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void Prop(StringBuilder sb, int indent, string name, string value, bool comma)
        {
            sb.Append(' ', indent * 2).Append(Str(name)).Append(": ").Append(value).Append(comma ? ",\n" : "\n");
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string NumArray(double[] a)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Num(a != null && i < a.Length ? a[i] : 0));
            }
            return sb.Append(']').ToString();
        }

        private static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ============================================================================ reading

        /// <summary>Parses a profile. Throws <see cref="FormatException"/> with a readable message if it isn't one.</summary>
        public static LensProfile Read(string json)
        {
            object root;
            try
            {
                var r = new Reader(json ?? "");
                root = r.ReadValue();
                r.SkipSpace();
                if (!r.End) throw new FormatException("unexpected text after the end");
            }
            catch (FormatException e)
            {
                throw new FormatException("Not a valid JSON file (" + e.Message + ").");
            }
            var o = root as Dictionary<string, object>;
            if (o == null) throw new FormatException("Not a lens profile (expected a JSON object).");
            string format = GetString(o, "format", null);
            if (format != Format) throw new FormatException("Not a lens profile (format is \"" + (format ?? "missing") + "\").");
            int version = (int)GetNumber(o, "formatVersion", 1);
            if (version > FormatVersion)
                throw new FormatException("This lens profile was written by a newer version (format " + version + ").");

            var p = new LensProfile
            {
                name = GetString(o, "name", "Lens"),
                squeezeFactor = GetNumber(o, "squeezeFactor", 1),
                notes = GetString(o, "notes", ""),
                created = GetString(o, "created", ""),
                modified = GetString(o, "modified", ""),
            };
            if (!(p.squeezeFactor > 0) || double.IsInfinity(p.squeezeFactor))
                throw new FormatException("The profile's squeeze factor is not a positive number.");

            var d = Get(o, "distortion") as Dictionary<string, object>;
            if (d != null)
            {
                var m = new DistortionModel();
                ReadCoefficients(d, "kx", m.Kx);
                ReadCoefficients(d, "ky", m.Ky);
                var terms = Get(d, "terms") as List<object>;
                if (terms != null)
                {
                    var list = new List<DistortionTerm>();
                    foreach (var to in terms)
                    {
                        var t = to as Dictionary<string, object>;
                        if (t == null) throw new FormatException("A distortion term is not an object.");
                        double ti = GetNumber(t, "i", -1), tj = GetNumber(t, "j", -1), tx = GetNumber(t, "x", 0), ty = GetNumber(t, "y", 0);
                        if (ti != Math.Floor(ti) || tj != Math.Floor(tj) || ti < 0 || tj < 0 || ti + tj < 2 || ti + tj > DistortionModel.MaxDegree)
                            throw new FormatException("A distortion term has powers out of range (x^" + ti + " y^" + tj + ").");
                        if (double.IsNaN(tx) || double.IsNaN(ty) || Math.Abs(tx) > 1000 || Math.Abs(ty) > 1000)
                            throw new FormatException("A distortion term's coefficient is out of range.");
                        list.Add(new DistortionTerm((int)ti, (int)tj, tx, ty));
                    }
                    m.Terms = list.ToArray();
                }
                p.distortion = m;
                p.hasDistortion = !m.IsIdentity;
                p.distortionKind = GetString(d, "kind", "");
                p.distortionRms = GetNumber(d, "rms", 0);
            }

            var c = Get(o, "calibration") as Dictionary<string, object>;
            if (c != null)
            {
                var cal = new LensCalibration
                {
                    imageWidth = (int)GetNumber(c, "imageWidth", 0),
                    imageHeight = (int)GetNumber(c, "imageHeight", 0),
                    factor = GetNumber(c, "factor", p.squeezeFactor),
                    sourceImage = GetString(c, "sourceImage", ""),
                };
                var lines = Get(c, "lines") as List<object>;
                if (lines != null)
                {
                    foreach (var lo in lines)
                    {
                        var l = lo as Dictionary<string, object>;
                        if (l == null) continue;
                        var line = new CalibrationLine { orientation = GetString(l, "orientation", "horizontal") == "vertical" ? "vertical" : "horizontal" };
                        var pts = Get(l, "points") as List<object>;
                        if (pts != null)
                        {
                            foreach (var po in pts)
                            {
                                var pa = po as List<object>;
                                if (pa == null || pa.Count < 2 || !(pa[0] is double) || !(pa[1] is double)) continue;
                                double x = (double)pa[0], y = (double)pa[1];
                                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
                                line.points.Add(new LinePoint(x, y));
                            }
                        }
                        cal.lines.Add(line);
                    }
                }
                p.calibration = cal;
            }
            return p;
        }

        private static void ReadCoefficients(Dictionary<string, object> d, string key, double[] into)
        {
            var a = Get(d, key) as List<object>;
            if (a == null) return;
            for (int i = 0; i < into.Length && i < a.Count; i++)
            {
                if (!(a[i] is double)) throw new FormatException("Distortion coefficient " + key + "[" + i + "] is not a number.");
                double v = (double)a[i];
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 10)
                    throw new FormatException("Distortion coefficient " + key + "[" + i + "] is out of range.");
                into[i] = v;
            }
        }

        private static object Get(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) ? v : null;
        }

        private static string GetString(Dictionary<string, object> o, string key, string fallback)
        {
            return Get(o, key) as string ?? fallback;
        }

        private static double GetNumber(Dictionary<string, object> o, string key, double fallback)
        {
            var v = Get(o, key);
            return v is double ? (double)v : fallback;
        }

        /// <summary>Minimal JSON reader: objects become dictionaries, arrays lists, numbers doubles.</summary>
        private sealed class Reader
        {
            private readonly string _s;
            private int _i;
            private int _depth;

            public Reader(string s)
            {
                _s = s;
            }

            public bool End => _i >= _s.Length;

            public void SkipSpace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private FormatException Error(string what)
            {
                int line = 1;
                for (int k = 0; k < Math.Min(_i, _s.Length); k++) if (_s[k] == '\n') line++;
                return new FormatException(what + " on line " + line);
            }

            public object ReadValue()
            {
                SkipSpace();
                if (End) throw Error("unexpected end");
                char c = _s[_i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't' && Match("true")) return true;
                if (c == 'f' && Match("false")) return false;
                if (c == 'n' && Match("null")) return null;
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw Error("unexpected '" + c + "'");
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
                _i += word.Length;
                return true;
            }

            private Dictionary<string, object> ReadObject()
            {
                if (++_depth > 64) throw Error("nested too deeply");
                var o = new Dictionary<string, object>();
                _i++; // {
                SkipSpace();
                if (!End && _s[_i] == '}')
                {
                    _i++;
                    _depth--;
                    return o;
                }
                while (true)
                {
                    SkipSpace();
                    if (End || _s[_i] != '"') throw Error("expected a property name");
                    string key = ReadString();
                    SkipSpace();
                    if (End || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    o[key] = ReadValue();
                    SkipSpace();
                    if (End) throw Error("unexpected end");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; break; }
                    throw Error("expected ',' or '}'");
                }
                _depth--;
                return o;
            }

            private List<object> ReadArray()
            {
                if (++_depth > 64) throw Error("nested too deeply");
                var a = new List<object>();
                _i++; // [
                SkipSpace();
                if (!End && _s[_i] == ']')
                {
                    _i++;
                    _depth--;
                    return a;
                }
                while (true)
                {
                    a.Add(ReadValue());
                    SkipSpace();
                    if (End) throw Error("unexpected end");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; break; }
                    throw Error("expected ',' or ']'");
                }
                _depth--;
                return a;
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                _i++; // "
                while (true)
                {
                    if (End) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (End) throw Error("unterminated string");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                            int code;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw Error("bad \\u escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
                return sb.ToString();
            }

            private double ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                double v;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw Error("bad number");
                return v;
            }
        }
    }
}
