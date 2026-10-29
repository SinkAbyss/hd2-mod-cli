using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The problem report (hd2qd report, Settings in the window).</summary>
    public class Reports : Fixture
    {
        static Dictionary<string, string> Entries(string zip)
        {
            using (var z = ZipFile.OpenRead(zip))
                return z.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
        }

        [Fact]
        public void TheReportHoldsTheStateAndTheLogsButNeverTheSignIn()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(Path.Combine(logs, "dumps"));
            File.WriteAllText(Path.Combine(logs, "boot_mod_loader.log"), "12:00:00 == boot-mod-loader-0.13.0 2026-10-29 12:00:00\n");
            File.WriteAllBytes(Path.Combine(logs, "dumps", "dump-1.dmp"), new byte[] { 1, 2, 3 });
            LuaMods.GameFolderOverride = logs;
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
                Store.Save("deploy_history.json", new Dictionary<string, object> { ["deploys"] = new List<object>() });
                Store.Save("nexus.json", new Dictionary<string, object>
                {
                    ["oauth"] = "SECRET-SIGN-IN", ["key"] = "SECRET-KEY",
                    ["history"] = new List<object> { new Dictionary<string, object> { ["name"] = "Cape", ["result"] = "added" } },
                });
                var outDir = Path.Combine(Root, "out");
                Directory.CreateDirectory(outDir);
                var said = Cli("report", "--out", outDir);
                var zip = Directory.GetFiles(outDir, "hd2qd-report-*.zip").Single();
                Assert.Contains("report written: " + zip, said);
                Assert.Contains("mods: 1 (1 on)", said);
                Assert.Empty(Directory.GetFiles(outDir, "*.part"));

                var e = Entries(zip);
                Assert.Contains("info.txt", e.Keys);
                Assert.Contains("Cape", e["state.json"]);
                Assert.Contains("deploy_history.json", e.Keys);
                Assert.Contains("config.json", e.Keys);
                Assert.Contains("boot-mod-loader-0.13.0", e["game/boot_mod_loader.log"]);
                Assert.Contains("dump-1.dmp", e["game/dumps.txt"]);
                Assert.DoesNotContain(e.Keys, k => k.EndsWith(".dmp"));
                Assert.DoesNotContain(e.Keys, k => k == "nexus.json");
                Assert.All(e.Values, text => Assert.DoesNotContain("SECRET", text));
#if HAS_NEXUS
                Assert.Contains("\"added\"", e["nexus_history.json"]);
#else
                Assert.DoesNotContain("nexus_history.json", e.Keys);
#endif
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        /// <summary>The Windows user's folder, however it is written, and nothing that only
        /// starts the same.</summary>
        [Fact]
        public void TheUserFolderIsWrittenAsUserProfile()
        {
            const string profile = @"C:\Users\Bob";
            Assert.Equal(@"%USERPROFILE%\mods", Report.Redact(@"C:\Users\Bob\mods", profile));
            Assert.Equal(@"""%USERPROFILE%\\mods""", Report.Redact(@"""C:\\Users\\Bob\\mods""", profile));
            Assert.Equal("%USERPROFILE%/mods", Report.Redact("c:/users/bob/mods", profile));
            Assert.Equal(@"""home"": ""%USERPROFILE%""", Report.Redact(@"""home"": ""C:\\Users\\Bob""", profile));
            Assert.Equal(@"C:\Users\Bobby\mods", Report.Redact(@"C:\Users\Bobby\mods", profile));
        }

        [Fact]
        public void ALongLogKeepsItsEnd()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(logs);
            var line = new string('x', 99) + "\n";
            File.WriteAllText(Path.Combine(logs, "boot_mod_loader.log"),
                              string.Concat(Enumerable.Repeat(line, Report.LogTail / line.Length + 100)) + "the last session\n");
            LuaMods.GameFolderOverride = logs;
            try
            {
                var (zip, _) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                var log = Entries(zip)["game/boot_mod_loader.log"];
                Assert.StartsWith("[the first ", log);
                Assert.EndsWith("the last session\n", log);
                Assert.True(log.Length <= Report.LogTail + 100);
            }
            finally { LuaMods.GameFolderOverride = null; }
        }
    }
}
