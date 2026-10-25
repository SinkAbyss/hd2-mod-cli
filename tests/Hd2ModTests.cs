using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Hd2Mod;
using Xunit;

// the settings folder and the hooks are static: one test at a time
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hd2Mod.Tests
{
    /// <summary>hd2qd against a fake game folder and fake mods.</summary>
    public abstract class Fixture : IDisposable
    {
        protected const string A = "9ba626afa44a3aa3", B = "20bdec2a51065e1f";
        protected readonly string Root, Game, Data;

        protected Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "hd2mod_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Store.HomeOverride = Path.Combine(Root, "home");
            Game = Path.Combine(Root, "game");
            Data = Path.Combine(Game, "data");
            Directory.CreateDirectory(Data);
            // a mod folder of the test's own: the default would be the player's HD2 Arsenal folder
            Store.Save("config.json", new Dictionary<string, object> { ["game_dir"] = Game, ["mods_dir"] = Path.Combine(Root, "library") });
            Deploy.GameRunning = () => false;
            Store.Redirected = () => null;             // the test's home is not under AppData anyway
            Deploy.SameDrive = Native.SameDrive;
        }

        public virtual void Dispose()
        {
            Store.HomeOverride = null;
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        protected static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        /// <summary>A patch archive holding resources (type, id): header, one type row, file rows.</summary>
        protected static byte[] Archive(params (ulong Type, ulong Id)[] resources) => Archive(null, resources);

        /// <summary>The same, with bytes for every resource: each one gets its own copy of them,
        /// at its own place in the archive (that is what tells two mods' resources apart).</summary>
        protected static byte[] Archive(byte[] body, params (ulong Type, ulong Id)[] resources)
        {
            var b = new List<byte>();
            var head = new byte[0x48];
            BitConverter.GetBytes(0xF0000011u).CopyTo(head, 0);
            BitConverter.GetBytes(1u).CopyTo(head, 4);
            BitConverter.GetBytes((uint)resources.Length).CopyTo(head, 8);
            b.AddRange(head);
            b.AddRange(new byte[32]);
            int size = body?.Length ?? 0;
            ulong at = (ulong)(0x48 + 32 + 80 * resources.Length);
            foreach (var (t, id) in resources)
            {
                var row = new byte[80];
                BitConverter.GetBytes(id).CopyTo(row, 0);
                BitConverter.GetBytes(t).CopyTo(row, 8);
                BitConverter.GetBytes(at).CopyTo(row, 0x10);
                BitConverter.GetBytes((uint)size).CopyTo(row, 0x38);
                b.AddRange(row);
                at += (ulong)size;
            }
            if (body != null) foreach (var _ in resources) b.AddRange(body);
            return b.ToArray();
        }

        /// <summary>A mod folder: files {relative path: bytes}, plus empty companions for each main file.</summary>
        protected string Mod(string name, Dictionary<string, byte[]> files, string manifest = null)
        {
            var folder = Path.Combine(Root, "mods", name);
            foreach (var kv in files)
            {
                var p = Path.Combine(folder, kv.Key.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, kv.Value);
                if (Deploy.Patch.IsMatch(Path.GetFileName(p)))
                    foreach (var ext in Deploy.Companions) File.WriteAllBytes(p + ext, new byte[0]);
            }
            if (manifest != null)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest, new UTF8Encoding(false));
            }
            return folder;
        }

        protected static Dictionary<string, byte[]> Files(params (string Rel, string Body)[] xs) =>
            xs.ToDictionary(x => x.Rel, x => Bytes(x.Body));

        /// <summary>A command's output; a refusal fails the test unless code says otherwise.</summary>
        protected string Cli(params string[] args) => CliCode(0, args);

        protected string CliCode(int code, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int r = Commands.Run(args, o, e);
            Assert.True(r == code, "hd2qd " + string.Join(" ", args) + " returned " + r + ": " + e);
            return o.ToString();
        }

        protected List<string> Deployed()
        {
            var l = Directory.EnumerateFiles(Data).Select(Path.GetFileName).Where(f => Deploy.Managed.IsMatch(f)).ToList();
            l.Sort(string.CompareOrdinal);
            return l;
        }

        protected string Body(string name) => File.ReadAllText(Path.Combine(Data, name));

        protected static Dictionary<string, string> PlanBodies() =>
            Deploy.Plan(Library.Load()).Where(w => Deploy.Patch.IsMatch(w.Name)).ToDictionary(w => w.Name, w => File.ReadAllText(w.Src));

        protected static List<string> PlanList() =>
            Deploy.Plan(Library.Load()).Where(w => Deploy.Patch.IsMatch(w.Name)).Select(w => File.ReadAllText(w.Src)).ToList();

        protected static bool Same(string a, string b) => Native.SameFile(a, b) == true;

        protected static string J(object x) => Json.Write(x);
    }

    public class Numbering : Fixture
    {
        [Fact]
        public void OrderOptionsAndArchives()
        {
            var one = Mod("One", Files((A + ".patch_0", "one-a"), (B + ".patch_0", "one-b")));
            var two = Mod("Two", Files(("opt/" + A + ".patch_0", "two-opt"), ("sub1/" + A + ".patch_0", "two-sub1"),
                                       ("sub2/" + A + ".patch_0", "two-sub2"), ("sub2/" + A + ".patch_1", "two-sub2b")),
                          J(new Dictionary<string, object>
                          {
                              ["Version"] = 1L, ["Name"] = "Two", ["Options"] = new List<object> { new Dictionary<string, object>
                              {
                                  ["Name"] = "Base", ["Include"] = new List<object> { "opt" },
                                  ["SubOptions"] = new List<object>
                                  {
                                      new Dictionary<string, object> { ["Name"] = "S1", ["Include"] = new List<object> { "sub1" } },
                                      new Dictionary<string, object> { ["Name"] = "S2", ["Include"] = new List<object> { "sub2" } },
                                  },
                              } },
                          }));
            Cli("add", one);
            Cli("add", two);
            // mods top to bottom; Two's option folder, then its first sub-option (the default)
            Assert.Equal(new Dictionary<string, string>
            {
                [A + ".patch_0"] = "one-a", [B + ".patch_0"] = "one-b", [A + ".patch_1"] = "two-opt", [A + ".patch_2"] = "two-sub1",
            }, PlanBodies());
            Cli("set", "Two", "Base/S2", "on", "--only");
            var plan = PlanBodies();
            Assert.Equal("two-sub2", plan[A + ".patch_2"]);
            Assert.Equal("two-sub2b", plan[A + ".patch_3"]);
            Cli("set", "Two", "Base", "off");
            Assert.DoesNotContain(A + ".patch_2", Deploy.Plan(Library.Load()).Select(w => w.Name));
        }

        [Fact]
        public void LenientManifestAndNoManifest()
        {
            var m = Mod("Loose", Files(("x/" + A + ".patch_0", "x")),
                        "{\n \"name\": \"Loose\",\n \"options\": [\n  {\"name\": \"X\", \"include\": [\"x\"],},\n ],\n}\n");
            var bare = Mod("Bare", Files(("deep/" + A + ".patch_0", "deep")));
            Cli("add", m);
            Cli("add", bare);
            Assert.Equal(new[] { "x", "deep" }, PlanList());
        }
    }

    public class Deploying : Fixture
    {
        readonly string one, two, three;

        public Deploying()
        {
            one = Mod("One", Files((A + ".patch_0", "one")));
            two = Mod("Two", Files((A + ".patch_0", "two"), (B + ".patch_0", "two-b")));
            three = Mod("Three", Files((A + ".patch_0", "three")));
            foreach (var m in new[] { one, two, three }) Cli("add", m);
        }

        [Fact]
        public void DeployThenNothingToDo()
        {
            Assert.Contains("wrote 12", Cli("deploy"));            // 4 patch files, each with 2 companions
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.Equal("three", Body(A + ".patch_2"));
            Assert.True(Same(Path.Combine(Data, A + ".patch_1"), Path.Combine(two, A + ".patch_0")), "a hard link to the library, not a copy");
            Assert.Contains("kept 12, moved 0, wrote 0", Cli("deploy"));
        }

        [Fact]
        public void ReorderOnlyRenames()
        {
            Cli("deploy");
            Cli("move", "Three", "--top");
            var out_ = Cli("deploy");
            Assert.Contains("moved", out_);
            Assert.Contains("wrote 0", out_);
            Assert.Equal(new[] { "three", "one", "two" }, Enumerable.Range(0, 3).Select(i => Body(A + ".patch_" + i)));
        }

        [Fact]
        public void DisableClosesTheGap()
        {
            Cli("deploy");
            Cli("disable", "One");
            Cli("deploy");
            var want = new List<string>
            {
                A + ".patch_0", A + ".patch_0.gpu_resources", A + ".patch_0.stream",
                A + ".patch_1", A + ".patch_1.gpu_resources", A + ".patch_1.stream",
                B + ".patch_0", B + ".patch_0.gpu_resources", B + ".patch_0.stream",
            };
            want.Sort(string.CompareOrdinal);
            Assert.Equal(want, Deployed());
            Assert.Equal(new[] { "two", "three" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1") });
        }

        [Fact]
        public void ReplaceTouchesOnlyThatMod()
        {
            Cli("deploy");
            var v2 = Mod("Two v2", Files((A + ".patch_0", "two v2"), (B + ".patch_0", "two-b v2")));
            Cli("replace", "Two", v2);
            Assert.Contains("kept 10, moved 0, wrote 2", Cli("deploy"));   // only Two's two patch files (its empty companions match)
            Assert.Equal("two v2", Body(A + ".patch_1"));
            Assert.Equal(v2, Library.Load().Mods[1].Path);
        }

        [Fact]
        public void AdoptsIdenticalFilesFromAnotherManager()
        {
            foreach (var (src, name) in new[] { (one, A + ".patch_0"), (two, A + ".patch_1"), (three, A + ".patch_2") })
                File.Copy(Path.Combine(src, A + ".patch_0"), Path.Combine(Data, name));
            File.Copy(Path.Combine(two, B + ".patch_0"), Path.Combine(Data, B + ".patch_0"));
            File.WriteAllText(Path.Combine(Data, A + ".patch_9"), "left over");
            var out_ = Cli("deploy");
            Assert.Contains("kept 4", out_);            // the four copies are taken over, not rewritten
            Assert.Contains("deleted 1", out_);
            Assert.DoesNotContain(A + ".patch_9", Deployed());
        }

        [Fact]
        public void RelinkTurnsCopiesIntoLinks()
        {
            Cli("deploy", "--copy");
            var a0 = Path.Combine(Data, A + ".patch_0");
            Assert.False(Same(a0, Path.Combine(one, A + ".patch_0")));
            Assert.Contains("would link 12 copies", Cli("relink", "--dry-run"));
            Assert.Contains("linked 12 copies", Cli("relink"));
            Assert.True(Same(a0, Path.Combine(one, A + ".patch_0")));
            Assert.Equal("three", Body(A + ".patch_2"));
            Assert.Empty(Directory.EnumerateFiles(Data, "*.hd2mod-link"));
            Assert.Contains("kept 12, moved 0, wrote 0", Cli("deploy"));
            Assert.Contains("linked 0 copies", Cli("relink"));
        }

        [Fact]
        public void RelinkLeavesOtherDrivesAndWrongFiles()
        {
            Cli("deploy", "--copy");
            File.WriteAllText(Path.Combine(Data, A + ".patch_1"), "bad");   // same size as "two", wrong bytes: a deploy rewrites it, relink does not
            Deploy.SameDrive = (a, b) => !a.StartsWith(three);
            Assert.Contains("linked 8 copies", Cli("relink"));    // 12, less Three's 3 (another drive) and the wrong file
            Assert.Equal("bad", Body(A + ".patch_1"));
            Assert.False(Same(Path.Combine(Data, A + ".patch_2"), Path.Combine(three, A + ".patch_0")));
        }

        [Fact]
        public void AChangedFileIsRewritten()
        {
            Cli("deploy", "--copy");
            File.WriteAllText(Path.Combine(Data, A + ".patch_0"), "oops");
            Assert.Contains("wrote 1", Cli("deploy", "--copy"));
            Assert.Equal("one", Body(A + ".patch_0"));
        }

        [Fact]
        public void StatusChangesNothingAndPurgeEmpties()
        {
            var before = Deployed();
            Assert.Contains("would keep 0", Cli("status"));
            Assert.Equal(before, Deployed());
            Cli("deploy");
            CliCode(1, "purge");
            Cli("purge", "--yes");
            Assert.Empty(Deployed());
            Assert.True(File.Exists(Path.Combine(one, A + ".patch_0")), "the library keeps its files");
        }

        static Dictionary<string, object> ByName(Dictionary<string, object> st, string name) =>
            Json.List(st, "mods").Cast<Dictionary<string, object>>().First(m => (string)m["name"] == name);

        static List<string> Actions(Dictionary<string, object> mod) =>
            Json.List(mod, "files").Cast<Dictionary<string, object>>().Select(f => f["name"] + " " + f["action"]).ToList();

        Dictionary<string, object> State() => (Dictionary<string, object>)Json.Parse(Cli("state"));

        [Fact]
        public void StateSaysWhatADeployWouldDo()
        {
            Cli("deploy");
            Cli("move", "Three", "--top");
            Cli("disable", "Two");
            Cli("add", Mod("Four", Files((A + ".patch_0", "four!"))));
            var st = State();
            Assert.Equal(new[] { A + ".patch_0 move" }, Actions(ByName(st, "Three")));
            Assert.Equal(A + ".patch_2", Json.List(ByName(st, "Three"), "files").Cast<Dictionary<string, object>>().First()["from"]);
            Assert.Equal(new[] { A + ".patch_1 move" }, Actions(ByName(st, "One")));
            Assert.Equal(new[] { A + ".patch_2 write" }, Actions(ByName(st, "Four")));
            Assert.Empty(Json.List(ByName(st, "Two"), "files"));
            Assert.Contains(B + ".patch_0", Json.List(st, "deletes"));
            // Four's patch file; its two empty companions are Two's, renamed
            Assert.Equal(1L, Json.Obj(st, "summary")["written"]);
            Cli("deploy");
            st = State();
            Assert.All(Json.List(st, "mods").Cast<Dictionary<string, object>>().SelectMany(m => Json.List(m, "files").Cast<Dictionary<string, object>>()),
                       f => Assert.Equal("keep", f["action"]));
            Assert.Empty(Json.List(st, "deletes"));
        }

        [Fact]
        public void StateShowsABrokenModAndTheRest()
        {
            Directory.Delete(two, true);
            var st = State();
            Assert.Contains("folder is gone", (string)ByName(st, "Two")["error"]);
            Assert.False((bool)ByName(st, "Two")["exists"]);
            Assert.Equal(new[] { A + ".patch_1 write" }, Actions(ByName(st, "Three")));
        }

        [Fact]
        public void DeleteTakesAModOffTheDisk()
        {
            Cli("deploy");
            Assert.Contains("deleted Two", Cli("delete", "Two", "--yes"));
            Assert.DoesNotContain(Library.Load().Mods, m => m.Name == "Two");
            Assert.False(Directory.Exists(two), "its folder is gone");
            Assert.True(Directory.Exists(one) && Directory.Exists(three), "the others stay");
            var want = new List<string> { A + ".patch_0", A + ".patch_0.gpu_resources", A + ".patch_0.stream",
                                          A + ".patch_1", A + ".patch_1.gpu_resources", A + ".patch_1.stream" };
            Assert.Equal(want, Deployed());
            Assert.Equal(new[] { "one", "three" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1") });
        }

        [Fact]
        public void DeleteChangesNothingWithoutYesOrWhileTheGameRuns()
        {
            Cli("deploy");
            var before = Deployed();
            CliCode(1, "delete", "Two");
            Deploy.GameRunning = () => true;
            Assert.Equal(1, Commands.Run(new[] { "delete", "Two", "--yes" }, new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("game_running", refused.Kind);
            Assert.True(Directory.Exists(two));
            Assert.Contains(Library.Load().Mods, m => m.Name == "Two");
            Assert.Equal(before, Deployed());
        }

        [Fact]
        public void DeleteRefusesAFolderThatIsNotTheModsOwn()
        {
            Cli("add", Path.GetDirectoryName(one), "--name", "Everything");     // the folder that holds One, Two and Three
            Assert.Equal(1, Commands.Run(new[] { "delete", "Everything", "--yes" }, new StringWriter(), new StringWriter(), out var shared));
            Assert.Equal("shared", shared.Kind);
            Cli("add", Game, "--name", "The game");
            Assert.Equal(1, Commands.Run(new[] { "delete", "The game", "--yes" }, new StringWriter(), new StringWriter(), out var guarded));
            Assert.Equal("protected", guarded.Kind);
            Assert.True(Directory.Exists(one) && Directory.Exists(Data), "nothing deleted");
        }

        [Fact]
        public void RefusesWhileTheGameRuns()
        {
            Deploy.GameRunning = () => true;
            CliCode(1, "deploy");
            Assert.Empty(Deployed());
        }
    }

    /// <summary>Downloads and update checks against fakes of the sites on localhost.</summary>
    public class OnlineSources : Fixture
    {
        readonly System.Net.HttpListener server = new System.Net.HttpListener();
        readonly string baseUrl;
        readonly Dictionary<string, byte[]> zips = new Dictionary<string, byte[]>();
        public readonly List<string> Seen = new List<string>();

        public OnlineSources()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            baseUrl = "http://localhost:" + port + "/";
            server.Prefixes.Add(baseUrl);
            server.Start();
            Updates.GitHubApi = baseUrl + "gh";
            System.Threading.Tasks.Task.Run(Serve);
            for (int f = 1; f <= 2; f++) zips["f" + f] = Zip(("Cape/manifest.json", "{\"Name\": \"Cape Colours\"}"), ("Cape/" + A + ".patch_0", "cape v" + f));
        }

        static byte[] Zip(params (string Name, string Body)[] entries)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, body) in entries)
                    using (var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(body);
            return ms.ToArray();
        }

        void Serve()
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = server.GetContext(); }
                catch (Exception) { return; }
                var path = c.Request.Url.AbsolutePath;
                int status = 200;
                byte[] body;
                if (path.StartsWith("/cdn/")) body = zips[path.Substring(5).Replace(".zip", "")];
                // GitHub: a repository whose latest release is v2.0 with a zip
                else if (path == "/gh/repos/someone/cape/releases/latest")
                    body = Bytes("{\"tag_name\": \"v2.0\", \"html_url\": \"https://github.com/someone/cape/releases/tag/v2.0\", " +
                                 "\"assets\": [{\"browser_download_url\": \"" + baseUrl + "cdn/f2.zip\"}]}");
                else if (path.StartsWith("/gh/")) { status = 404; body = Bytes("{}"); }
                else { status = 404; body = Bytes("{}"); }
                c.Response.StatusCode = status;
                c.Response.OutputStream.Write(body, 0, body.Length);
                c.Response.Close();
            }
        }

        public override void Dispose()
        {
            server.Stop();
            base.Dispose();
        }









        /// <summary>A mod from GitHub: checked against the repository's latest release, and
        /// updated in place with one command.</summary>
        [Fact]
        public void GitHubUpdatesAreCheckedAndApplied()
        {
            var gh = Path.Combine(Root, "gh");
            File.WriteAllBytes(gh + ".zip", zips["f1"]);
            Cli("add", gh + ".zip", "--name", "From GitHub");
            Cli("source", "From GitHub", "https://github.com/someone/cape/releases/tag/v1.0");
            Cli("add", Mod("Nowhere", Files((B + ".patch_0", "x"))));
            var r = Updates.Check(Library.Load()).ToDictionary(x => x.Name);
            Assert.Equal("update", r["From GitHub"].Status);
            Assert.Equal("v1.0", r["From GitHub"].Current);
            Assert.Equal("v2.0", r["From GitHub"].Latest);
            Assert.Equal("no_source", r["Nowhere"].Status);
            Assert.Contains("updated: Cape Colours v2.0", Cli("update", "From GitHub"));
            var updated = Library.Load().Mods.First(m => m.GitHub != null);
            Assert.Equal("v2.0", updated.GitHub.Tag);
            Assert.Equal("cape v2", File.ReadAllText(Path.Combine(updated.Path, A + ".patch_0")));
            Assert.Equal("current", Updates.Check(Library.Load()).First(x => x.ModId == updated.Id).Status);
        }

        [Fact]
        public void AModsVersionIsTheRecordedOneElseOneInItsNames()
        {
            (string, string) Of(string name, string path, NexusRef nx = null, GitHubRef gh = null) =>
                Origins.VersionOf(new Mod { Name = name, Path = path, Nexus = nx, GitHub = gh });
            Assert.Equal(("4", "nexus"), Of("ESC Squad Ping v0.3.6", @"X:\m\a", new NexusRef { Version = "4", File = 9 }));  // what was downloaded
            // a Nexus version with no file and no time was the page's newest, written in by an
            // older match: not the installed version
            Assert.Equal(("0.3.6", "name"), Of("ESC Squad Ping v0.3.6", @"X:\m\a", new NexusRef { Version = "4" }));
            Assert.Equal(("v2.0", "github"), Of("Cape", @"X:\m\a", gh: new GitHubRef { Tag = "v2.0" }));
            Assert.Equal(("11", "file"), Of("HD2 SmoothBoot", @"X:\m\HD2 SmoothBoot 3.0.50 16727 11 2026-10-11T07-02Z abc"));
            Assert.Equal(("1.136.1", "name"), Of("Super Holo HUD v1.136.1", @"X:\m\Super Holo HUD v1.136.1_cdae72a9f188"));
            Assert.Equal(("4.9.28", "folder"), Of("战备冷却", @"X:\m\StratagemCooldown-4.9.28_AR129985"));
            Assert.Equal(("0.6.1", "folder"), Of("Native CarpetBomb", @"X:\m\NativeCarpetBomb_v0.6.1_AR176602"));
            Assert.Equal(((string)null, (string)null), Of("[Violet] Ines-24", @"X:\m\[Violet] Ines-24 (ver.28-4)_AR825256"));   // not a version
            Assert.Equal(((string)null, (string)null), Of("Fog 2026-07-27", @"X:\m\Leveller 3D Reticle 1_AR871371"));
            // the files replaced since the download: the folder's version, not the record's
            Assert.Equal(("0.9.0", "folder"), Of("BML", @"X:\m\Boot Mod Loader v0.9.0_4c5d85be41af", new NexusRef { Version = "0.7.0", File = 9 }));
            // the same version written two ways is no replacement
            Assert.Equal(("v2.0", "github"), Of("Cape", @"X:\m\Cape 2.0_0123456789ab", gh: new GitHubRef { Tag = "v2.0" }));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>A file's Zone.Identifier, as a browser writes it for a download.</summary>
        static void Zone(string file, string host, string referrer)
        {
            using (var h = CreateFile(file + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
            using (var fs = new FileStream(h, FileAccess.Write))
            using (var w = new StreamWriter(fs))
                w.Write("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + referrer + "\r\nHostUrl=" + host + "\r\n");
        }

        [Fact]
        public void ADownloadSaysWhereItCameFrom()
        {
            var a = Path.Combine(Root, "Helmet Headlamp 16320 1.6.0 2026-10-23T09-47Z 5V4g.zip");
            File.WriteAllBytes(a, zips["f1"]);
            Zone(a, "https://cf-files.nexusmods.com/cdn/b6/48/ec/x?md5=1", "https://www.nexusmods.com/");
            var g = Path.Combine(Root, "release.zip");
            File.WriteAllBytes(g, zips["f2"]);
            Zone(g, "https://objects.githubusercontent.com/x", "https://github.com/someone/cape/releases/download/v1.5/release.zip");
            var w = Path.Combine(Root, "other.zip");
            File.WriteAllBytes(w, zips["f2"]);
            Zone(w, "https://ayakamods.com/mods/x.1/download", "https://ayakamods.com/mods/x.1/");
            var m1 = new Mod(); Origins.FromFile(m1, a);
            Assert.Equal(16320, m1.Nexus.Mod);
            Assert.Equal("1.6.0", m1.Nexus.Version);
            Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 47, 0, TimeSpan.Zero).ToUnixTimeSeconds(), m1.Nexus.Time);
            var m2 = new Mod(); Origins.FromFile(m2, g);
            Assert.Equal("someone/cape@v1.5", m2.GitHub.Owner + "/" + m2.GitHub.Repo + "@" + m2.GitHub.Tag);
            var m3 = new Mod(); Origins.FromFile(m3, w);
            Assert.Null(m3.Nexus);
            Assert.Null(m3.GitHub);
            Assert.Equal("https://ayakamods.com/mods/x.1/", m3.Page);
            var m4 = new Mod(); Origins.FromFile(m4, Path.Combine(Root, "no-such.zip"));
            Assert.Null(m4.Nexus ?? (object)m4.GitHub ?? m4.Page);
        }

        [Fact]
        public void SevenZipIsUnpackedByWindowsTar()
        {
            var src = Path.Combine(Root, "seven");
            Directory.CreateDirectory(Path.Combine(src, "Thing"));
            File.WriteAllText(Path.Combine(src, "Thing", A + ".patch_0"), "seven");
            var archive = Path.Combine(Root, "Thing.7z");
            var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tar, "--format 7zip -cf \"" + archive + "\" -C \"" + src + "\" Thing")
                   { UseShellExecute = false, CreateNoWindow = true }))
                p.WaitForExit();
            Assert.True(File.Exists(archive));
            Cli("add", archive);
            Assert.Equal(new[] { "seven" }, PlanList());
        }
    }

    public class LibraryOps : Fixture
    {
        static void Zip(string path, params (string Name, string Body)[] entries)
        {
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (name, body) in entries)
                    using (var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)))
                        w.Write(body);
        }

        static string CamManifest(params string[] subs) =>
            "{\"Name\": \"Cam\", \"Options\": [{\"Name\": \"Look\", \"SubOptions\": [" +
            string.Join(", ", subs.Select(s => "{\"Name\": \"" + s + "\", \"Include\": [\"" + s.ToLowerInvariant() + "\"]}")) + "]}]}";

        [Fact]
        public void ZipWithATopFolderAndReplaceKeepsOptions()
        {
            var z = Path.Combine(Root, "Cam.zip");
            Zip(z, ("Cam/manifest.json", CamManifest("Near", "Far")), ("Cam/near/" + A + ".patch_0", "near"), ("Cam/far/" + A + ".patch_0", "far"));
            Cli("add", z);
            Cli("set", "Cam", "1.2", "on", "--only");
            var z2 = Path.Combine(Root, "Cam2.zip");
            Zip(z2, ("manifest.json", CamManifest("Near", "Far", "Mid")), ("near/" + A + ".patch_0", "near2"),
                ("far/" + A + ".patch_0", "far2"), ("mid/" + A + ".patch_0", "mid2"));
            Cli("replace", "Cam", z2);
            Assert.Equal(new[] { "far2" }, PlanList());   // the sub-option chosen before is kept by name
        }

        [Fact]
        public void ZipThatLeavesItsFolderIsRefused()
        {
            var z = Path.Combine(Root, "Evil.zip");
            Zip(z, ("../escaped.txt", "x"), (A + ".patch_0", "a"));
            CliCode(1, "add", z);
            Assert.False(File.Exists(Path.Combine(Store.ModsDir(), "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Store.ModsDir()), "escaped.txt")));
        }

        [Fact]
        public void ImportArsenal()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files(("a/" + A + ".patch_0", "two-a"), ("b/" + A + ".patch_0", "two-b")),
                          "{\"Name\": \"Two\", \"Options\": [{\"Name\": \"Pick\", \"SubOptions\": [" +
                          "{\"Name\": \"A\", \"Include\": [\"a\"]}, {\"Name\": \"B\", \"Include\": [\"b\"]}]}]}");
            var arsenal = Path.Combine(Root, "hd2a_data.json");
            var text = J(new Dictionary<string, object>
            {
                ["deployedProfile"] = "p", ["userGameDir"] = Game, ["modsList"] = new Dictionary<string, object>
                {
                    ["p"] = new Dictionary<string, object>
                    {
                        ["label"] = "Main", ["mods"] = new List<object>
                        {
                            new Dictionary<string, object> { ["uuid"] = "u1", ["label"] = "One", ["path"] = one, ["enabled"] = false, ["options"] = new List<object>() },
                            new Dictionary<string, object>
                            {
                                ["uuid"] = "u2", ["label"] = "Two", ["path"] = two, ["enabled"] = true, ["tags"] = new List<object> { "misc", "vfx", "vehicle sfx" },
                                ["nexusData"] = new Dictionary<string, object> { ["modId"] = "555", ["fileId"] = "9", ["version"] = "1.2", ["updateTimestamp"] = 1700000000L },
                                ["options"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["name"] = "Pick", ["enabled"] = true, ["include"] = new List<object>(), ["suboptions"] = new List<object>
                                        {
                                            new Dictionary<string, object> { ["name"] = "A", ["enabled"] = false, ["include"] = new List<object> { "a" } },
                                            new Dictionary<string, object> { ["name"] = "B", ["enabled"] = true, ["include"] = new List<object> { "b" } },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            });
            File.WriteAllText(arsenal, text, new UTF8Encoding(false));
            Cli("import-arsenal", "--file", arsenal);
            Assert.Equal(new[] { "two-b" }, PlanList());
            var nx = Library.Load().Mods.First(m => m.Name == "Two").Nexus;   // HD2 Arsenal's nexusData comes along
            Assert.Equal("555/9/1.2/1700000000", nx.Mod + "/" + nx.File + "/" + nx.Version + "/" + nx.Time);
            // its tags, as Arsenal shows them (the user, 2026-10-25)
            Assert.Equal(new[] { "Misc", "VFX", "Vehicle SFX" }, Library.Load().Mods.First(m => m.Name == "Two").Tags.ToArray());
            Assert.Empty(Library.Load().Mods.First(m => m.Name == "One").Tags);
            Assert.Equal(text, File.ReadAllText(arsenal));   // Arsenal's file untouched
            // with Arsenal's "first mod highest priority" on, its top mod wins: here that is the
            // last one, so the list comes in reversed (proven on Arsenal 0.36.2, 2026-10-25)
            Assert.Equal(new[] { "One", "Two" }, Ops.ImportArsenal(arsenal).Lib.Mods.Select(m => m.Name).ToArray());
            File.WriteAllText(arsenal, text.Replace("\"deployedProfile\"", "\"setTopPriority\": true, \"deployedProfile\""), new UTF8Encoding(false));
            Assert.Equal(new[] { "Two", "One" }, Ops.ImportArsenal(arsenal).Lib.Mods.Select(m => m.Name).ToArray());
            // and the import says so (the user, 2026-10-25), only when it is on
            Assert.Contains("the list came in the other way round", Cli("import-arsenal", "--file", arsenal, "--replace"));
            File.WriteAllText(arsenal, text, new UTF8Encoding(false));
            Assert.DoesNotContain("other way round", Cli("import-arsenal", "--file", arsenal, "--replace"));
        }

        /// <summary>Mods from another manager meet the ones already in the library for the first
        /// time: the import says how many pairs of them conflict, and only then (the user,
        /// 2026-10-25).</summary>
        [Fact]
        public void ImportSaysWhereManagersConflict()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            string Echelon(string name, params (ulong, ulong)[] resources)
            {
                var folder = Mod(name, new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes(name + "'s own"), resources) });
                var dir = Path.Combine(Root, "Echelon-" + name);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "library.json");
                File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [{\"id\": \"e1\", \"name\": " + Json.Write(name) +
                    ", \"folder\": " + Json.Write(folder) + ", \"enabled\": true, \"deployed\": true, \"sets\": [{\"hash\": \"" + A +
                    "\", \"files\": [\"" + A + ".patch_0\"], \"deployed_as\": null}], \"options\": []}]}", new UTF8Encoding(false));
                return file;
            }
            // one resource in common with One, different bytes
            Assert.Contains("1 conflict between mods from Echelon and mods already in the library", Cli("import-echelon", "--file", Echelon("Two", (1, 101), (2, 7))));
            // nothing in common: nothing said
            Assert.DoesNotContain("conflict", Cli("import-echelon", "--file", Echelon("Three", (3, 1))));
        }

        [Fact]
        public void ConflictsNameTheWinner()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("two's own"), (1, 101), (2, 7)) }));
            Assert.Contains("1 resource(s) in One / Two -- Two wins", Cli("conflicts"));
        }

        /// <summary>The same resource, byte for byte, in both mods (and at a different place in
        /// each archive): shared, not a conflict — whichever loads last, the game gets the same
        /// bytes (the user, 2026-10-24).</summary>
        [Fact]
        public void TheSameResourceInTwoModsIsNoConflict()
        {
            var shared = Bytes("the very same resource");
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(shared, (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(shared, (1, 101), (2, 7)) }));
            var said = Cli("conflicts");
            Assert.Contains("1 resource(s) in One / Two -- the very same bytes", said);
            Assert.DoesNotContain("wins", said);
        }

        /// <summary>A name of the player's own (the user, 2026-10-24): the mod answers to it, the
        /// name it came with is kept, and giving that name back drops it again.</summary>
        [Fact]
        public void RenameKeepsTheNameItCameWith()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("rename", "One", "My HUD");
            var mod = Library.Load().Mods[0];
            Assert.Equal("My HUD", mod.Name);
            Assert.Equal("One", mod.RealName);
            Cli("rename", "My HUD", "One");
            Assert.Null(Library.Load().Mods[0].RealName);
        }

        /// <summary>Labels of the player's own: put on, renamed everywhere, taken off, deleted
        /// (the user, 2026-10-24).</summary>
        [Fact]
        public void LabelsAreThePlayersOwn()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("tag", "One", "everyday");
            Cli("tag", "Two", "everyday", "to try");
            Assert.Contains("2  everyday", Cli("tags"));
            Assert.Contains("1  to try", Cli("tags"));
            Cli("tag-rename", "everyday", "daily");
            Assert.Equal(new[] { "daily" }, Library.Load().Mods[0].Tags.ToArray());
            Cli("untag", "Two", "to try");
            Assert.Equal(new[] { "daily" }, Library.Load().Mods[1].Tags.ToArray());
            Assert.Contains("0  to try", Cli("tags"));      // no mod carries it, the label stays
            Cli("tag-delete", "daily");
            Cli("tag-delete", "to try");
            Assert.Contains("no labels yet", Cli("tags"));
        }

        /// <summary>What the player chose cannot be downloaded again, so each save keeps the
        /// version before it beside the file as .bak (the user, 2026-10-24).</summary>
        [Fact]
        public void EverySaveKeepsTheVersionBeforeIt()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var file = Store.PathOf("library.json");
            var bak = file + ".bak";
            var first = File.ReadAllText(file);
            Assert.DoesNotContain("Two", first);
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Assert.Contains("Two", File.ReadAllText(file));
            Assert.True(File.Exists(bak), "the version before the last save is kept");
            Assert.Equal(first, File.ReadAllText(bak));
            // and the file itself is whole, never half written
            Assert.False(File.Exists(file + ".tmp"));
        }

        /// <summary>Another manager's ids are its own: a mod that comes in with an id the library
        /// already uses is given a new one, so nothing that goes by id sees two of them (the user,
        /// 2026-10-24: the window came down while drawing the storage page).</summary>
        [Fact]
        public void AnImportNeverBringsAnIdTwice()
        {
            Cli("add", Mod("Mine", Files((A + ".patch_0", "mine"))));
            var lib = Library.Load();
            var taken = lib.Mods[0].Id;
            var theirs = new Library { ImportedFrom = "Another" };
            theirs.Mods.Add(new Mod { Id = taken, Name = "Theirs", Path = Mod("Theirs", Files((B + ".patch_0", "theirs"))), Enabled = true });
            var (added, _) = Importers.Merge(lib, theirs);
            Assert.Single(added);
            Assert.NotEqual(taken, added[0].Id);
            Assert.Equal(2, lib.Mods.Select(m => m.Id).Distinct().Count());
        }

        /// <summary>Echelon's own library.json (format "echelon-library", version 1), which it
        /// writes for other tools. Echelon keeps its copy of a mod as numbered set folders with no
        /// manifest.json, so its options are the only thing that says which folder belongs to which
        /// option: they come in as the mod's layout, and the deploy takes the chosen folders alone
        /// (the user, 2026-10-24: a tester saw the sub-options come in unchosen).</summary>
        [Fact]
        public void EchelonsListIsImported()
        {
            var cape = Mod("Cape (ab12)", Files(
                ("opt/0/" + A + ".patch_0", "core"),
                ("opt/1/" + A + ".patch_0", "blue"),
                ("opt/2/" + A + ".patch_0", "red")));
            Assert.False(File.Exists(Path.Combine(cape, "manifest.json")));
            var file = Path.Combine(Root, "Echelon", "library.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string Set(int i) => "{\"hash\": \"" + A + "\", \"files\": [\"opt/" + i + "/" + A + ".patch_0\"], \"deployed_as\": null}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                "{\"id\": \"ab12\", \"name\": \"Cape Colours\", \"folder\": " + Json.Write(cape) + ", \"enabled\": true, \"deployed\": true," +
                " \"sets\": [" + Set(0) + ", " + Set(1) + ", " + Set(2) + "]," +
                " \"options\": [" +
                "{\"name\": \"Core\", \"enabled\": true, \"sets\": [0], \"choices\": [], \"selected\": null}," +
                "{\"name\": \"Colour\", \"enabled\": true, \"sets\": [], \"choices\": [{\"name\": \"Blue\", \"sets\": [1]}, {\"name\": \"Red\", \"sets\": [2]}], \"selected\": 0}]}]}",
                new UTF8Encoding(false));
            Assert.Contains("1 mods added from Echelon", Cli("import-echelon", "--file", file));
            var mod = Library.Load().Mods.Last();
            Assert.Equal("Cape Colours", mod.Name);
            Assert.Equal(cape, mod.Path);
            var chosen = Manifest.Merged(Manifest.Of(mod), mod.Options);
            Assert.Equal(new[] { "Core", "Colour" }, chosen.Select(x => x.Name).ToArray());
            Assert.True(chosen[0].Enabled);
            Assert.Equal(new[] { "Blue" }, chosen[1].Subs.Where(x => x.Enabled).Select(x => x.Name).ToArray());
            // the chosen folders only: opt/2 (Red) stays out
            Assert.Equal(new[] { "blue", "core" }, PlanList().OrderBy(x => x).ToArray());
            // its own file is left alone, and another format is refused
            Assert.Contains("echelon-library", File.ReadAllText(file));
            File.WriteAllText(file, "{\"format\": \"something-else\", \"version\": 1, \"mods\": []}", new UTF8Encoding(false));
            Assert.Throws<ModError>(() => Importers.ReadEchelon(file));
        }

        /// <summary>Echelon's library.json is in the order the mods were added; the load order is
        /// its active profile's, in its state.json (a tester, 2026-10-25: imported "乱序").</summary>
        [Fact]
        public void EchelonsLoadOrderIsItsProfiles()
        {
            var dir = Path.Combine(Root, "Echelon");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "library.json");
            string Entry(string id) => "{\"id\": \"" + id + "\", \"name\": \"Mod " + id + "\", \"folder\": " +
                                       Json.Write(Path.Combine(Root, id)) + ", \"enabled\": true, \"deployed\": true, \"sets\": [], \"options\": []}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                Entry("a") + ", " + Entry("b") + ", " + Entry("c") + "]}", new UTF8Encoding(false));
            string Names() => string.Join(" ", Importers.ReadEchelon(file).Mods.Select(m => m.Name));
            // no state.json: library.json's order
            Assert.Equal("Mod a Mod b Mod c", Names());
            void State(int version) => File.WriteAllText(Path.Combine(dir, "state.json"),
                "{\"version\": " + version + ", \"active_profile\": \"p2\", \"profiles\": [" +
                "{\"id\": \"p1\", \"name\": \"Other\", \"entries\": [{\"type\": \"mod\", \"id\": \"b\"}]}," +
                "{\"id\": \"p2\", \"name\": \"Default\", \"entries\": [{\"type\": \"mod\", \"id\": \"c\", \"enabled\": true}," +
                " {\"type\": \"sep\", \"id\": \"s1\", \"title\": \"Bingus\"}, {\"type\": \"mod\", \"id\": \"a\", \"enabled\": true}]}]}",
                new UTF8Encoding(false));
            // the active profile's order; b, not in it, after
            State(2);
            Assert.Equal("Mod c Mod a Mod b", Names());
            // a state.json of a version not known here is not guessed at
            State(3);
            Assert.Equal("Mod a Mod b Mod c", Names());
        }

        /// <summary>The pictures and the words Echelon keeps beside its library, not in the mod's
        /// folder, come in too, copied (a tester, 2026-10-25: no cover, no option pictures).</summary>
        [Fact]
        public void EchelonsPicturesAndWordsComeIn()
        {
            var cape = Mod("Cape (ab12)", Files(
                ("opt/0/" + A + ".patch_0", "core"),
                ("opt/1/" + A + ".patch_0", "blue"),
                ("opt/2/" + A + ".patch_0", "red")));
            var dir = Path.Combine(Root, "Echelon");
            Directory.CreateDirectory(Path.Combine(dir, "images"));
            File.WriteAllBytes(Path.Combine(dir, "images", "ab12_cover.png"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "images", "ab12_blue.png"), new byte[] { 4, 5 });
            var file = Path.Combine(dir, "library.json");
            string Set(int i) => "{\"hash\": \"" + A + "\", \"files\": [\"opt/" + i + "/" + A + ".patch_0\"], \"deployed_as\": null}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                "{\"id\": \"ab12\", \"name\": \"Cape Colours\", \"folder\": " + Json.Write(cape) + ", \"enabled\": true, \"deployed\": true," +
                " \"sets\": [" + Set(0) + ", " + Set(1) + ", " + Set(2) + "]," +
                " \"options\": [" +
                "{\"name\": \"Core\", \"enabled\": true, \"sets\": [0], \"choices\": [], \"selected\": null}," +
                "{\"name\": \"Colour\", \"enabled\": true, \"sets\": [], \"choices\": [{\"name\": \"Blue\", \"sets\": [1]}, {\"name\": \"Red\", \"sets\": [2]}], \"selected\": 0}]}]}",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "state.json"),
                "{\"version\": 2, \"active_profile\": \"p\", \"profiles\": [{\"id\": \"p\", \"name\": \"Default\", \"entries\": [{\"type\": \"mod\", \"id\": \"ab12\"}]}]," +
                " \"library\": {\"ab12\": {\"id\": \"ab12\", \"description\": \"Capes in two colours.\", \"image\": \"ab12_cover.png\", \"nexus\": {\"mod_id\": 777, \"version\": \"\"}, \"tags\": [\"Cape\", \" Lua script \", \"cape\"], \"options\": [" +
                "{\"name\": \"Core\", \"description\": \"Required.\", \"image\": null, \"include\": [0], \"subs\": []}," +
                "{\"name\": \"Colour\", \"description\": \"\", \"image\": null, \"include\": [], \"subs\": [" +
                "{\"name\": \"Blue\", \"description\": \"Sky blue.\", \"image\": \"ab12_blue.png\", \"include\": [1]}," +
                "{\"name\": \"Red\", \"description\": \"\", \"image\": \"missing.png\", \"include\": [2]}]}]}}}",
                new UTF8Encoding(false));
            Cli("import-echelon", "--file", file);
            var mod = ((List<object>)((Dictionary<string, object>)Json.Parse(Cli("state")))["mods"]).OfType<Dictionary<string, object>>().Single(x => (string)x["name"] == "Cape Colours");
            Assert.Equal("Capes in two colours.", mod["description"]);
            // its source: the Nexus Mods page, with no version where Echelon left it blank
            Assert.Equal(777L, mod["nexus_mod"]);
            Assert.Null(Library.Load().Mods.Single().Nexus.Version);
            // its labels, each once
            Assert.Equal(new[] { "Cape", "Lua script" }, Library.Load().Mods.Single().Tags.ToArray());
            Assert.Equal(new[] { "Cape", "Lua script" }, Library.Load().Tags.ToArray());
            // copied into this program's own folder: Echelon may delete its copy
            var cover = (string)mod["icon"];
            Assert.StartsWith(Path.Combine(Store.Home, "pictures"), cover);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(cover));
            var options = ((List<object>)mod["options"]).OfType<Dictionary<string, object>>().ToList();
            Assert.Equal("Required.", options[0]["description"]);
            var subs = ((List<object>)options[1]["subs"]).OfType<Dictionary<string, object>>().ToList();
            Assert.Equal("Sky blue.", subs[0]["description"]);
            Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes((string)subs[0]["icon"]));
            Assert.StartsWith(Path.Combine(Store.Home, "pictures"), (string)subs[0]["icon"]);
            // a picture Echelon names but does not have is no picture
            Assert.Null(subs[1]["icon"]);
            // and it all stays after the library is read again
            Assert.Equal("Sky blue.", Library.Load().Mods.Single().Layout[1].SubOptions[0].Description);
        }

        /// <summary>A label made before any mod carries it is kept in the library, and stays there
        /// when the last mod lets it go; only deleting it takes it away (the user, 2026-10-24).</summary>
        [Fact]
        public void ALabelIsKeptBeforeAnyModCarriesIt()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("tag-new", "later");
            Assert.Contains("0  later", Cli("tags"));
            Assert.Contains("later", Library.Load().Tags);
            Cli("tag", "One", "later");
            Assert.Contains("1  later", Cli("tags"));
            Cli("untag", "One", "later");
            Assert.Contains("0  later", Cli("tags"));
            Cli("tag-delete", "later");
            Assert.DoesNotContain("later", Cli("tags"));
        }

        /// <summary>A patch file the library does not account for is the hardest kind of mistake
        /// to find: the game's own integrity check does not know these files and never removes
        /// them (HS历史学家 on bilibili, 2026-10-25). check says what is missing, what no mod
        /// wants, and what is not the file the library holds.</summary>
        [Fact]
        public void CheckSaysWhatTheGameFolderHasThatTheLibraryDoesNot()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("deploy");
            Assert.Contains("6 files checked", Cli("check"));
            Assert.Contains("exactly what the library asks for", Cli("check"));

            // another manager's file, or one an interrupted deploy left behind
            File.WriteAllText(Path.Combine(Data, A + ".patch_7"), "stray");
            var spare = CliCode(1, "check");
            Assert.Contains("spare    " + A + ".patch_7", spare);
            Assert.Contains("that no mod wants", spare);
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));   // a deploy clears it

            // a file taken away behind the program's back
            File.Delete(Path.Combine(Data, A + ".patch_0"));
            Assert.Contains("missing  " + A + ".patch_0", CliCode(1, "check"));
            Cli("deploy");

            // a file under the right name that is not the mod's any more. It has to be put
            // there in place of the old one: writing into the deployed file would write into
            // the library's own copy, since the two names are one file
            File.Delete(Path.Combine(Data, A + ".patch_0"));
            File.WriteAllText(Path.Combine(Data, A + ".patch_0"), "something else entirely");
            Assert.Contains("differs  " + A + ".patch_0", CliCode(1, "check"));
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));
        }

        /// <summary>A mod replaced with a newer build of its own shows the build's version, not
        /// the one recorded when it was downloaded from Nexus Mods, and keeps the name the player
        /// gave it (the user, 2026-10-25: Boot Mod Loader 0.9.0 installed, 0.7.0 shown, and its
        /// own name gone).</summary>
        [Fact]
        public void AReplacedModShowsItsOwnVersionAndKeepsItsName()
        {
            var one = Path.Combine(Root, "Loader v0.7.0.zip");
            var two = Path.Combine(Root, "Loader v0.9.0.zip");
            Zip(one, ("manifest.json", "{\"Version\": 1, \"Name\": \"Loader v0.7.0\"}"), (A + ".patch_0", "seven"));
            Zip(two, ("manifest.json", "{\"Version\": 1, \"Name\": \"Loader v0.9.0\"}"), (A + ".patch_0", "nine"));
            Cli("add", one);
            var lib = Library.Load();
            lib.Mods[0].Nexus = new NexusRef { Game = "helldivers2", Mod = 17754, Version = "0.7.0" };
            lib.Save();
            Cli("rename", "Loader v0.7.0", "Loader");
            Cli("replace", "Loader", two);
            var m = Library.Load().Mods[0];
            Assert.Equal("Loader", m.Name);                       // the player's name stays
            Assert.Equal("Loader v0.9.0", m.RealName);            // the name it came with is the new one
            Assert.Equal("0.9.0", Origins.VersionOf(m).Version);  // what is installed, not the Nexus record
            Assert.Equal(17754, m.Nexus.Mod);                     // the page it comes from is kept
        }

        /// <summary>The library lives outside AppData, where a desktop AI assistant's writes are
        /// not sent anywhere else (the user, 2026-10-25).</summary>
        [Fact]
        public void TheHomeIsNotInAppData()
        {
            var keep = Store.HomeOverride;
            var env = Environment.GetEnvironmentVariable("HD2MOD_HOME");
            Store.HomeOverride = null;
            Environment.SetEnvironmentVariable("HD2MOD_HOME", null);
            try
            {
                var home = Store.Home;
                Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hd2-mod-cli"), home);
                Assert.False(home.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase));
                Assert.False(home.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Store.HomeOverride = keep;
                Environment.SetEnvironmentVariable("HD2MOD_HOME", env);
            }
        }

        /// <summary>Started from inside a packaged app, a program's writes to AppData can be sent
        /// to that app's own copy, which nothing else reads: the library splits in two and a
        /// deploy made from the wrong half rearranges the real game folder (the user,
        /// 2026-10-25). A command that writes refuses before it writes anything; one that only
        /// reads goes on.</summary>
        [Fact]
        public void NoWritesThatWouldLandInAnotherAppsCopy()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var before = File.ReadAllText(Store.PathOf("library.json"));
            Store.Redirected = () => @"C:\elsewhere\LocalCache\Roaming\hd2-mod-cli";
            try
            {
                CliCode(1, "tag-new", "anything");
                CliCode(1, "deploy");
                Assert.Equal(before, File.ReadAllText(Store.PathOf("library.json")));
                Assert.Empty(Deployed());
                Assert.Contains("One", Cli("list"));          // reading is fine
                Cli("status");
            }
            finally { Store.Redirected = () => null; }
            Cli("deploy");
            Assert.NotEmpty(Deployed());
        }

        /// <summary>While one program writes to the game folder, another must not: two deploys
        /// at once each undo the other, and anything read in the middle is about a state on its
        /// way somewhere (the user, 2026-10-25). The mark is ignored once the program that left
        /// it is gone, so a deploy that was killed does not block the next one for ever.</summary>
        [Fact]
        public void OneDeployAtATime()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            // a mark from a program that is running and is not this one
            Deploy.Pid = () => 4242;
            Store.Save("deploying.json", new Dictionary<string, object>
            {
                ["pid"] = (long)System.Diagnostics.Process.GetCurrentProcess().Id,
                ["since"] = DateTime.UtcNow.Ticks,
                ["game"] = Game,
            });
            CliCode(1, "deploy");          // refused: the message goes to the error output
            Assert.Contains("\"deploying\"", Cli("state"));
            Assert.Empty(Deployed());

            // the same mark, from a program that is no longer there
            Store.Save("deploying.json", new Dictionary<string, object>
            {
                ["pid"] = 999999L, ["since"] = DateTime.UtcNow.Ticks, ["game"] = Game,
            });
            Cli("deploy");
            Assert.NotEmpty(Deployed());

            // and a deploy takes its own mark away when it is done
            Assert.False(File.Exists(Store.PathOf("deploying.json")));
            Deploy.Pid = () => System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        /// <summary>A library waiting to be deployed is not a fault. Putting a mod in front moves
        /// the numbering of everything behind it, so most of the game folder stops matching until
        /// the next deploy -- the window must not call that a problem, it already counts it as
        /// work to do (the user, 2026-10-25: the line said 135 files did not match, and all of
        /// them were simply not deployed yet).</summary>
        [Fact]
        public void WorkStillToDoIsNotCalledAFault()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Cli("deploy");
            Cli("add", Mod("Three", Files((A + ".patch_0", "three"))));
            Cli("move", "Three", "--top");
            var out_ = CliCode(1, "check");
            Assert.Contains("differ", out_);                       // the folder really does not match
            Assert.Contains("none of it is a file a deploy would leave alone", out_);
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));
        }

        /// <summary>Making copies into hard links must not record a file as a mod's when the file
        /// under that name is another mod's: numbering moves everything after a mod that was put
        /// in front, and a record like that makes the next deploy skip the write (the user,
        /// 2026-10-25: the window said the loader was deployed while the game ran the old one).
        /// Both bodies are the same length on purpose, since a size that differs hides it.</summary>
        [Fact]
        public void LinkingNeverRecordsAFileAsAModItIsNot()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("deploy", "--copy");          // copies, so there is something for relink to do
            // another manager's file, or one an interrupted deploy left, under a name this
            // library has not deployed yet: the same length as what belongs there
            File.WriteAllText(Path.Combine(Data, A + ".patch_1"), "old");
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Cli("relink");
            Cli("deploy");
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.Equal("two", Body(A + ".patch_1"));
        }

        /// <summary>A new version takes the old one's place on the disk too: an armour pack
        /// leaves hundreds of megabytes behind otherwise (the user, 2026-10-25).</summary>
        [Fact]
        public void ANewVersionTakesTheOldOnesFolderWithIt()
        {
            var one = Path.Combine(Root, "Cape v1.zip");
            var two = Path.Combine(Root, "Cape v2.zip");
            Zip(one, ("Cape/" + A + ".patch_0", "cape v1"));
            Zip(two, ("Cape/" + A + ".patch_0", "cape v2"));
            Cli("add", one, "--name", "Cape");
            var first = Library.Load().Mods[0].Path;
            Assert.True(Directory.Exists(first));
            Cli("replace", "Cape", two);
            var now = Library.Load().Mods[0].Path;
            Assert.NotEqual(first, now);
            Assert.False(Directory.Exists(first), "the old version's folder is still there");
            Assert.True(Directory.Exists(now));
            Assert.Contains("no folders of old versions are left over", Cli("leftovers"));
            // --keep-old keeps it, and then it is a leftover the player can delete
            Cli("replace", "Cape", one, "--keep-old");
            Assert.True(Directory.Exists(now));
            Assert.Contains(Path.GetFileName(now), Cli("leftovers"));
            Cli("leftovers", "--delete", "--all");
            Assert.False(Directory.Exists(now));
        }

        /// <summary>The library folder is often another manager's own (HD2 Arsenal's), so only the
        /// folders this program unpacked may ever be deleted.</summary>
        [Fact]
        public void LeftoversNeverTouchWhatThisProgramDidNotUnpack()
        {
            var mods = Path.Combine(Root, "library");
            Directory.CreateDirectory(Path.Combine(mods, "Someone Elses Mod_AR123456"));
            Directory.CreateDirectory(Path.Combine(mods, "A folder of my own"));
            Directory.CreateDirectory(Path.Combine(mods, "Old Cape_0123456789ab"));
            var out_ = Cli("leftovers");
            Assert.Contains("Old Cape_0123456789ab", out_);
            Assert.DoesNotContain("_AR123456", out_);
            Assert.DoesNotContain("A folder of my own", out_);
            Cli("leftovers", "--delete", "--all");
            Assert.False(Directory.Exists(Path.Combine(mods, "Old Cape_0123456789ab")));
            Assert.True(Directory.Exists(Path.Combine(mods, "Someone Elses Mod_AR123456")));
            Assert.True(Directory.Exists(Path.Combine(mods, "A folder of my own")));
            CliCode(1, "leftovers", "--delete");      // neither folders nor --all: refused
        }

        /// <summary>One label onto several mods in one call (the user's tester, 2026-10-25).</summary>
        [Fact]
        public void OneLabelGoesOntoSeveralModsAtOnce()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("add", Mod("Three", Files(("3a1c2d3e4f506172.patch_0", "three"))));
            Cli("tag", "Three", "armour");
            Cli("tag-many", "armour", "One", "Two");
            Assert.Contains("3  armour", Cli("tags"));
            Assert.Equal(new[] { "armour" }, Library.Load().Mods[0].Tags.ToArray());
            Cli("tag-many", "armour", "One");           // again: the label is not doubled
            Assert.Equal(new[] { "armour" }, Library.Load().Mods[0].Tags.ToArray());
            CliCode(1, "tag-many", "armour");   // a label with no mod behind it is refused
        }

        /// <summary>A mod packs its picture under whatever name the author chose, and Arsenal shows
        /// it; so does this (the user's tester, 2026-10-25).</summary>
        [Fact]
        public void AModsPictureIsFoundUnderAnyOfItsNames()
        {
            var named = Mod("One", Files((A + ".patch_0", "one"), ("cover.jpg", "a picture")));
            var alone = Mod("Two", Files((B + ".patch_0", "two"), ("screenshot_01.png", "a picture")));
            var many = Mod("Three", Files(("3a1c2d3e4f506172.patch_0", "three"), ("a.png", "one"), ("b.png", "two")));
            Cli("add", named);
            Cli("add", alone);
            Cli("add", many);
            var state = Cli("state");
            Assert.Contains("cover.jpg", state);        // one of the names a picture goes by
            Assert.Contains("screenshot_01.png", state);// the only image in the folder
            Assert.DoesNotContain("a.png", state);      // two images and no usual name: neither
        }

        /// <summary>A mod that ships one of the scripts a loader replaces is marked as one.</summary>
        [Fact]
        public void LoadersAreMarked()
        {
            byte[] Lua(ulong id)
            {
                var a = Archive(Bytes("-- a script"), (Deploy.LuaType, id));
                BitConverter.GetBytes(Deploy.LuaType).CopyTo(a, 0x48 + 8);
                return a;
            }
            Cli("add", Mod("Loader", new Dictionary<string, byte[]> { [A + ".patch_0"] = Lua(Deploy.LoaderScripts[0]) }));
            Cli("add", Mod("Plain", new Dictionary<string, byte[]> { [A + ".patch_0"] = Lua(0x1234567890ABCDEF) }));
            var st = (Dictionary<string, object>)Json.Parse(Cli("state"));
            var loader = Json.List(st, "mods").Cast<Dictionary<string, object>>().ToDictionary(m => (string)m["name"], m => (bool)m["loader"]);
            Assert.True(loader["Loader"]);
            Assert.False(loader["Plain"]);
        }

        [Fact]
        public void LuaModsAreMarked()
        {
            byte[] WithType(ulong type)
            {
                var a = Archive((type, 1));
                BitConverter.GetBytes(type).CopyTo(a, 0x48 + 8);     // the type table's row: the type at +8
                return a;
            }
            Cli("add", Mod("Script", new Dictionary<string, byte[]> { ["opt/" + A + ".patch_0"] = WithType(Deploy.LuaType) },
                           "{\"Name\": \"Script\", \"Options\": [{\"Name\": \"X\", \"Include\": [\"opt\"]}]}"));
            Cli("add", Mod("Texture", new Dictionary<string, byte[]> { [A + ".patch_0"] = WithType(0xCD4238C6A0C69E32) }));
            Cli("set", "Script", "1", "off");    // off and not deployed: still marked, from its folder
            var st = (Dictionary<string, object>)Json.Parse(Cli("state"));
            var lua = Json.List(st, "mods").Cast<Dictionary<string, object>>().ToDictionary(m => (string)m["name"], m => (bool)m["lua"]);
            Assert.True(lua["Script"]);
            Assert.False(lua["Texture"]);
        }

        [Fact]
        public void Which()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Assert.Contains("Two", Cli("which", A + ".patch_1"));
        }

        [Fact]
        public void WrongCommandLines()
        {
            CliCode(2, "move", "One", "--top", "--bottom");
            CliCode(2, "set", "One", "1", "maybe");
            CliCode(2, "nonsense");
            CliCode(2, "add");
        }

        [Fact]
        public void GameFolderFromWhatAPlayerPicks()
        {
            Directory.CreateDirectory(Path.Combine(Game, "bin"));
            Assert.Equal(Game, Ops.GameFolderOf(Game));
            Assert.Equal(Game, Ops.GameFolderOf(Data));
            Assert.Equal(Game, Ops.GameFolderOf(Path.Combine(Game, "bin")));
            Assert.Equal(Game, Ops.GameFolderOf(Game + "\\"));
            Assert.Null(Ops.GameFolderOf(Root));
            Assert.Null(Ops.GameFolderOf(Path.Combine(Root, "nothing", "data")));
            Assert.Null(Ops.GameFolderOf(""));
        }

        [Fact]
        public void InitTakesDataAndRefusesAWrongFolderForTheWindow()
        {
            Store.Save("config.json", new Dictionary<string, object>());
            Cli("init", "--game", Data);
            Assert.Equal(Game, Store.GameDir());
            var e = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "init", "--game", Root }, new StringWriter(), e, out var refused));
            Assert.Equal("no_game", refused.Kind);
            Assert.Equal(Game, Store.GameDir());     // the folder set before stays
        }

        [Fact]
        public void ADuplicateNameIsRefusedWithItsName()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Assert.Equal(1, Commands.Run(new[] { "add", Mod("One again", Files((A + ".patch_0", "x"))), "--name", "One" },
                                         new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("duplicate", refused.Kind);
            Assert.Equal("One", refused.Arg);
        }

        [Fact]
        public void TheModFolderIsNeverAppData()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            try
            {
                Store.Save("config.json", new Dictionary<string, object> { ["game_dir"] = Game });
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Root, "local"));     // no HD2 Arsenal
                Assert.Equal(Path.Combine(Path.GetPathRoot(Game), "HD2 Mods"), Store.ModsDir());
                var arsenalMods = Path.Combine(Root, "Arsenal Mods");
                Directory.CreateDirectory(arsenalMods);
                Directory.CreateDirectory(Path.Combine(Root, "local", "hd2arsenal"));
                File.WriteAllText(Path.Combine(Root, "local", "hd2arsenal", "hd2a_data.json"), J(new Dictionary<string, object> { ["userModsDir"] = arsenalMods }));
                Assert.Equal(arsenalMods, Store.ModsDir());                                        // HD2 Arsenal's
                Cli("init", "--mods-dir", Path.Combine(Root, "chosen"));
                Assert.Equal(Path.Combine(Root, "chosen"), Store.ModsDir());                       // the one set
                Assert.False(Store.ModsDir().StartsWith(Store.Home, StringComparison.OrdinalIgnoreCase));
            }
            finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", local); }
        }

        /// <summary>A fake HD2ModManager under a fake LOCALAPPDATA: C (object options), A (none),
        /// B (folder-name options), listed in enabled.json in the order C, A, B.</summary>
        string FakeHd2mm()
        {
            var dir = Path.Combine(Root, "local", "Helldivers2ModManager");
            string M(string name) => Path.Combine(dir, "Mods", name);
            Directory.CreateDirectory(M("A"));
            File.WriteAllText(Path.Combine(M("A"), A + ".patch_0"), "a");
            File.WriteAllText(Path.Combine(M("A"), "manifest.json"), "{\"Guid\":\"g-a\",\"Name\":\"Mod A\"}");
            foreach (var f in new[] { "x", "y" }) { Directory.CreateDirectory(Path.Combine(M("B"), f)); File.WriteAllText(Path.Combine(M("B"), f, A + ".patch_0"), "b-" + f); }
            File.WriteAllText(Path.Combine(M("B"), "manifest.json"), "{\"Guid\":\"g-b\",\"Name\":\"Mod B\",\"Options\":[\"x\",\"y\"]}");
            foreach (var f in new[] { "base", "s1", "s2" }) { Directory.CreateDirectory(Path.Combine(M("C"), f)); File.WriteAllText(Path.Combine(M("C"), f, A + ".patch_0"), "c-" + f); }
            File.WriteAllText(Path.Combine(M("C"), "manifest.json"),
                "{\"Version\":1,\"Guid\":\"g-c\",\"Name\":\"Mod C\",\"Options\":[{\"Name\":\"Pick\",\"SubOptions\":[{\"Name\":\"S1\",\"Include\":[\"s1\"]},{\"Name\":\"S2\",\"Include\":[\"s2\"]}]},{\"Name\":\"Base\",\"Include\":[\"base\"]}]}");
            File.WriteAllText(Path.Combine(dir, "enabled.json"),
                "[{\"Guid\":\"g-c\",\"Enabled\":true,\"Toggled\":[true,false],\"Selected\":[1,0]},{\"Guid\":\"g-a\",\"Enabled\":false,\"Toggled\":[],\"Selected\":[0]},{\"Guid\":\"g-b\",\"Enabled\":true,\"Toggled\":[],\"Selected\":[1]}]");
            return dir;
        }

        [Fact]
        public void Hd2ModManagerIsImportedWithItsOrderAndChoices()
        {
            var dir = FakeHd2mm();
            Assert.Contains("3 mods added from HD2ModManager (2 on), 0 already", Cli("import-hd2mm", "--dir", dir));
            var lib = Library.Load();
            Assert.Equal(new[] { "Mod C", "Mod A", "Mod B" }, lib.Mods.Select(m => m.Name));
            Assert.Equal(new[] { true, false, true }, lib.Mods.Select(m => m.Enabled));
            // C: S2 chosen, Base off; B: its second folder
            Assert.Equal(new[] { "c-s2", "b-y" }, PlanList());
        }

        [Fact]
        public void ImportsMergeWithoutDuplicates()
        {
            var dir = FakeHd2mm();
            Cli("add", Mod("Mine", Files((B + ".patch_0", "mine"))));
            Cli("import-hd2mm", "--dir", dir);
            Assert.Contains("0 mods added from HD2ModManager (0 on), 3 already", Cli("import-hd2mm", "--dir", dir));
            Assert.Equal(new[] { "Mine", "Mod C", "Mod A", "Mod B" }, Library.Load().Mods.Select(m => m.Name));
            Cli("import-hd2mm", "--dir", dir, "--replace");
            Assert.Equal(new[] { "Mod C", "Mod A", "Mod B" }, Library.Load().Mods.Select(m => m.Name));
        }

        [Fact]
        public void ModsMoveBetweenFoldersAsSteamMovesGames()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files((B + ".patch_0", "two")));
            Cli("add", one);
            Cli("add", two);
            var target = Path.Combine(Root, "Second Library");
            Cli("folder-add", target);
            var folders = Storage.Folders(Library.Load(), Game);
            Assert.Equal(Path.Combine(Root, "library"), folders[0].Path);                // the default first
            Assert.True(folders[0].IsDefault);
            Assert.Contains(folders, f => f.Path == target && f.Registered && f.Mods.Count == 0);
            Assert.Contains(folders, f => f.Path == Path.Combine(Root, "mods") && f.Mods.Count == 2);

            Assert.Contains("moved One", Cli("relocate", "One", "--into", target));
            Assert.False(Directory.Exists(one));
            Assert.Equal(Path.Combine(target, "One"), Library.Load().Mods[0].Path);
            Assert.Contains("copied Two", Cli("relocate", "Two", "--into", target, "--keep"));
            Assert.True(Directory.Exists(two), "kept for the manager that had it");
            Assert.Equal(new[] { "one", "two" }, PlanList());

            // a library folder is not a mod's own folder: never moved
            Cli("add", target, "--name", "The whole folder");
            Assert.Equal(1, Commands.Run(new[] { "relocate", "The whole folder", "--into", Path.Combine(Root, "x") }, new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("protected", refused.Kind);
        }

        [Fact]
        public void NormMatchesPython()
        {
            Assert.Equal(@"C:\a\c", Paths.Norm(@"C:\a\b\..\c\"));
            Assert.Equal(@"C:\a\b", Paths.Norm("C:/a/./b//"));
            Assert.Equal(@"C:\x", Paths.Norm(@"C:\..\x"));
            Assert.Equal(@"..\x", Paths.Norm(@"..\x"));
            Assert.Equal(@"\\srv\share\x", Paths.Norm(@"\\srv\share\y\..\x"));
            Assert.Equal(@"C:\a\b", Paths.Join(@"C:\a", "b"));
            Assert.Equal("C:/a/b", Paths.Join("C:/a/", "b"));
        }
    }
}
