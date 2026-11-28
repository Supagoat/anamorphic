using System;
using System.IO;
using Desqueeze.Core.Testing;
using NUnit.Framework;
using UnityEngine;

namespace Desqueeze.Tests
{
    /// <summary>
    /// TIFF reading and writing, held to Tools/TestData/tiff (see TiffConformance). The full report goes to
    /// Tools/TestData/tiff/tiff-report.txt, and the TIFFs written by the check to Tools/TestData/tiff/written, so other
    /// programs can be asked to read them.
    /// </summary>
    public class TiffTests
    {
        [Test]
        public void Tiff_ReadsAndWritesLikeTheReferences()
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "TestData", "tiff"));
            if (!File.Exists(Path.Combine(folder, "manifest.txt"))) Assert.Ignore("The TIFF test files aren't in " + folder);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var report = TiffConformance.Run(folder, Path.Combine(folder, "written"));
            string text = "TIFF check, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", Unity " + Application.unityVersion + ", " +
                          SystemInfo.operatingSystem + "\n" + report.Passed + " passed, " + report.Failed + " failed, " +
                          watch.ElapsedMilliseconds + " ms\n\n" + report.Text;
            File.WriteAllText(Path.Combine(folder, "tiff-report.txt"), text);
            Debug.Log(text);
            Assert.AreEqual(0, report.Failed, text);
        }
    }
}
