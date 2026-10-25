using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A mod manager found on this computer whose list can be imported.</summary>
    public class ImportSource
    {
        /// <summary>"arsenal", "hd2mm" or "echelon".</summary>
        public string Key, Name, Where;
        public int Mods;
    }

    /// <summary>Other mod managers' lists, read only, merged into the library: a mod already in
    /// it (the same folder, the same manifest Guid, or the same name) is left as it is.</summary>
    public static class Importers
    {
        /// <summary>HD2ModManager's own folder (its Mods\ and enabled.json), when it is there.</summary>
        public static string Hd2mmDir()
        {
            var b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var d = string.IsNullOrEmpty(b) ? null : Path.Combine(b, "Helldivers2ModManager");
            return d != null && Directory.Exists(Path.Combine(d, "Mods")) ? d : null;
        }

        /// <summary>Echelon's own list, when it is there: %APPDATA%\Echelon\library.json, which it
        /// writes for other tools to read (its author asked, 2026-10-13). HD2QD_ECHELON names
        /// another file, for tests.</summary>
        public static string EchelonFile()
        {
            var said = Environment.GetEnvironmentVariable("HD2QD_ECHELON");
            if (!string.IsNullOrEmpty(said)) return File.Exists(said) ? said : null;
            var b = Environment.GetEnvironmentVariable("APPDATA");
            var f = string.IsNullOrEmpty(b) ? null : Path.Combine(b, "Echelon", "library.json");
            return f != null && File.Exists(f) ? f : null;
        }

        /// <summary>Echelon's list (format "echelon-library", version 1), read only: each mod's
        /// folder, the name it is called there, whether it is on, and the options it has chosen.
        /// An option with choices is a pick-one, and "selected" is the choice that is on. The
        /// options are matched to the mod's own manifest by name when it is deployed.</summary>
        public static Library ReadEchelon(string file = null)
        {
            file = file ?? EchelonFile();
            if (file == null) throw new ModError("Echelon's library.json was not found", "no_source");
            var d = Store.ReadJson(file) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var format = Json.Str(d, "format");
            if (format != "echelon-library") throw new ModError("this is not Echelon's library.json (format " + (format ?? "none") + ")", "bad_format");
            if (Json.Long(d, "version") != 1) throw new ModError("Echelon's library.json is version " + Json.Long(d, "version") + ", which this program does not read", "bad_version");
            var lib = new Library { ImportedFrom = "Echelon" };
            var theirIds = new List<string>();
            foreach (var m in Json.List(d, "mods").OfType<Dictionary<string, object>>())
            {
                var folder = Json.Str(m, "folder");
                if (string.IsNullOrEmpty(folder)) continue;
                // Echelon keeps its own copy of the mod, one folder per set (opt/0, opt/1, ...)
                // and no manifest.json, so its options are the only thing that says which folder
                // belongs to which option: they come in as the mod's layout.
                var sets = Json.List(m, "sets").OfType<Dictionary<string, object>>().ToList();
                List<string> Folders(Dictionary<string, object> o, string key)
                {
                    var out_ = new List<string>();
                    foreach (var x in Json.List(o, key))
                    {
                        if (!(x is long i) || i < 0 || i >= sets.Count) continue;
                        var first = Json.List(sets[(int)i], "files").OfType<string>().FirstOrDefault() ?? "";
                        int cut = first.LastIndexOf('/');
                        var dir = cut < 0 ? "" : first.Substring(0, cut).Replace('/', '\\');
                        if (dir.Length > 0 && !out_.Contains(dir, StringComparer.OrdinalIgnoreCase)) out_.Add(dir);
                    }
                    return out_;
                }
                var options = new List<OptionChoice>();
                var layout = new List<ManifestOption>();
                foreach (var o in Json.List(m, "options").OfType<Dictionary<string, object>>())
                {
                    var choices = Json.List(o, "choices").OfType<Dictionary<string, object>>().ToList();
                    bool picked = o.TryGetValue("selected", out var sel) && sel != null;
                    long which = picked ? Json.Long(o, "selected") : 0;
                    var name = Json.Str(o, "name") ?? "";
                    options.Add(new OptionChoice
                    {
                        Name = name,
                        Enabled = Json.Bool(o, "enabled", true),
                        Subs = choices.Select((c, i) => new SubChoice { Name = Json.Str(c, "name") ?? "", Enabled = i == which }).ToList(),
                    });
                    layout.Add(new ManifestOption
                    {
                        Name = name,
                        Include = Folders(o, "sets"),
                        SubOptions = choices.Select(c => new ManifestOption { Name = Json.Str(c, "name") ?? "", Include = Folders(c, "sets") }).ToList(),
                    });
                }
                // nothing to say where the files are: leave the folder to be read as it is
                if (!layout.Any(o => o.Include.Count > 0 || o.SubOptions.Any(x => x.Include.Count > 0))) layout.Clear();
                lib.Mods.Add(new Mod
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Json.Str(m, "name") ?? Path.GetFileName(folder.TrimEnd('\\')),
                    Path = folder, Source = folder,
                    Enabled = Json.Bool(m, "enabled", true),
                    Options = options, Layout = layout,
                });
                theirIds.Add(Json.Str(m, "id") ?? "");
            }
            // What library.json leaves out is in Echelon's own state.json. It is Echelon's own
            // file, not one written for other tools, so a version not known here leaves it unread.
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            var state = EchelonState(Path.Combine(dir, "state.json"));
            if (state == null) return lib;
            // the pictures and the words: a tester, 2026-10-25, saw neither come in. Echelon keeps
            // them beside its library, not in the mod's folder.
            var theirLib = Json.Obj(state, "library");
            for (int i = 0; i < lib.Mods.Count; i++)
            {
                var their = Json.Obj(theirLib, theirIds[i]);
                if (their == null) continue;
                var mod = lib.Mods[i];
                mod.Description = Json.Str(their, "description");
                // its labels there, as labels here (plain names; Echelon's colours have no place here)
                foreach (var t in Json.List(their, "tags"))
                    if (t is string tag && tag.Trim().Length > 0 && !mod.Tags.Contains(tag.Trim(), StringComparer.CurrentCultureIgnoreCase))
                        mod.Tags.Add(tag.Trim());
                mod.ImportPicture = EchelonPicture(dir, Json.Str(their, "image"));
                // where it came from (the same tester: "来源也都没有显示"); a version left blank
                // there is no version
                var nx = Json.Obj(their, "nexus");
                if (nx != null && Json.Long(nx, "mod_id") > 0)
                    mod.Nexus = new NexusRef
                    {
                        Game = Nexus.Game, Mod = Json.Long(nx, "mod_id"), File = Json.Long(nx, "file_id"),
                        Version = string.IsNullOrWhiteSpace(Json.Str(nx, "version")) ? null : Json.Str(nx, "version").Trim(),
                    };
                var theirOptions = Json.List(their, "options").OfType<Dictionary<string, object>>().ToList();
                foreach (var o in mod.Layout)
                {
                    var t = theirOptions.FirstOrDefault(x => Json.Str(x, "name") == o.Name);
                    if (t == null) continue;
                    o.Description = Json.Str(t, "description") ?? "";
                    o.Image = EchelonPicture(dir, Json.Str(t, "image"));
                    var theirSubs = Json.List(t, "subs").OfType<Dictionary<string, object>>().ToList();
                    foreach (var sub in o.SubOptions)
                    {
                        var ts = theirSubs.FirstOrDefault(x => Json.Str(x, "name") == sub.Name);
                        if (ts == null) continue;
                        sub.Description = Json.Str(ts, "description") ?? "";
                        sub.Image = EchelonPicture(dir, Json.Str(ts, "image"));
                    }
                }
            }
            // library.json lists the mods in the order they were added, not in the load order
            // (a tester, 2026-10-25: "乱序"). The load order is the active profile's: top first,
            // the bottom one deployed last and so winning, as here (proven on 0.37.4: the top mod
            // got patch_0). Mods the profile does not list follow in library.json's order.
            var order = EchelonOrder(state);
            if (order != null)
            {
                var ranked = lib.Mods.Select((x, i) => (Mod: x, Rank: order.IndexOf(theirIds[i]))).ToList();
                lib.Mods = ranked.OrderBy(x => x.Rank < 0 ? int.MaxValue : x.Rank).Select(x => x.Mod).ToList();
            }
            return lib;
        }

        /// <summary>Echelon's state.json, when it is there and of the version read here (2).</summary>
        static Dictionary<string, object> EchelonState(string stateFile)
        {
            try
            {
                if (!File.Exists(stateFile)) return null;
                var s = Store.ReadJson(stateFile) as Dictionary<string, object>;
                return s != null && Json.Long(s, "version") == 2 ? s : null;
            }
            catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
            {
                return null;
            }
        }

        /// <summary>The mod ids of Echelon's active profile, top first; null when there is none.</summary>
        static List<string> EchelonOrder(Dictionary<string, object> state)
        {
            var want = Json.Str(state, "active_profile") ?? Json.Str(state, "deployed_profile");
            var profiles = Json.List(state, "profiles").OfType<Dictionary<string, object>>().ToList();
            var p = profiles.FirstOrDefault(x => Json.Str(x, "id") == want) ?? (profiles.Count == 1 ? profiles[0] : null);
            if (p == null) return null;
            var ids = Json.List(p, "entries").OfType<Dictionary<string, object>>()
                          .Where(e => Json.Str(e, "type") == "mod")      // not the separators
                          .Select(e => Json.Str(e, "id")).Where(x => !string.IsNullOrEmpty(x)).ToList();
            return ids.Count > 0 ? ids : null;
        }

        /// <summary>A picture Echelon names: a file in its images folder (the mod's own picture is
        /// there, seen on 0.37.4), else in its icons folder, else a full path; null when none is
        /// there.</summary>
        static string EchelonPicture(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
                foreach (var sub in new[] { "images", "icons" })
                {
                    var f = Path.Combine(dir, sub, name);
                    if (File.Exists(f)) return f;
                }
            }
            catch (ArgumentException) { }       // a name with characters no path can hold
            return null;
        }

        /// <summary>Pictures another manager keeps outside the mod's folder, copied into this
        /// program's own pictures folder when the mod is added, so they stay when that manager
        /// is gone: the mod's (found the way a site's picture is, Site.PictureOf) and its
        /// options'. A picture that cannot be copied is left out; the mod comes in all the same.</summary>
        public static void CopyPictures(Mod m)
        {
            string Copy(string from, string name)
            {
                if (string.IsNullOrEmpty(from) || !File.Exists(from)) return null;
                try
                {
                    var into = Path.Combine(Store.Home, "pictures");
                    Directory.CreateDirectory(into);
                    var to = Path.Combine(into, name + Path.GetExtension(from).ToLowerInvariant());
                    File.Copy(from, to, true);
                    return to;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            }
            Copy(m.ImportPicture, m.Id);
            m.ImportPicture = null;
            int n = 0;
            foreach (var o in m.Layout)
            {
                o.Image = Copy(o.Image, m.Id + "-" + ++n);
                foreach (var s in o.SubOptions) s.Image = Copy(s.Image, m.Id + "-" + ++n);
            }
        }

        /// <summary>The managers found, with how many mods each lists.</summary>
        public static List<ImportSource> Found()
        {
            var out_ = new List<ImportSource>();
            if (Store.ArsenalFile() != null)
            {
                try { out_.Add(new ImportSource { Key = "arsenal", Name = "HD2 Arsenal", Where = Store.ArsenalModsDir() ?? Store.ArsenalFile(), Mods = Ops.ImportArsenal().Lib.Mods.Count }); }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
            }
            var mm = Hd2mmDir();
            if (mm != null)
            {
                try { out_.Add(new ImportSource { Key = "hd2mm", Name = "HD2ModManager", Where = Path.Combine(mm, "Mods"), Mods = ReadHd2mm(mm).Mods.Count }); }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
            }
            var ec = EchelonFile();
            if (ec != null)
            {
                try
                {
                    var theirs = ReadEchelon(ec);
                    var where = theirs.Mods.Count > 0 ? Path.GetDirectoryName(theirs.Mods[0].Path.TrimEnd('\\')) ?? ec : ec;
                    out_.Add(new ImportSource { Key = "echelon", Name = "Echelon", Where = where, Mods = theirs.Mods.Count });
                }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
            }
            return out_.Where(x => x.Mods > 0).ToList();
        }

        /// <summary>HD2ModManager's list: its Mods\ folders in its enabled.json's order (those it does
        /// not list after them, off), each with its on/off and choices: Toggled[i] is option i on,
        /// Selected[i] the sub-option chosen in it (for folder-name options, Selected[0] the folder).</summary>
        public static Library ReadHd2mm(string dir = null)
        {
            dir = dir ?? Hd2mmDir();
            if (dir == null) throw new ModError("HD2ModManager's folder was not found", "no_source");
            var states = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var enabledFile = Path.Combine(dir, "enabled.json");
            if (File.Exists(enabledFile))
                foreach (var e in (Store.ReadJson(enabledFile) as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    var g = Json.Str(e, "Guid");
                    if (string.IsNullOrEmpty(g) || states.ContainsKey(g)) continue;
                    states[g] = e;
                    order.Add(g);
                }
            var mods = new List<(int Rank, Mod Mod)>();
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(dir, "Mods")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Manifest man;
                try { man = Manifest.Read(folder); }
                catch (ModError) { continue; }
                states.TryGetValue(man?.Guid ?? "", out var st);
                var toggled = Json.List(st, "Toggled");
                var selected = Json.List(st, "Selected").Select(x => x is long n ? (int)n : 0).ToList();
                var choices = new List<OptionChoice>();
                for (int i = 0; man != null && i < man.Options.Count; i++)
                {
                    var o = man.Options[i];
                    int pick = man.FolderOptions ? (selected.Count > 0 ? selected[0] : 0) : (i < selected.Count ? selected[i] : 0);
                    choices.Add(new OptionChoice
                    {
                        Name = o.Name,
                        Enabled = man.FolderOptions || i >= toggled.Count || !(toggled[i] is bool b) || b,
                        Subs = o.SubOptions.Select((x, j) => new SubChoice { Name = x.Name, Enabled = j == pick }).ToList(),
                    });
                }
                var mod = new Mod
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = !string.IsNullOrEmpty(man?.Name) ? man.Name : Path.GetFileName(folder),
                    Path = folder, Source = folder, Enabled = Json.Bool(st, "Enabled"), Options = choices,
                };
                int rank = man?.Guid != null && order.Contains(man.Guid) ? order.IndexOf(man.Guid) : int.MaxValue;
                mods.Add((rank, mod));
            }
            var lib = new Library { ImportedFrom = "HD2ModManager" };
            lib.Mods.AddRange(mods.OrderBy(x => x.Rank).Select(x => x.Mod));
            return lib;
        }

        /// <summary>How many pairs of mods from different sources ship the same resources with
        /// different bytes, so one of them is overridden. group says where each mod came from
        /// (by id; a mod not in it was in the library before). Mods from different managers were
        /// never deployed together, so nothing says which should win: the one lower in the list
        /// does, the one imported later (the user, 2026-10-25). 0 when the plan cannot be read.</summary>
        public static int CrossClashes(Library lib, Dictionary<string, int> group)
        {
            List<Deploy.Clash> found;
            try { found = Deploy.Clashes(Deploy.Plan(lib)); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { return 0; }
            var groupOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lib.Mods) groupOf[m.Name] = group.TryGetValue(m.Id ?? "", out var g) ? g : 0;
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in found.Where(c => !c.Same))
                for (int i = 0; i < c.Mods.Count; i++)
                    for (int j = i + 1; j < c.Mods.Count; j++)
                    {
                        groupOf.TryGetValue(c.Mods[i], out var a);
                        groupOf.TryGetValue(c.Mods[j], out var b);
                        if (a != b) pairs.Add(string.Compare(c.Mods[i], c.Mods[j], StringComparison.OrdinalIgnoreCase) < 0
                                              ? c.Mods[i] + "\u001f" + c.Mods[j] : c.Mods[j] + "\u001f" + c.Mods[i]);
                    }
            return pairs.Count;
        }

        /// <summary>The mods of from that are not in into yet, added at its bottom in from's order;
        /// the ones already there (same folder, manifest Guid or name) are named in Skipped. A mod
        /// that comes with an id the library already uses is given a new one: another manager's
        /// ids are its own, and two mods with one id confuse everything that goes by id.</summary>
        public static (List<Mod> Added, List<Mod> Skipped) Merge(Library into, Library from)
        {
            string GuidOf(string folder)
            {
                try { return Directory.Exists(folder) ? Manifest.Read(folder)?.Guid : null; }
                catch (ModError) { return null; }
            }
            var ids = new HashSet<string>(into.Mods.Select(m => m.Id ?? ""), StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(into.Mods.Select(m => Paths.Abs(m.Path).TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(into.Mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var guids = new HashSet<string>(into.Mods.Select(m => GuidOf(m.Path)).Where(g => !string.IsNullOrEmpty(g)), StringComparer.OrdinalIgnoreCase);
            var added = new List<Mod>();
            var skipped = new List<Mod>();
            foreach (var m in from.Mods)
            {
                var g = GuidOf(m.Path);
                if (paths.Contains(Paths.Abs(m.Path).TrimEnd('\\')) || names.Contains(m.Name) || (g != null && guids.Contains(g))) { skipped.Add(m); continue; }
                if (string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id)) { m.Id = Guid.NewGuid().ToString(); ids.Add(m.Id); }
                into.Mods.Add(m);
                added.Add(m);
                paths.Add(Paths.Abs(m.Path).TrimEnd('\\'));
                names.Add(m.Name);
                if (g != null) guids.Add(g);
            }
            if (added.Count > 0)
                into.ImportedFrom = string.IsNullOrEmpty(into.ImportedFrom) ? from.ImportedFrom
                                  : into.ImportedFrom.Contains(from.ImportedFrom) ? into.ImportedFrom : into.ImportedFrom + "; " + from.ImportedFrom;
            return (added, skipped);
        }
    }
}
