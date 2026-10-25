using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>What the last check of this program's own releases found.</summary>
    public class SelfUpdateInfo
    {
        /// <summary>The newest release's tag and page, or null when none counts for this build.</summary>
        public string Latest, Page;
        public bool Prerelease;
        /// <summary>Whether it is newer than the version running.</summary>
        public bool Newer;
        /// <summary>When it was checked (Unix seconds); 0: never.</summary>
        public long CheckedAt;
    }

    /// <summary>This program's own updates, from its GitHub releases (the user, 2026-10-25). The
    /// development build is published there as pre-releases, the official build as releases: the
    /// official build counts only releases, so it is never offered a build without Nexus Mods;
    /// the development build counts both. Read only: nothing is downloaded, the release page is
    /// opened for the player. The last result is kept, so the window can show it before (or
    /// without) going online; the automatic check runs at most once a day.</summary>
    public static class SelfUpdate
    {
        public const string Repo = "pat-pat-patrick/hd2-mod-cli";
        const string File = "self_update.json";
        const long Day = 24 * 3600;

        public const bool Official = false;

        /// <summary>A tag's version as numbers: "v0.9.0", "0.9", "v1.2.3-dev" -> [0,9,0]...; null when it has none.</summary>
        public static int[] Numbers(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            return Enumerable.Range(1, 3).Select(i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0).ToArray();
        }

        public static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        /// <summary>The newest release that counts for this build, out of GitHub's list of them.</summary>
        public static SelfUpdateInfo Pick(List<Dictionary<string, object>> releases, string running, bool official)
        {
            var now = Numbers(running);
            var best = releases
                .Where(r => !Json.Bool(r, "draft", false) && (!official || !Json.Bool(r, "prerelease", false)))
                .Select(r => (R: r, N: Numbers(Json.Str(r, "tag_name"))))
                .Where(x => x.N != null)
                .OrderByDescending(x => x.N[0]).ThenByDescending(x => x.N[1]).ThenByDescending(x => x.N[2])
                .FirstOrDefault();
            if (best.R == null) return new SelfUpdateInfo();
            return new SelfUpdateInfo
            {
                Latest = Json.Str(best.R, "tag_name"), Page = Json.Str(best.R, "html_url"),
                Prerelease = Json.Bool(best.R, "prerelease", false),
                Newer = now != null && Compare(best.N, now) > 0,
            };
        }

        /// <summary>GitHub asked now; the result kept.</summary>
        public static SelfUpdateInfo Check()
        {
            var list = Json.Parse(Updates.Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + Repo + "/releases?per_page=30")) as List<object>;
            var found = Pick((list ?? new List<object>()).OfType<Dictionary<string, object>>().ToList(), StateDoc.Version, Official);
            found.CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var d = Store.Load(File);
            d["checked_at"] = found.CheckedAt;
            d["latest"] = found.Latest;
            d["page"] = found.Page;
            d["prerelease"] = found.Prerelease;
            Store.Save(File, d);
            return found;
        }

        /// <summary>The last result, compared again with the version running now: once the new
        /// version is the one running, it is not newer any more. A pre-release a development
        /// build found in the same folder is nothing to the official build.</summary>
        public static SelfUpdateInfo Last()
        {
            var d = Store.Load(File);
            var latest = Json.Str(d, "latest");
            var pre = Json.Bool(d, "prerelease", false);
            if (Official && pre) latest = null;
            var n = Numbers(latest);
            var now = Numbers(StateDoc.Version);
            return new SelfUpdateInfo
            {
                Latest = latest, Page = latest == null ? null : Json.Str(d, "page"), Prerelease = latest != null && pre,
                CheckedAt = Official && pre ? 0 : Json.Long(d, "checked_at"),
                Newer = n != null && now != null && Compare(n, now) > 0,
            };
        }

        /// <summary>Whether to check by itself (on unless turned off).</summary>
        public static bool Auto
        {
            get => Json.Bool(Store.Load(File), "auto", true);
            set { var d = Store.Load(File); d["auto"] = value; Store.Save(File, d); }
        }

        /// <summary>Whether the automatic check is due: on, and not checked in the last day.</summary>
        public static bool Due() => Auto && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Last().CheckedAt >= Day;
    }
}
