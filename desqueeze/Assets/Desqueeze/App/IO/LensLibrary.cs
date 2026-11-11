using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Desqueeze.Core.Lens;
using UnityEngine;

namespace Desqueeze.App.IO
{
    /// <summary>
    /// The lens profile library: one JSON file per lens (see <see cref="LensProfileJson"/>) in a folder the user can open,
    /// back up and share. Files can also be imported from and exported to anywhere.
    /// </summary>
    internal sealed class LensLibrary
    {
        internal sealed class Entry
        {
            public string Path;
            public LensProfile Profile;
            public string Name => Profile.name;
        }

        public string Folder { get; }
        public List<Entry> Entries { get; } = new List<Entry>();
        /// <summary>Files in the folder that could not be read at the last <see cref="Refresh"/>.</summary>
        public List<string> Unreadable { get; } = new List<string>();

        public LensLibrary(string configuredFolder)
        {
            Folder = string.IsNullOrEmpty(configuredFolder) ? DefaultFolder() : ExpandHome(configuredFolder.Trim());
        }

        /// <summary>"Desqueeze Lens Profiles" in the user's Documents folder (application data if there is none).</summary>
        public static string DefaultFolder()
        {
            string docs = null;
            try
            {
                string home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
                string special = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                // Mono returns the home folder for MyDocuments on macOS and Linux; prefer a real Documents folder.
                if (!string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, "Documents"))) docs = Path.Combine(home, "Documents");
                else if (!string.IsNullOrEmpty(special) && Directory.Exists(special)) docs = special;
            }
            catch (Exception)
            {
                docs = null;
            }
            if (string.IsNullOrEmpty(docs)) docs = Application.persistentDataPath;
            return Path.Combine(docs, "Desqueeze Lens Profiles");
        }

        private static string ExpandHome(string p)
        {
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal))
            {
                string home = Environment.GetEnvironmentVariable("HOME") ?? "";
                return Path.Combine(home, p.Length > 2 ? p.Substring(2) : "");
            }
            return p;
        }

        /// <summary>Re-reads the folder. Profiles are listed by name.</summary>
        public void Refresh()
        {
            Entries.Clear();
            Unreadable.Clear();
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var f in Directory.GetFiles(Folder, "*" + LensProfileJson.FileExtension))
                {
                    try
                    {
                        Entries.Add(new Entry { Path = f, Profile = Load(f) });
                    }
                    catch (Exception)
                    {
                        Unreadable.Add(System.IO.Path.GetFileName(f));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Desqueeze] Could not read the lens profile folder: " + e.Message);
            }
            Entries.Sort((a, b) =>
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });
        }

        public Entry Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var e in Entries)
                if (SamePath(e.Path, path)) return e;
            return null;
        }

        public static LensProfile Load(string path)
        {
            return LensProfileJson.Read(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>Writes a profile anywhere (export).</summary>
        public static void Write(LensProfile p, string path)
        {
            FileUtil.WriteAllBytesSafely(path, new UTF8Encoding(false).GetBytes(LensProfileJson.Write(p)));
        }

        /// <summary>
        /// Saves a profile into the library: over <paramref name="existingPath"/> when given (renaming the file to follow
        /// the profile's name when that name is free), otherwise as a new file named after the profile. Returns the path.
        /// </summary>
        public string Save(LensProfile p, string existingPath)
        {
            Directory.CreateDirectory(Folder);
            string path = existingPath;
            if (string.IsNullOrEmpty(path) || !IsInFolder(path))
            {
                path = UniquePath(p.name, null);
            }
            else if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), Slug(p.name), StringComparison.OrdinalIgnoreCase))
            {
                string renamed = UniquePath(p.name, path);
                Write(p, renamed);
                if (!SamePath(renamed, path) && File.Exists(path)) File.Delete(path);
                Refresh();
                return renamed;
            }
            Write(p, path);
            Refresh();
            return path;
        }

        public void Delete(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            Refresh();
        }

        /// <summary>True if a library profile other than <paramref name="except"/> already has this name.</summary>
        public bool NameTaken(string name, string except)
        {
            foreach (var e in Entries)
                if (string.Equals(e.Name.Trim(), (name ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase) && !SamePath(e.Path, except))
                    return true;
            return false;
        }

        /// <summary>A name not used by any profile in the library, based on <paramref name="name"/>.</summary>
        public string UniqueName(string name)
        {
            name = string.IsNullOrEmpty(name) ? "Lens" : name.Trim();
            if (!NameTaken(name, null)) return name;
            for (int i = 2; ; i++)
            {
                string n = name + " " + i;
                if (!NameTaken(n, null)) return n;
            }
        }

        private string UniquePath(string name, string except)
        {
            string slug = Slug(name);
            string p = System.IO.Path.Combine(Folder, slug + LensProfileJson.FileExtension);
            for (int i = 2; File.Exists(p) && !SamePath(p, except); i++)
                p = System.IO.Path.Combine(Folder, slug + " " + i + LensProfileJson.FileExtension);
            return p;
        }

        /// <summary>A file name for a profile name: characters that are not allowed in file names become '-'.</summary>
        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            var bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in (name ?? "").Trim())
                sb.Append(Array.IndexOf(bad, c) >= 0 || c == '/' || c == '\\' || c == ':' || c < 32 ? '-' : c);
            string s = sb.ToString().Trim().Trim('.');
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s.Length == 0 ? "Lens" : s;
        }

        public bool IsInFolder(string path)
        {
            try
            {
                return SamePath(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), System.IO.Path.GetFullPath(Folder));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
