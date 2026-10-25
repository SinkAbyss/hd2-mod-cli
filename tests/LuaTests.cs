using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The Lua checks before a deploy (the user, 2026-10-26): the syntax of the game's
    /// Lua 5.1, the LuaJIT libraries, whose boot runs, which loader that is, what each mod needs.
    /// Every rule has a script that must pass and one that must not.</summary>
    public class LuaChecks : Fixture
    {
        static bool Compiles(string src) => LuaSyntax.Check(src) == null;

        [Fact]
        public void TheSyntaxIsLua51s()
        {
            // LuaJIT and Lua 5.2+ syntax: refused; the same thing written for 5.1: compiles
            Assert.False(Compiles("goto done ::done::"));
            Assert.True(Compiles("do local done end"));
            Assert.False(Compiles("local x = 0xf6978e86e4f4b0d5ull"));
            Assert.True(Compiles("local x = 0xf6978e86"));
            Assert.False(Compiles("x = a // b"));
            Assert.True(Compiles("x = math.floor(a / b)"));
            Assert.False(Compiles("x = a & b"));
            Assert.False(Compiles("x = 1..2"));
            Assert.True(Compiles("x = 1 .. 2"));
            // a call split from its function by a line break; in a table constructor 5.1 lets it pass
            Assert.False(Compiles("local f = g\n(h)()"));
            Assert.True(Compiles("local f = g(h)()"));
            Assert.True(Compiles("t = { f\n(x) }"));
            // [[ inside [[ ]]: refused in 5.1; with a level, or [==[ ]==] around it, fine
            Assert.False(Compiles("s = [[ [[x]] ]]"));
            Assert.True(Compiles("s = [=[ [[x]] ]=]"));
            Assert.False(Compiles("--[[ [[ ]] ]] x = 1"));
            // break and return end a block
            Assert.False(Compiles("break"));
            Assert.True(Compiles("while true do break end"));
            Assert.False(Compiles("x = 1; return; y = 2"));
            Assert.True(Compiles("do return end y = 2"));
            Assert.False(Compiles("function f() return ... end"));
            Assert.True(Compiles("function f(...) return ... end"));
            Assert.False(Compiles("f() = 1"));
            Assert.True(Compiles("f().x = 1"));
            // escapes: \z is just z in 5.1; above \255 is refused
            Assert.True(Compiles("x = '\\z'"));
            Assert.False(Compiles("x = '\\300'"));
            Assert.True(Compiles("x = '\\255'"));
            // a UTF-8 BOM is not skipped by loadstring; UTF-8 in strings and comments is fine
            Assert.False(LuaSyntax.Check(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("x = 1")).ToArray()) == null);
            Assert.True(Compiles("x = '中文' -- 中文"));
            Assert.False(Compiles("中文 = 1"));
            // the line is the line PUC 5.1 names
            Assert.Equal(3, LuaSyntax.Check("x = 1\ny = 2\nz = = 3").Value.Line);
            Assert.Contains("'end' expected (to close 'if' at line 1)", LuaSyntax.Check("if x then\ny = 1\n").Value.Message);
        }

        [Fact]
        public void TheParsersLimitsAreLua51s()
        {
            string Locals(int n) => "local " + string.Join(", ", Enumerable.Range(0, n).Select(i => "v" + i));
            Assert.True(Compiles(Locals(200)));
            Assert.Contains("main function has more than 200 local variables", LuaSyntax.Check(Locals(201)).Value.Message);
            // a for loop's three hidden variables count too
            Assert.True(Compiles(Locals(196) + "\nfor i = 1, 2 do end"));
            Assert.False(Compiles(Locals(197) + "\nfor i = 1, 2 do end"));
            string Upvalues(int n) => Locals(n) + "\nfunction f() return " + string.Join(" + ", Enumerable.Range(0, n).Select(i => "v" + i)) + " end";
            Assert.True(Compiles(Upvalues(60)));
            Assert.Contains("has more than 60 upvalues", LuaSyntax.Check(Upvalues(61)).Value.Message);
            // the same upvalue twice is one
            Assert.True(Compiles(Locals(60) + "\nfunction f() return v0 + v0 + v1 end"));
        }

        static List<string> Jit(string src) => LuaMods.JitUses(LuaSyntax.Tokens(Encoding.UTF8.GetBytes(src)));

        [Fact]
        public void LuaJitLibrariesAreFoundOnlyWhereTheyWouldFail()
        {
            Assert.Equal(new[] { "ffi" }, Jit("local ffi = require('ffi')"));
            Assert.Equal(new[] { "ffi" }, Jit("local ffi = require 'ffi'"));
            Assert.Empty(Jit("local ok, ffi = pcall(require, 'ffi')"));                 // it copes without
            Assert.Empty(Jit("local ok, ffi = pcall(function() return require('ffi') end)"));
            Assert.Equal(new[] { "bit" }, Jit("x = bit.band(a, b)"));
            Assert.Empty(Jit("local bit = { band = function() end }\nx = bit.band(a, b)"));  // its own
            Assert.Empty(Jit("if jit then jit.off() end"));                                 // tested first
            Assert.Empty(Jit("local band = bit and bit.band or function() end"));
            Assert.Empty(Jit("-- require('ffi')\nx = 'ffi.cdef'"));                          // comments and strings
            Assert.Empty(Jit("x = t.bit.band(1)"));                                          // somebody's field
            Assert.Empty(Jit("function f(bit) return bit.band(1) end"));                      // a parameter
        }

        const ulong A1 = 0x1000000000000001, A2 = 0x1000000000000002, A3 = 0x1000000000000003;

        string Bml(string version = "0.11.0") => Mod("Boot Mod Loader", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader: runs HD2-Addon mods\nlocal MARKER = \"boot-mod-loader-" + version + "\"\n")),
        });

        string Chef() => Mod("Chef Shared Loader", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Chef Shared Loader start-up stub, version 1 --@@LOADER@@\nlocal x = 1\n"),
                                          (LuaMods.ChefCoreId, "-- Chef Shared Loader - its core\nlocal VERSION = '1.2.0'\n")),
        });

        string Addon(string folder, string name, string extra = "", ulong id = A1) => Mod(folder, new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((id, "-- HD2-Addon: " + name + "\n" + extra + "local x = 1\n")),
        });

        static List<string> Kinds(LuaMods.Session s) => s.Mods.Select(m => m.State).ToList();

        (ModLua Owner, string Running, List<LuaIssue> Issues) Now()
        {
            var lib = Library.Load();
            var (owner, _, running, issues) = LuaMods.Check(lib, LuaMods.Of(lib));
            return (owner, running, issues);
        }

        [Fact]
        public void WhoseBootRunsAndWhatThatMeans()
        {
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            // no loader: the mod does not start
            var (_, running, issues) = Now();
            Assert.Null(running);
            Assert.Equal("no_loader", issues.Single().Kind);

            Cli("add", Bml());
            (_, running, issues) = Now();
            Assert.Equal("bml", running);
            Assert.Empty(issues);

            // Chef after it: its stub's boot wins; two loaders, one boot that does not run
            Cli("add", Chef());
            var now = Now();
            Assert.Equal("Chef Shared Loader", now.Owner.Mod.Name);
            Assert.Equal("chef", now.Running);
            Assert.Equal(new[] { "boot_conflict", "two_loaders" }, now.Issues.Select(i => i.Kind).ToArray());
            Assert.Equal(new[] { "Chef Shared Loader", "Boot Mod Loader" }, now.Issues[0].Mods.Select(m => m.Name).ToArray());

            // the player puts Boot Mod Loader last: its boot wins
            Cli("move", "Boot Mod Loader", "--bottom");
            now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Equal("Boot Mod Loader", now.Issues.First(i => i.Kind == "boot_conflict").Mods[0].Name);

            // a mod only Chef Shared Loader runs, under Boot Mod Loader
            Cli("add", Addon("Radar", "mods/chef/radar", "-- HD2-Requires: chef-shared-loader\n", A2));
            Assert.Contains(Now().Issues, i => i.Kind == "needs_chef" && i.Mods.Single().Name == "Radar");
            Cli("disable", "Chef Shared Loader");
            Assert.DoesNotContain(Now().Issues, i => i.Kind == "boot_conflict" || i.Kind == "two_loaders");

            // a boot nobody knows: named after its mod, and no loader runs
            Cli("add", Mod("Installer", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- swaps files\nlocal y = 2\n")) }));
            now = Now();
            Assert.Equal("Installer", now.Owner.Mod.Name);
            Assert.Equal("other", now.Running);
            Assert.Contains(now.Issues, i => i.Kind == "no_loader");
        }

        [Fact]
        public void SameNamesPacksAndScriptsTheGameCannotRun()
        {
            Cli("add", Bml());
            Cli("add", Addon("Cam", "mods/actioncam/actioncam"));
            Cli("add", Addon("Cam copy", "mods/actioncam/actioncam", "", A2));
            var same = Now().Issues.Single(i => i.Kind == "same_name");
            Assert.Equal("mods/actioncam/actioncam", same.Detail);
            Assert.Equal("Cam copy", same.Mods[0].Name);                       // the later one runs
            Cli("remove", "Cam copy");

            // a translation pack: the same author's folder, the mod's name and a language, and it wraps the menus
            Cli("add", Addon("Cam zh", "mods/actioncam/actioncam_zh_cn", "local m = rawget(_G, 'ModOptionsMenu')\nlocal old = m.register_option\n", A3));
            var lib = Library.Load();
            var pack = LuaMods.Of(lib).Single(x => x.Mod.Name == "Cam zh");
            Assert.Equal("Cam", pack.PackOf.Name);
            Assert.Empty(Now().Issues);
            Cli("disable", "Cam");
            Assert.Contains(Now().Issues, i => i.Kind == "pack_alone" && i.Mods[0].Name == "Cam zh");
            // a name that only looks like one, with no menus wrapped: not a pack
            Cli("add", Addon("Cam ru", "mods/actioncam/actioncam_ru", "", A3));
            Assert.Null(LuaMods.Of(Library.Load()).Single(x => x.Mod.Name == "Cam ru").PackOfName);

            // goto, and ffi without a fallback: the game's Lua cannot run them
            Cli("add", Addon("Old", "mods/old/one", "goto skip ::skip::\n", A2));
            Cli("add", Addon("Older", "mods/old/two", "local ffi = require('ffi')\n", A1));
            var broken = Now().Issues.Where(i => i.Kind == "cannot_run").Select(i => i.Mods.Single().Name).ToArray();
            Assert.Equal(new[] { "Old", "Older" }, broken);
            Assert.Contains("cannot run: mods/old/one line 2", Cli("lua"));         // line 1 is the header
        }

        [Fact]
        public void TheLastSessionAndTheLastCrash()
        {
            var folder = Path.Combine(Root, "Arrowhead");
            Directory.CreateDirectory(Path.Combine(folder, "dumps"));
            LuaMods.GameFolderOverride = folder;
            try
            {
                Assert.Null(LuaMods.LastSession());
                File.WriteAllText(Path.Combine(folder, "boot_mod_loader.log"),
                    "10:00:00 == boot-mod-loader-0.10.0 2026-10-24 10:00:00 (Lua 5.1)\n" +
                    "10:00:01 mods/me/old (patch_3, 10 bytes): loaded in 1 ms\n" +
                    "19:21:58 == boot-mod-loader-0.11.0 2026-10-25 19:21:58 (Lua 5.1)\n" +
                    "19:21:58 game boot: ran\n" +
                    "19:21:58 mods/me/lamp (patch_68, 240458 bytes): loaded in 12 ms\n" +
                    "19:21:58 mods/me/radio (patch_70): FAILED: [string \"mods/me/radio\"]:5: attempt to index nil\n" +
                    "stack traceback:\n\t[C]: in function 'error'\n" +
                    "19:21:58 mods/me/old (patch_71): does not compile on Lua 5.1: mods/me/old:9: malformed number near '1ULL'\n" +
                    "19:21:58 started 1 of 3 mods in 303 ms\n", new UTF8Encoding(false));
                var s = LuaMods.LastSession();
                Assert.Equal("boot-mod-loader-0.11.0", s.Loader);
                Assert.Equal(new DateTime(2026, 10, 25, 19, 21, 58), s.When);
                Assert.Equal(new[] { "loaded", "failed", "no_compile" }, Kinds(s));
                Assert.Equal(12, s.Mods[0].Ms);
                Assert.Contains("stack traceback", s.Mods[1].Why);
                Assert.Equal((1, 3), (s.StartedCount, s.Total));

                // a crash: the time it was written, not the one in its name (the game's start)
                Assert.Empty(LuaMods.Crashes());
                var dump = Path.Combine(folder, "dumps", "dump-2026-10-25-19.21.40-3d5d3f48-PC-1.dmp");
                File.WriteAllBytes(dump, new byte[] { 1 });
                var crashed = new DateTime(2026, 10, 25, 19, 43, 26);
                File.SetLastWriteTime(dump, crashed);
                Assert.Equal(crashed, LuaMods.Crashes().Single().When);
                // what was in the game then: the last deploy before it
                Assert.Null(LuaMods.DeployedAt(crashed));
                LuaMods.Record(new[] { "Lamp", "Lamp", "Radio" });
                Assert.Null(LuaMods.DeployedAt(crashed));                     // that deploy came after
                Assert.Equal(new[] { "Lamp", "Radio" }, LuaMods.DeployedAt(DateTime.Now.AddMinutes(1)).Value.Mods.ToArray());
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        [Fact]
        public void ADeployIsRecordedForLaterCrashes()
        {
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("deploy");
            var then = LuaMods.DeployedAt(DateTime.Now.AddSeconds(5));
            Assert.Equal(new[] { "Lamp" }, then.Value.Mods.ToArray());
        }
    }
}
