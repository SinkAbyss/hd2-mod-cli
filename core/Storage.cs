using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A folder that holds mods, as Steam's library folders hold games.</summary>
    public class LibraryFolder
    {
        public string Path, Drive;
        /// <summary>Where new mods go (added zips, downloads, mods moved here by default).</summary>
        public bool IsDefault;
        /// <summary>Added by the player (else listed because mods are in it).</summary>
        public bool Registered;
        /// <summary>On the game's drive: its mods are deployed as hard links.</summary>
        public bool GameDrive;
        public bool InAppData;
        public long Free, Total, Bytes;
        public List<Mod> Mods = new List<Mod>();
    }

    /// <summary>The mod library folders (the user, 2026-10-24: "像 Steam 设置里设置游戏库一样"): the
    /// ones added, the default (where new mods go), and every folder the library's mods are in;
    /// moving mods between them, as Steam moves an install folder.</summary>
    public static class Storage
    {
        public static List<string> Registered() =>
            Json.List(Store.Config(), "library_folders").OfType<string>().Where(s => s.Length > 0).ToList();

        static void SaveRegistered(Dictionary<string, object> cfg, List<string> list)
        {
            cfg["library_folders"] = list.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
            Store.Save("config.json", cfg);
        }

        public static string AddFolder(string path)
        {
            var p = Paths.Abs(path);
            if (System.IO.Path.GetPathRoot(p).TrimEnd('\\') == p.TrimEnd('\\')) throw new ModError("a whole drive cannot be a mod folder: " + p, "drive_root", p);
            Directory.CreateDirectory(p);
            var cfg = Store.Config();
            var list = Json.List(cfg, "library_folders").OfType<string>().ToList();
            list.Add(p);
            SaveRegistered(cfg, list);
            return p;
        }

        /// <summary>The folder off the list (its files stay). The default stays: choose another first.</summary>
        public static void RemoveFolder(string path)
        {
            var p = Paths.Abs(path).TrimEnd('\\');
            if (string.Equals(p, Paths.Abs(Store.ModsDir()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ModError("the default mod folder stays on the list: make another one the default first", "is_default", p);
            var cfg = Store.Config();
            SaveRegistered(cfg, Json.List(cfg, "library_folders").OfType<string>().Where(x => !string.Equals(Paths.Abs(x).TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        public static void SetDefault(string path)
        {
            var p = Paths.Abs(path);
            Directory.CreateDirectory(p);
            var cfg = Store.Config();
            cfg["mods_dir"] = p;
            var list = Json.List(cfg, "library_folders").OfType<string>().ToList();
            list.Add(p);
            SaveRegistered(cfg, list);
        }

        static string Key(string p) => Paths.Abs(p).TrimEnd('\\').ToLowerInvariant();

        /// <summary>Every mod folder: the default first, then the ones added, then the folders the
        /// library's mods are in (a mod belongs to the folder its own folder is in).</summary>
        public static List<LibraryFolder> Folders(Library lib, string game)
        {
            var seen = new Dictionary<string, LibraryFolder>();
            var order = new List<LibraryFolder>();
            string data = game != null ? Paths.Join(game, "data") : null;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LibraryFolder Get(string path)
            {
                var k = Key(path);
                if (seen.TryGetValue(k, out var f)) return f;
                var abs = Paths.Abs(path);
                f = new LibraryFolder
                {
                    Path = abs, Drive = System.IO.Path.GetPathRoot(abs),
                    InAppData = abs.StartsWith(appData, StringComparison.OrdinalIgnoreCase) || abs.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                };
                try
                {
                    var d = new DriveInfo(f.Drive);
                    if (d.IsReady) { f.Free = d.AvailableFreeSpace; f.Total = d.TotalSize; }
                }
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException) { }
                // the drive the game is on: compared by volume when both exist, else by drive letter
                f.GameDrive = data != null && (Directory.Exists(abs) ? Deploy.SameDrive(abs, data)
                                                                      : string.Equals(f.Drive, System.IO.Path.GetPathRoot(Paths.Abs(game)), StringComparison.OrdinalIgnoreCase));
                seen[k] = f;
                order.Add(f);
                return f;
            }
            Get(Store.ModsDir()).IsDefault = true;
            foreach (var r in Registered()) Get(r).Registered = true;
            foreach (var m in lib.Mods)
            {
                if (string.IsNullOrEmpty(m.Path)) continue;
                var parent = System.IO.Path.GetDirectoryName(Paths.Abs(m.Path).TrimEnd('\\'));
                if (parent == null) continue;
                var f = Get(parent);
                f.Mods.Add(m);
                if (Directory.Exists(m.Path)) f.Bytes += StateDoc.LibraryBytes(new DirectoryInfo(m.Path));
            }
            return order;
        }

        /// <summary>Mods moved into another mod folder, as Steam moves an install folder: each one's
        /// folder is copied there (a name already taken gets " (2)"), the library points at the copy,
        /// and the old folder is deleted unless keep (another manager may still use it). Refused,
        /// before anything is copied, for a folder that is not the mod's own.</summary>
        public static List<(Mod Mod, string From, string To)> Move(Library lib, IList<Mod> mods, string target, bool keep, string game)
        {
            var into = Paths.Abs(target);
            var plan = new List<(Mod Mod, string From, string To)>();
            foreach (var m in mods)
            {
                var from = Ops.OwnFolder(lib, m, game, "moved");
                if (!Directory.Exists(from)) throw new ModError("its folder is gone: " + from, "gone", from);
                if (string.Equals(System.IO.Path.GetDirectoryName(from.TrimEnd('\\')), into.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                var name = System.IO.Path.GetFileName(from.TrimEnd('\\'));
                var to = Paths.Join(into, name);
                for (int n = 2; Directory.Exists(to) || File.Exists(to) || plan.Any(x => string.Equals(x.To, to, StringComparison.OrdinalIgnoreCase)); n++)
                    to = Paths.Join(into, name + " (" + n + ")");
                plan.Add((m, from, to));
            }
            Directory.CreateDirectory(into);
            var done = new List<(Mod Mod, string From, string To)>();
            foreach (var (m, from, to) in plan)
            {
                var tmp = to + ".moving";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                CopyTree(from, tmp);
                Directory.Move(tmp, to);
                m.Path = to;
                lib.Save();                                  // each one as it is done: a stop halfway loses nothing
                done.Add((m, from, to));
                if (!keep)
                {
                    try { DeleteTree(from); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        throw new ModError("moved to " + to + ", but the old folder could not be deleted (" + e.Message + "): " + from, "delete_failed", from);
                    }
                }
            }
            return done;
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Paths.Join(to, System.IO.Path.GetFileName(f)));
            foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Paths.Join(to, System.IO.Path.GetFileName(d)));
        }

        static void DeleteTree(string folder)
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(folder, true);
        }
    }
}
