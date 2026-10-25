using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Hd2Mod
{
    /// <summary>One mod's update check.</summary>
    public class UpdateInfo
    {
        public string ModId, Name, Channel, Page;
        /// <summary>"update" (newer there), "current", "unknown" (the mod's own version is not known),
        /// "no_source", "no_check" (a site without a check), "error".</summary>
        public string Status;
        public string Current, Latest, Why;
        /// <summary>GitHub: the release asset to download; Nexus: the newest file's id.</summary>
        public string Download;
        public long LatestFile, LatestTime;
    }

    /// <summary>Update checks on each mod's own channel (the user, 2026-10-24: "从 GitHub 下下来的，
    /// 就从 GitHub 检查更新"): Nexus Mods through its API and the player's key (files.json and its
    /// file_updates chain: which file replaced which), GitHub through its releases (latest).
    /// Read only; nothing is downloaded unless the player asks.</summary>
    public static class Updates
    {
        public static string GitHubApi = Environment.GetEnvironmentVariable("HD2QD_GITHUB_API") ?? "https://api.github.com";

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return c;
        }

        static string Get(string url)
        {
            HttpResponseMessage r;
            try { r = http.GetAsync(url).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("cannot be reached: " + e.Message, "offline"); }
            using (r)
            {
                var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if ((int)r.StatusCode == 404) throw new ModError("no such repository or no release", "missing");
                if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) throw new ModError("too many requests for now", "busy");
                if (!r.IsSuccessStatusCode) throw new ModError("answered " + (int)r.StatusCode, "error");
                return body;
            }
        }

        /// <summary>Every mod checked (progress: done, all). Mods without an origin first learn the one
        /// HD2 Arsenal recorded, when it has.</summary>
        public static List<UpdateInfo> Check(Library lib, Action<int, int> progress = null)
        {
            if (Origins.FromArsenal(lib) > 0) lib.Save();
            var out_ = new List<UpdateInfo>();
            int i = 0;
            foreach (var m in lib.Mods)
            {
                out_.Add(One(m));
                progress?.Invoke(++i, lib.Mods.Count);
            }
            return out_;
        }

        /// <summary>One mod checked on its own channel. A check that cannot be made is a
        /// result too: "no_source", "no_check", or "error" with its reason in Why.</summary>
        public static UpdateInfo One(Mod m)
        {
            var (channel, page) = Origins.Of(m);
            var u = new UpdateInfo { ModId = m.Id, Name = m.Name, Channel = channel, Page = page };
            try
            {
                if (m.Nexus != null) CheckNexus(m.Nexus, u);
                else if (m.GitHub != null) CheckGitHub(m.GitHub, u);
                else u.Status = m.Page != null ? "no_check" : "no_source";
            }
            catch (ModError e) { u.Status = "error"; u.Why = e.Kind ?? e.Message; }
            return u;
        }

        static void CheckNexus(NexusRef r, UpdateInfo u)
        {
            u.Status = "no_check";
        }

        static Dictionary<string, object> NewestMain(List<Dictionary<string, object>> files) =>
            files.Where(f => (Json.Str(f, "category_name") ?? "") == "MAIN").OrderByDescending(f => Json.Long(f, "uploaded_timestamp")).FirstOrDefault();

        static void CheckGitHub(GitHubRef r, UpdateInfo u)
        {
            var d = Json.Parse(Get(GitHubApi.TrimEnd('/') + "/repos/" + r.Owner + "/" + r.Repo + "/releases/latest")) as Dictionary<string, object>;
            var tag = Json.Str(d, "tag_name");
            u.Current = r.Tag;
            u.Latest = tag;
            u.Page = Json.Str(d, "html_url") ?? u.Page;
            u.Download = Json.List(d, "assets").OfType<Dictionary<string, object>>()
                .Select(a => Json.Str(a, "browser_download_url"))
                .FirstOrDefault(x => x != null && new[] { ".zip", ".7z", ".rar" }.Any(e => x.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
            u.Status = string.IsNullOrEmpty(r.Tag) ? "unknown" : string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase) ? "current" : "update";
        }

        /// <summary>A GitHub release's archive downloaded into the mod folder and put in the library as
        /// the mod's new version (its place, on/off and options kept); the archive is not kept.</summary>
        public static Mod ApplyGitHub(string modId, UpdateInfo u)
        {
            if (string.IsNullOrEmpty(u.Download)) throw new ModError("the release has no zip, 7z or rar to download", "no_asset");
            var folder = Store.ModsDir();
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(new Uri(u.Download).AbsolutePath);
            var file = Paths.Join(folder, Uri.UnescapeDataString(name));
            HttpResponseMessage r;
            try { r = http.GetAsync(u.Download).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("the download did not start: " + e.Message, "offline"); }
            using (r)
            {
                if (!r.IsSuccessStatusCode) throw new ModError("the download was refused: " + (int)r.StatusCode, "error");
                using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write)) r.Content.CopyToAsync(dst).GetAwaiter().GetResult();
            }
            var lib = Library.Load();
            var mod = lib.Mods.FirstOrDefault(m => m.Id == modId) ?? throw new ModError("not in the library any more", "gone");
            var unpacked = Ops.SourceFolder(file, Store.Config());
            try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            var manifest = Manifest.Read(unpacked);
            mod.Options = Manifest.Merged(manifest, mod.Options);
            mod.Path = unpacked;
            mod.Source = u.Page;
            mod.GitHub = new GitHubRef { Owner = mod.GitHub?.Owner, Repo = mod.GitHub?.Repo, Tag = u.Latest };
            if (!string.IsNullOrEmpty(manifest?.Name)) mod.Name = manifest.Name;
            lib.Save();
            return mod;
        }
    }
}
