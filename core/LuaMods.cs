using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>One Lua script in a mod's patch archives.</summary>
    public class LuaScript
    {
        public ulong Id;
        /// <summary>"-- HD2-Addon: mods/a/b" (or -Boot, -Options, -Lang): the kind and the name.</summary>
        public string Header, Name;
        /// <summary>Whether Boot Mod Loader can find it: its pattern is stricter than Chef's.</summary>
        public bool BmlName;
        /// <summary>"-- HD2-Requires: chef-shared-loader" among its first lines.</summary>
        public bool ChefOnly;
        /// <summary>Compiled code rather than text; LuaJIT's cannot run on the game's Lua.</summary>
        public bool Bytecode, LuaJitBytecode;
        /// <summary>Where it does not compile on Lua 5.1, else null.</summary>
        public (int Line, string Message)? Error;
        /// <summary>What the error is, in words a player can follow: "luajit_number" (0x...ULL),
        /// "goto" (goto and labels), "new_operator" (//, &amp;, |, ~, &lt;&lt;, &gt;&gt;), "bom", "locals",
        /// "upvalues", else "syntax".</summary>
        public string ErrorKind;
        /// <summary>LuaJIT libraries it uses without a fallback: "ffi", "bit", "jit".</summary>
        public List<string> Jit = new List<string>();
        /// <summary>For the script "boot": whose it is ("bml", "chef_stub", "chef", "bsl", "other") and its version.</summary>
        public string BootKind, BootVersion;
        /// <summary>Whether it wraps the old menus' register calls (a translation pack does).</summary>
        public bool WrapsMenus;
    }

    /// <summary>What a mod's Lua is: its scripts, what it needs, what stops it.</summary>
    public class ModLua
    {
        public Mod Mod;
        public List<LuaScript> Scripts = new List<LuaScript>();
        /// <summary>A loader itself: "bml", "chef", "bsl"; null otherwise.</summary>
        public string Loader;
        /// <summary>"any" (Boot Mod Loader or Chef Shared Loader), "chef" (Chef Shared Loader
        /// only), null (no mod script).</summary>
        public string Needs;
        /// <summary>For Needs "chef": "declared" (HD2-Requires or HD2-Boot) or "name" (a name Boot
        /// Mod Loader's pattern does not take).</summary>
        public string NeedsWhy;
        public LuaScript Boot => Scripts.FirstOrDefault(s => s.Id == LuaMods.BootId);
        /// <summary>The loader's version, from its boot or Chef's core.</summary>
        public string LoaderVersion => Scripts.Select(s => s.BootVersion).FirstOrDefault(v => v != null);
        public IEnumerable<string> Addons => Scripts.Where(s => s.Name != null && (s.Header == "Addon" || s.Header == "Boot")).Select(s => s.Name).Distinct();
        /// <summary>The scripts the game's Lua cannot run.</summary>
        public IEnumerable<LuaScript> Broken => Scripts.Where(s => s.Error != null || s.LuaJitBytecode || s.Jit.Count > 0);
        /// <summary>The mod it translates (by name, see LuaMods.Packs), else null.</summary>
        public Mod PackOf;
        public string PackOfName;
    }

    /// <summary>A problem the load order gives the mods' scripts, in codes; the window and
    /// the command line say them in their own words.</summary>
    public class LuaIssue
    {
        /// <summary>"boot_conflict", "two_loaders", "no_loader", "needs_chef", "same_name",
        /// "cannot_run", "pack_alone".</summary>
        public string Kind;
        /// <summary>The mods it is about, the one that wins (or matters) first.</summary>
        public List<Mod> Mods = new List<Mod>();
        public string Detail;
    }

    /// <summary>The library's Lua before a deploy (the user, 2026-10-26: several loaders take
    /// the game's boot, only one runs, and the player cannot tell why; the mod manager checks
    /// and says it, the player decides). Everything here is read from the mods' patch archives
    /// and the game's logs outside the game: nothing is changed, nothing runs in it.</summary>
    public static class LuaMods
    {
        /// <summary>The script the engine starts from (murmur64 of "boot").</summary>
        public const ulong BootId = 0xF476DF93691895FA;
        /// <summary>Chef Shared Loader's core, which its start-up stub loads (murmur64 of
        /// "mods/chef_shared_loader/core_1").</summary>
        public const ulong ChefCoreId = 0xC27789387E64579A;

        // the headers both loaders read (BML: the first line, its letters; Chef: also - and more parts)
        static readonly Regex HeaderRx = new Regex(@"^-- HD2-(Addon|Boot|Options|Lang): (mods/[A-Za-z0-9_\-]+/[A-Za-z0-9_\-/]*[A-Za-z0-9_\-])[ \t]*\r?\n");
        static readonly Regex BmlRx = new Regex(@"^-- HD2-Addon: mods/[A-Za-z0-9_]+/[A-Za-z0-9_/]+\r?\n");

        // ------------------------------------------------------------ one archive

        static readonly Dictionary<string, (string Sig, List<LuaScript> Scripts)> known =
            new Dictionary<string, (string, List<LuaScript>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The window's: a state read takes only the archives already read (in this run
        /// or in lua_scan.json), so the list is never kept waiting; the rest is read in the
        /// background (Warm) and the state read again. Reading and checking every script of a
        /// library of 65 mods took 0.94 s, which held the list back at every start (the user,
        /// 2026-10-26: "明显没以前快了"). The command line reads everything.</summary>
        public static bool Lazy;

        // what was found in each archive, kept between runs in lua_scan.json by the archive's
        // size and time; a new version of the checks reads every archive again
        const int ScanVersion = 2;
        static Dictionary<string, (string Sig, List<LuaScript> Scripts)> disk;
        static bool diskDirty;

        /// <summary>The Lua scripts of one patch archive, read once while it is unchanged; with
        /// knownOnly, null when it would have to be read now.</summary>
        public static List<LuaScript> Read(string path, bool knownOnly = false)
        {
            var fi = new FileInfo(path);
            var sig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            lock (known)
            {
                if (known.TryGetValue(path, out var k) && k.Sig == sig) return k.Scripts;
                LoadDisk();
                if (disk.TryGetValue(path, out var d) && d.Sig == sig) { known[path] = d; return d.Scripts; }
            }
            if (knownOnly) return null;
            var out_ = new List<LuaScript>();
            if (Deploy.Types(path).Contains(Deploy.LuaType))
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    foreach (var r in Deploy.Rows(path).Where(r => r.Type == Deploy.LuaType && r.Size > 8 && r.Size < 64 << 20))
                    {
                        var data = new byte[r.Size];
                        f.Seek((long)r.Off, SeekOrigin.Begin);
                        if (Deploy.Fill(f, data) < data.Length) continue;
                        int n = (int)Math.Min(BitConverter.ToUInt32(data, 0), (uint)(data.Length - 8));
                        var body = new byte[n];
                        Array.Copy(data, 8, body, 0, n);
                        out_.Add(Look(r.Id, body));
                    }
            lock (known)
            {
                known[path] = (sig, out_);
                disk[path] = (sig, out_);
                diskDirty = true;
            }
            return out_;
        }

        static void LoadDisk()
        {
            if (disk != null) return;
            disk = new Dictionary<string, (string, List<LuaScript>)>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> d;
            try { d = Store.Load("lua_scan.json"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return; }
            if (Json.Long(d, "version") != ScanVersion) return;
            var files = Json.Obj(d, "files");
            if (files == null) return;
            foreach (var kv in files)
            {
                if (!(kv.Value is Dictionary<string, object> f)) continue;
                var scripts = Json.List(f, "scripts").OfType<Dictionary<string, object>>().Select(x =>
                {
                    var s = new LuaScript
                    {
                        Id = ulong.Parse(Json.Str(x, "id") ?? "0", NumberStyles.HexNumber), Header = Json.Str(x, "header"), Name = Json.Str(x, "name"),
                        BmlName = Json.Bool(x, "bml"), ChefOnly = Json.Bool(x, "chef_only"), Bytecode = Json.Bool(x, "bytecode"),
                        LuaJitBytecode = Json.Bool(x, "luajit_bytecode"), BootKind = Json.Str(x, "boot_kind"), BootVersion = Json.Str(x, "version"),
                        WrapsMenus = Json.Bool(x, "wraps_menus"), Jit = Json.List(x, "jit").OfType<string>().ToList(),
                    };
                    if (Json.Str(x, "error") != null) { s.Error = ((int)Json.Long(x, "line"), Json.Str(x, "error")); s.ErrorKind = Json.Str(x, "error_kind"); }
                    return s;
                }).ToList();
                disk[kv.Key] = (Json.Str(f, "sig"), scripts);
            }
        }

        /// <summary>What was read, kept for the next run; only the archives seen are kept.</summary>
        static void SaveDisk(HashSet<string> seen)
        {
            Dictionary<string, object> files;
            lock (known)
            {
                if (!diskDirty && (disk == null || disk.Keys.All(seen.Contains))) return;
                files = new Dictionary<string, object>();
                foreach (var kv in disk.Where(kv => seen.Contains(kv.Key)))
                    files[kv.Key] = new Dictionary<string, object>
                    {
                        ["sig"] = kv.Value.Sig,
                        ["scripts"] = kv.Value.Scripts.Select(s => (object)new Dictionary<string, object>
                        {
                            ["id"] = s.Id.ToString("x16"), ["header"] = s.Header, ["name"] = s.Name, ["bml"] = s.BmlName, ["chef_only"] = s.ChefOnly,
                            ["bytecode"] = s.Bytecode, ["luajit_bytecode"] = s.LuaJitBytecode, ["boot_kind"] = s.BootKind, ["version"] = s.BootVersion,
                            ["wraps_menus"] = s.WrapsMenus, ["jit"] = s.Jit.Cast<object>().ToList(),
                            ["line"] = (long)(s.Error?.Line ?? 0), ["error"] = s.Error?.Message, ["error_kind"] = s.ErrorKind,
                        }).ToList(),
                    };
                diskDirty = false;
            }
            try { Store.Save("lua_scan.json", new Dictionary<string, object> { ["version"] = (long)ScanVersion, ["files"] = files }); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Tests only: as if the program started again (what is on disk stays).</summary>
        internal static void ForgetRun()
        {
            lock (known) { known.Clear(); disk = null; diskDirty = false; }
        }

        /// <summary>Every archive read that was not yet, and kept: the window's background reading.</summary>
        public static void Warm(Library lib) => Of(lib, false, out _);

        /// <summary>One script's facts from its bytes.</summary>
        public static LuaScript Look(ulong id, byte[] body)
        {
            var s = new LuaScript { Id = id };
            if (body.Length > 0 && body[0] == 0x1B)
            {
                s.Bytecode = true;
                s.LuaJitBytecode = body.Length > 2 && body[1] == (byte)'L' && body[2] == (byte)'J';
                return s;
            }
            var head = Latin(body, 1400);
            var m = HeaderRx.Match(head);
            if (m.Success)
            {
                s.Header = m.Groups[1].Value;
                s.Name = m.Groups[2].Value;
                s.BmlName = BmlRx.IsMatch(head);
            }
            s.ChefOnly = head.Substring(0, Math.Min(head.Length, 1200)).IndexOf("\n-- HD2-Requires: chef-", StringComparison.Ordinal) >= 0;
            s.Error = LuaSyntax.Check(body);
            if (s.Error != null) s.ErrorKind = KindOf(s.Error.Value, body);
            if (s.Error == null)
            {
                var tokens = LuaSyntax.Tokens(body);
                if (tokens != null)
                {
                    s.Jit = JitUses(tokens);
                    // the menus by name or as rawget(_G, "ModOptionsMenu"), as ActionCam简体中文 reads them
                    s.WrapsMenus = tokens.Any(t => t.Kind == LuaSyntax.T.Name && (t.Text == "register_option" || t.Text == "register_binding"))
                                && tokens.Any(t => (t.Kind == LuaSyntax.T.Name || t.Kind == LuaSyntax.T.String) && (t.Text == "ModOptionsMenu" || t.Text == "ModBindingsMenu"));
                }
            }
            if (id == BootId) (s.BootKind, s.BootVersion) = BootOf(Latin(body, 1 << 20));
            if (id == ChefCoreId)
            {
                var v = Regex.Match(Latin(body, 1 << 20), @"local VERSION = '(\d+\.\d+\.\d+)'");
                if (v.Success) s.BootVersion = v.Groups[1].Value;
            }
            return s;
        }

        /// <summary>The kind of a syntax error, from its message and the line it is on.</summary>
        static string KindOf((int Line, string Message) e, byte[] body)
        {
            var msg = e.Message;
            if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF && e.Line == 1) return "bom";
            if (msg.Contains("local variables")) return "locals";
            if (msg.Contains("upvalues")) return "upvalues";
            if (msg.StartsWith("malformed number") && Regex.IsMatch(msg, @"(?i)\d[0-9a-f]*(u?ll|i)'$")) return "luajit_number";
            var lines = Encoding.UTF8.GetString(body).Split('\n');
            var line = e.Line >= 1 && e.Line <= lines.Length ? lines[e.Line - 1] : "";
            var code = Regex.Replace(line, @"--.*$|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'", "");
            if (Regex.IsMatch(code, @"\bgoto\s+[A-Za-z_]|::[A-Za-z_]\w*::")) return "goto";
            if (Regex.IsMatch(code, @"//|<<|>>|[^~]~[^=]|(?<![A-Za-z0-9_\]\)\s])&|\s&\s|\s\|\s")) return "new_operator";
            return "syntax";
        }

        static string Latin(byte[] b, int max)
        {
            int n = Math.Min(b.Length, max);
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append((char)b[i]);
            return sb.ToString();
        }

        /// <summary>Whose boot it is, by what each loader's boot says about itself.</summary>
        static (string, string) BootOf(string text)
        {
            var bml = Regex.Match(text, @"boot-mod-loader-(\d+\.\d+\.\d+)");
            if (text.StartsWith("-- Boot Mod Loader", StringComparison.Ordinal) || bml.Success)
                return ("bml", bml.Success ? bml.Groups[1].Value : null);
            var first = text.Split('\n')[0];
            if (first.Contains("--@@LOADER@@") && first.Contains("start-up stub")) return ("chef_stub", null);
            if (text.StartsWith("-- Chef Shared Loader", StringComparison.Ordinal))
            {
                var v = Regex.Match(text, @"local VERSION = '(\d+\.\d+\.\d+)'");
                return ("chef", v.Success ? v.Groups[1].Value : null);
            }
            if (text.Contains("CowboyBingusModLoader") && text.Contains("9ba626afa44a3aa3"))
            {
                var v = Regex.Match(text, @"revision = ""loader-v(\d+)""");
                return ("bsl", v.Success ? "v" + v.Groups[1].Value : null);
            }
            return ("other", null);
        }

        /// <summary>The LuaJIT libraries a script uses with nothing to fall back on: a require
        /// of ffi, jit or bit outside a pcall, or ffi.x / bit.x / jit.x on a name the script
        /// never defines and never tests. Comments and strings never count (the tokens have
        /// them resolved).</summary>
        public static List<string> JitUses(List<LuaSyntax.Token> t)
        {
            const int Name = LuaSyntax.T.Name, Str = LuaSyntax.T.String;
            var libs = new[] { "ffi", "jit", "bit" };
            var found = new SortedSet<string>(StringComparer.Ordinal);
            // inside the argument list of pcall( / xpcall(: a failure there is caught
            var guardedFrom = new Stack<int>();
            int depth = 0;
            var guardDepths = new HashSet<int>();
            for (int i = 0; i < t.Count; i++)
            {
                var k = t[i].Kind;
                if (k == '(')
                {
                    depth++;
                    if (i > 0 && t[i - 1].Kind == Name && (t[i - 1].Text == "pcall" || t[i - 1].Text == "xpcall")) guardDepths.Add(depth);
                }
                else if (k == ')') { guardDepths.Remove(depth); depth--; }
                else if (k == Name && t[i].Text == "require" && guardDepths.Count == 0 && (i == 0 || (t[i - 1].Kind != '.' && t[i - 1].Kind != ':')))
                {
                    string lib = null;
                    if (i + 1 < t.Count && t[i + 1].Kind == Str) lib = t[i + 1].Text;
                    else if (i + 3 < t.Count && t[i + 1].Kind == '(' && t[i + 2].Kind == Str && t[i + 3].Kind == ')') lib = t[i + 2].Text;
                    if (lib != null && libs.Contains(lib)) found.Add(lib);
                }
            }
            foreach (var lib in libs)
            {
                if (found.Contains(lib)) continue;
                bool used = false, defined = false, tested = false;
                for (int i = 0; i < t.Count; i++)
                {
                    if (t[i].Kind != Name || t[i].Text != lib) continue;
                    int prev = i > 0 ? t[i - 1].Kind : -1, next = i + 1 < t.Count ? t[i + 1].Kind : -1;
                    if (prev == '.' || prev == ':') continue;                    // a field of something else
                    if (prev == LuaSyntax.T.Local || prev == LuaSyntax.T.Function || next == '=') defined = true;
                    else if (prev == ',' || prev == '(') { if (IsDeclared(t, i)) defined = true; }
                    if (prev == LuaSyntax.T.If || prev == LuaSyntax.T.Not || prev == LuaSyntax.T.And || prev == LuaSyntax.T.Or ||
                        prev == LuaSyntax.T.Eq || prev == LuaSyntax.T.Ne || prev == LuaSyntax.T.Elseif || prev == LuaSyntax.T.While ||
                        next == LuaSyntax.T.And || next == LuaSyntax.T.Or || next == LuaSyntax.T.Then || next == LuaSyntax.T.Eq || next == LuaSyntax.T.Ne)
                        tested = true;
                    if (next == '.' || next == ':' || next == '[') used = true;
                }
                if (used && !defined && !tested) found.Add(lib);
            }
            return found.ToList();
        }

        /// <summary>Whether the name at i stands in a local list or a parameter list.</summary>
        static bool IsDeclared(List<LuaSyntax.Token> t, int i)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                var k = t[j].Kind;
                if (k == ',' || k == LuaSyntax.T.Name) continue;
                if (k == LuaSyntax.T.Local) return true;
                if (k == '(') return j > 0 && (t[j - 1].Kind == LuaSyntax.T.Function || (t[j - 1].Kind == LuaSyntax.T.Name && j > 1 && t[j - 2].Kind == LuaSyntax.T.Function));
                return false;
            }
            return false;
        }

        // ------------------------------------------------------------ the library

        /// <summary>Each mod's Lua, from the folders a deploy would take (its chosen options),
        /// whether it is on or not. Mods with no Lua at all are left out.</summary>
        public static List<ModLua> Of(Library lib) => Of(lib, false, out _);

        /// <summary>The same; with knownOnly, the archives not yet read are left out and counted
        /// in missing (the answer is then incomplete, and not to be shown).</summary>
        public static List<ModLua> Of(Library lib, bool knownOnly, out int missing)
        {
            missing = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var out_ = new List<ModLua>();
            foreach (var m in lib.Mods)
            {
                if (!Directory.Exists(m.Path)) continue;
                List<string> folders;
                try { folders = Deploy.IncludedFolders(m); }
                catch (ModError) { continue; }
                var ml = new ModLua { Mod = m };
                foreach (var folder in folders.Where(Directory.Exists))
                    foreach (var f in Deploy.List(folder).Where(f => Deploy.Patch.IsMatch(f)))
                        try
                        {
                            var path = Paths.Join(folder, f);
                            seen.Add(path);
                            var scripts = Read(path, knownOnly);
                            if (scripts == null) missing++;
                            else ml.Scripts.AddRange(scripts);
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                if (ml.Scripts.Count == 0) continue;
                var boot = ml.Boot;
                if (ml.Scripts.Any(s => s.Id == ChefCoreId) || boot?.BootKind == "chef") ml.Loader = "chef";
                else if (boot?.BootKind == "bml") ml.Loader = "bml";
                else if (boot?.BootKind == "bsl") ml.Loader = "bsl";
                if (ml.Loader == null)
                {
                    var mods = ml.Scripts.Where(s => s.Header == "Addon" || s.Header == "Boot").ToList();
                    if (mods.Count > 0)
                    {
                        ml.Needs = mods.Any(s => s.ChefOnly || s.Header == "Boot" || !s.BmlName) ? "chef" : "any";
                        if (ml.Needs == "chef") ml.NeedsWhy = mods.Any(s => s.ChefOnly || s.Header == "Boot") ? "declared" : "name";
                    }
                }
                out_.Add(ml);
            }
            Packs(out_);
            if (!knownOnly) SaveDisk(seen);
            return out_;
        }

        // a translation pack's name: the mod's own name and a language or "translation"
        static readonly Regex PackSuffix = new Regex(@"^(.+?)[_\-](zh_cn|zh_tw|zh_hans|zh_hant|zh|cn|chs|cht|tw|ru|ja|jp|ko|kr|de|fr|es|pt|pt_br|br|pl|it|tr|uk|ua|cs|translation|translations|lang|i18n|l10n)$",
                                                          RegexOptions.IgnoreCase);

        /// <summary>A translation pack and the mod it translates: in the same author's folder, its
        /// name the mod's name and a language, and it wraps the old menus' register calls (as
        /// ActionCam简体中文 does). Only that: a mod that only looks like one is not marked.</summary>
        static void Packs(List<ModLua> all)
        {
            var byName = new Dictionary<string, ModLua>(StringComparer.Ordinal);
            foreach (var ml in all)
                foreach (var n in ml.Addons)
                    if (!byName.ContainsKey(n)) byName[n] = ml;
            foreach (var ml in all)
            {
                var addons = ml.Addons.ToList();
                if (addons.Count != 1 || !ml.Scripts.Any(s => s.WrapsMenus)) continue;
                var m = PackSuffix.Match(addons[0]);
                if (!m.Success) continue;
                ml.PackOfName = m.Groups[1].Value;
                if (byName.TryGetValue(ml.PackOfName, out var original) && original != ml) ml.PackOf = original.Mod;
            }
        }

        /// <summary>The scripts the load order gives the game: whose boot runs, which loader that
        /// is, and the problems, the enabled mods only.</summary>
        public static (ModLua BootOwner, List<ModLua> BootMods, string Running, List<LuaIssue> Issues) Check(Library lib, List<ModLua> all)
        {
            var on = all.Where(x => x.Mod.Enabled).ToList();          // in load order: the last one wins
            var issues = new List<LuaIssue>();
            var bootMods = on.Where(x => x.Boot != null).ToList();
            var owner = bootMods.LastOrDefault();
            string running;
            if (owner == null) running = null;
            else
            {
                var kind = owner.Boot.BootKind;
                if (kind == "chef_stub") running = on.Any(x => x.Scripts.Any(s => s.Id == ChefCoreId)) ? "chef" : null;
                else if (kind == "other") running = "other";
                else running = kind;
            }
            if (bootMods.Count > 1)
                issues.Add(new LuaIssue { Kind = "boot_conflict", Mods = Enumerable.Reverse(bootMods).Select(x => x.Mod).ToList() });
            var loaders = on.Where(x => x.Loader != null).ToList();
            if (loaders.Select(x => x.Loader).Distinct().Count() > 1)
                issues.Add(new LuaIssue { Kind = "two_loaders", Mods = loaders.OrderByDescending(x => x == owner).Select(x => x.Mod).ToList(), Detail = running });
            var wanting = on.Where(x => x.Needs != null).ToList();
            if (wanting.Count > 0 && running != "bml" && running != "chef" && running != "bsl")
                issues.Add(new LuaIssue { Kind = "no_loader", Mods = (owner != null ? new[] { owner.Mod } : new Mod[0]).Concat(wanting.Select(x => x.Mod)).ToList(),
                                          Detail = owner != null ? "other" : null });
            else if (running != "chef")
            {
                var chefOnly = wanting.Where(x => x.Needs == "chef").ToList();
                if (chefOnly.Count > 0) issues.Add(new LuaIssue { Kind = "needs_chef", Mods = chefOnly.Select(x => x.Mod).ToList(), Detail = running });
            }
            // two mods with the same name: the later one (a higher patch) runs
            var names = new Dictionary<string, List<ModLua>>(StringComparer.Ordinal);
            foreach (var x in on)
                foreach (var n in x.Addons)
                {
                    if (!names.TryGetValue(n, out var l)) names[n] = l = new List<ModLua>();
                    if (!l.Contains(x)) l.Add(x);
                }
            foreach (var kv in names.Where(kv => kv.Value.Count > 1))
                issues.Add(new LuaIssue { Kind = "same_name", Mods = Enumerable.Reverse(kv.Value).Select(x => x.Mod).ToList(), Detail = kv.Key });
            foreach (var x in on.Where(x => x.Broken.Any()))
                issues.Add(new LuaIssue { Kind = "cannot_run", Mods = { x.Mod } });
            foreach (var x in on.Where(x => x.PackOfName != null && (x.PackOf == null || !x.PackOf.Enabled)))
                issues.Add(new LuaIssue { Kind = "pack_alone", Mods = new[] { x.Mod }.Concat(x.PackOf != null ? new[] { x.PackOf } : new Mod[0]).ToList(), Detail = x.PackOfName });
            return (owner, bootMods, running, issues);
        }

        // ------------------------------------------------------------ the last session

        /// <summary>One mod in Boot Mod Loader's log of the last session.</summary>
        public class Started
        {
            public string Name, State, Why;
            public int Ms;
        }

        public class Session
        {
            public DateTime When;
            public string Loader;
            public int StartedCount = -1, Total = -1;
            public List<Started> Mods = new List<Started>();
        }

        public static string GameFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arrowhead", "Helldivers2");

        /// <summary>Tests only: the folder of the game's logs and dumps.</summary>
        public static string GameFolderOverride;

        static string Folder => GameFolderOverride ?? GameFolder;

        static readonly Regex Loaded = new Regex(@"^\d\d:\d\d:\d\d (mods/\S+) \(patch_\d+, \d+ bytes\): loaded in (\d+) ms");
        static readonly Regex Failed = new Regex(@"^\d\d:\d\d:\d\d (mods/\S+) \(patch_\d+\): FAILED: (.*)$");
        static readonly Regex NoCompile = new Regex(@"^\d\d:\d\d:\d\d (mods/\S+) \(patch_\d+\): does not compile on Lua 5\.1: (.*)$");
        static readonly Regex StartedRx = new Regex(@"^\d\d:\d\d:\d\d started (\d+) of (\d+) mods");
        static readonly Regex Head = new Regex(@"^\d\d:\d\d:\d\d == (boot-mod-loader-\S+) (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)");

        /// <summary>Boot Mod Loader's last session, from its log: its block from the last
        /// "== boot-mod-loader-" line. Null when there is no log. Read only.</summary>
        static (string Stamp, Session Value) sessionKnown;
        static (string Stamp, List<(string, DateTime)> Value) crashesKnown;

        public static Session LastSession()
        {
            var path = Path.Combine(Folder, "boot_mod_loader.log");
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            var stamp = path + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            lock (Folder) if (sessionKnown.Stamp == stamp) return sessionKnown.Value;
            var read = ReadSession(path);
            lock (Folder) sessionKnown = (stamp, read);
            return read;
        }

        static Session ReadSession(string path)
        {
            string[] lines;
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(f, Encoding.UTF8))
                    lines = r.ReadToEnd().Split('\n');
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            int start = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains(" == boot-mod-loader-")) { start = i; break; }
            if (start < 0) return null;
            var s = new Session();
            var h = Head.Match(lines[start].TrimEnd('\r'));
            if (h.Success)
            {
                s.Loader = h.Groups[1].Value;
                DateTime.TryParseExact(h.Groups[2].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out s.When);
            }
            // a failure's stack follows on the lines after it, which carry no time
            Started last = null;
            for (int i = start + 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                Match m;
                if ((m = Loaded.Match(line)).Success) s.Mods.Add(last = new Started { Name = m.Groups[1].Value, State = "loaded", Ms = int.Parse(m.Groups[2].Value) });
                else if ((m = Failed.Match(line)).Success) s.Mods.Add(last = new Started { Name = m.Groups[1].Value, State = "failed", Why = m.Groups[2].Value });
                else if ((m = NoCompile.Match(line)).Success) s.Mods.Add(last = new Started { Name = m.Groups[1].Value, State = "no_compile", Why = m.Groups[2].Value });
                else if ((m = StartedRx.Match(line)).Success) { s.StartedCount = int.Parse(m.Groups[1].Value); s.Total = int.Parse(m.Groups[2].Value); last = null; }
                else if (last != null && last.State == "failed" && line.Length > 0 && !Regex.IsMatch(line, @"^\d\d:\d\d:\d\d ")) last.Why += "\n" + line;
                else last = null;
            }
            return s;
        }

        // ------------------------------------------------------------ crashes

        /// <summary>The game's crash dumps (dumps\dump-*.dmp), newest first, by when the file was
        /// written: the time in its name is when the game started, not when it crashed.</summary>
        public static List<(string File, DateTime When)> Crashes(int max = 20)
        {
            var dir = Path.Combine(Folder, "dumps");
            if (!Directory.Exists(dir)) return new List<(string, DateTime)>();
            try
            {
                var di = new DirectoryInfo(dir);
                var stamp = dir + ":" + di.LastWriteTimeUtc.Ticks + ":" + max;
                if (crashesKnown.Stamp == stamp) return crashesKnown.Value;
                var found = di.EnumerateFiles("dump-*.dmp")
                    .Select(f => (f.Name, f.LastWriteTime))
                    .OrderByDescending(x => x.LastWriteTime).Take(max).ToList();
                crashesKnown = (stamp, found);
                return found;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new List<(string, DateTime)>(); }
        }

        /// <summary>The mods a deploy of this program had put in the game at a time: the last
        /// deploy before it (deploy_history.json); null when none is recorded that early.</summary>
        public static (DateTime When, List<string> Mods)? DeployedAt(DateTime when)
        {
            var list = Json.List(Store.Load("deploy_history.json"), "deploys").OfType<Dictionary<string, object>>()
                .Select(d => (When: DateTimeOffset.FromUnixTimeSeconds(Json.Long(d, "time")).LocalDateTime, Mods: Json.List(d, "mods").OfType<string>().ToList()))
                .Where(d => d.When <= when).OrderBy(d => d.When).ToList();
            if (list.Count == 0) return null;
            return list[list.Count - 1];
        }

        /// <summary>A deploy recorded: when, and the mods it put in the game, in load order.</summary>
        public static void Record(IEnumerable<string> mods)
        {
            var d = Store.Load("deploy_history.json");
            var list = Json.List(d, "deploys").OfType<Dictionary<string, object>>().Cast<object>().ToList();
            list.Add(new Dictionary<string, object> { ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["mods"] = mods.Distinct().Cast<object>().ToList() });
            if (list.Count > 50) list = list.Skip(list.Count - 50).ToList();
            d["deploys"] = list;
            Store.Save("deploy_history.json", d);
        }
    }
}
