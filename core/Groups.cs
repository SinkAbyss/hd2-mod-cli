using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A group of mods as the player sorts them (the user, 2026-10-27): it looks like a
    /// folder in the window but is only a name in library.json. Putting a mod in one moves no file
    /// and changes no deploy, which is why the load order can stay out of sight.</summary>
    public class ModGroup
    {
        public string Id, Name;
        /// <summary>The group it is in, else null (the top).</summary>
        public string Parent;
    }

    /// <summary>The groups' rules: at most Depth levels, names unique among siblings, a group
    /// never inside itself; deleting one hands what it held to the group around it.</summary>
    public static class Groups
    {
        public const int Depth = 3;

        public static ModGroup Of(Library lib, string id) => id == null ? null : lib.Groups.FirstOrDefault(g => g.Id == id);

        /// <summary>The groups from the top down to this one.</summary>
        public static List<ModGroup> PathOf(Library lib, string id)
        {
            var path = new List<ModGroup>();
            for (var g = Of(lib, id); g != null && path.Count <= lib.Groups.Count; g = Of(lib, g.Parent)) path.Insert(0, g);
            return path;
        }

        public static string PathText(Library lib, string id) => string.Join("/", PathOf(lib, id).Select(g => g.Name));

        /// <summary>How deep a group sits: 1 at the top.</summary>
        public static int Level(Library lib, string id) => PathOf(lib, id).Count;

        /// <summary>How many levels a group holds below itself: 0 with no groups in it.</summary>
        public static int Below(Library lib, string id) =>
            lib.Groups.Where(g => g.Parent == id).Select(g => 1 + Below(lib, g.Id)).DefaultIfEmpty(0).Max();

        public static bool Inside(Library lib, string id, string ancestor)
        {
            foreach (var g in PathOf(lib, id)) if (g.Id == ancestor) return true;
            return false;
        }

        /// <summary>A group by its id, its path ("外观/护甲"), or its name when only one group has it.</summary>
        public static ModGroup Pick(Library lib, string reference)
        {
            var r = (reference ?? "").Trim().Trim('/');
            if (r.Length == 0) throw new ModError("a group needs a name", "no_name");
            var byId = Of(lib, r);
            if (byId != null) return byId;
            var byPath = lib.Groups.Where(g => string.Equals(PathText(lib, g.Id), r, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (byPath.Count == 1) return byPath[0];
            var byName = lib.Groups.Where(g => string.Equals(g.Name, r, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count == 0) throw new ModError("no group " + Ops.Repr(r), "no_group", r);
            throw new ModError(Ops.Repr(r) + " names " + byName.Count + " groups: " + string.Join("; ", byName.Select(g => PathText(lib, g.Id))) +
                               " (give the path, such as " + PathText(lib, byName[0].Id) + ")", "group_ambiguous", r);
        }

        static void NameFree(Library lib, string parent, string name, string except)
        {
            if (lib.Groups.Any(g => g.Parent == parent && g.Id != except && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                throw new ModError("there is a group " + Ops.Repr(name) + " there already", "group_exists", name);
        }

        public static ModGroup New(Library lib, string name, string parent)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ModError("a group needs a name", "no_name");
            if (parent != null && Level(lib, parent) >= Depth)
                throw new ModError("groups go " + Depth + " levels deep at most", "group_depth");
            NameFree(lib, parent, name, null);
            var g = new ModGroup { Id = Guid.NewGuid().ToString("N").Substring(0, 12), Name = name, Parent = parent };
            lib.Groups.Add(g);
            return g;
        }

        public static void Rename(Library lib, ModGroup g, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ModError("a group needs a name", "no_name");
            NameFree(lib, g.Parent, name, g.Id);
            g.Name = name;
        }

        /// <summary>A group into another (null: the top), with everything in it.</summary>
        public static void Move(Library lib, ModGroup g, string into)
        {
            if (into == g.Id || (into != null && Inside(lib, into, g.Id)))
                throw new ModError("a group cannot go inside itself", "group_inside");
            if ((into == null ? 0 : Level(lib, into)) + 1 + Below(lib, g.Id) > Depth)
                throw new ModError("groups go " + Depth + " levels deep at most", "group_depth");
            NameFree(lib, into, g.Name, g.Id);
            g.Parent = into;
        }

        /// <summary>The group goes; its mods and groups go up into the group around it. A group
        /// of the same name already there takes in the one coming up.</summary>
        public static void Delete(Library lib, ModGroup g)
        {
            foreach (var m in lib.Mods.Where(m => m.Group == g.Id)) m.Group = g.Parent;
            foreach (var child in lib.Groups.Where(x => x.Parent == g.Id).ToList())
            {
                var same = lib.Groups.FirstOrDefault(x => x.Parent == g.Parent && x.Id != g.Id && string.Equals(x.Name, child.Name, StringComparison.CurrentCultureIgnoreCase));
                if (same == null) child.Parent = g.Parent;
                else
                {
                    foreach (var m in lib.Mods.Where(m => m.Group == child.Id)) m.Group = same.Id;
                    foreach (var x in lib.Groups.Where(x => x.Parent == child.Id)) x.Parent = same.Id;
                    lib.Groups.Remove(child);
                }
            }
            lib.Groups.Remove(g);
        }

        /// <summary>After a load: no group under one that does not exist, none inside itself, none
        /// deeper than Depth, no mod in a group that does not exist. What breaks a rule goes to the top.</summary>
        public static void Tidy(Library lib)
        {
            var ids = new HashSet<string>();
            lib.Groups.RemoveAll(g => string.IsNullOrEmpty(g.Id) || string.IsNullOrWhiteSpace(g.Name) || !ids.Add(g.Id));
            foreach (var g in lib.Groups)
            {
                if (g.Parent != null && !ids.Contains(g.Parent)) g.Parent = null;
                var seen = new HashSet<string> { g.Id };
                for (var p = Of(lib, g.Parent); p != null; p = Of(lib, p.Parent))
                    if (!seen.Add(p.Id)) { g.Parent = null; break; }
            }
            foreach (var g in lib.Groups)
                if (Level(lib, g.Id) > Depth) g.Parent = null;
            foreach (var m in lib.Mods)
                if (m.Group != null && !ids.Contains(m.Group)) m.Group = null;
        }
    }
}
