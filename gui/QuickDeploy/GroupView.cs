using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The mods as Finder's column view of the player's groups (the user, 2026-10-27):
    /// one column per level, up to Groups.Depth deep, each holding the groups and the mods inside
    /// the group chosen in the column before it. A group looks like a folder but is only a name
    /// in the library, so sorting mods moves no file and changes no deploy; the load order stays
    /// one click away (the other view).</summary>
    public partial class MainWindow
    {
        // the groups shown, else the load order; kept in gui.json
        bool groupView = true;
        // the groups opened, one per column after the first
        readonly List<string> groupPath = new List<string>();
        // the column the arrow keys work in
        int groupCol;
        // a group just made, opened once the state that has it arrives
        (string Parent, string Name)? openWhenMade;
        const double GroupColWidth = 250;

        // a top group's colour, its groups inside wear it too
        static readonly Color[] GroupColours =
        {
            Color.FromRgb(0xFF, 0xD2, 0x3F), Color.FromRgb(0x5A, 0xAA, 0xFF), Color.FromRgb(0x4C, 0xC3, 0x8A),
            Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xF2, 0xA6, 0x5A), Color.FromRgb(0x4F, 0xD1, 0xC5),
        };

        /// <summary>Where a dragged mod or group would go: a group, or the top (null).</summary>
        class GroupDrop
        {
            public string Group;
        }

        void LoadView()
        {
            try { groupView = Json.Str(Store.Load("gui.json"), "list_view") != "order"; }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        void SaveView()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["list_view"] = groupView ? "groups" : "order";
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        void View_Click(object sender, RoutedEventArgs e)
        {
            bool want = sender == ViewGroups;
            if (want == groupView) { ShowView(); return; }
            groupView = want;
            SaveView();
            if (groupView) RevealSelected();
            else if (selectedId == null) selectedId = Seen().FirstOrDefault()?.Id;
            RenderList();
            RenderDetail();
        }

        /// <summary>The switch and which of the two is on show.</summary>
        void ShowView()
        {
            ViewGroups.IsChecked = groupView;
            ViewOrder.IsChecked = !groupView;
            ListScroll.Visibility = groupView ? Visibility.Collapsed : Visibility.Visible;
            GroupScroll.Visibility = groupView ? Visibility.Visible : Visibility.Collapsed;
            // the jump buttons are the load order's
            TopBtn.Visibility = BottomBtn.Visibility = groupView ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The groups of the mod chosen in the load order, opened.</summary>
        void RevealSelected()
        {
            var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
            groupPath.Clear();
            if (m?.Group != null) groupPath.AddRange(PathTo(m.Group).Select(g => g.Id));
            groupCol = groupPath.Count;
        }

        GroupRow GroupOf(string id) => id == null ? null : st.Groups.FirstOrDefault(g => g.Id == id);

        List<GroupRow> PathTo(string id)
        {
            var path = new List<GroupRow>();
            for (var g = GroupOf(id); g != null && path.Count <= st.Groups.Count; g = GroupOf(g.Parent)) path.Insert(0, g);
            return path;
        }

        int LevelOf(string id) => PathTo(id).Count;

        Color ColourOf(string id)
        {
            var top = PathTo(id).FirstOrDefault();
            int i = top == null ? 0 : st.Groups.Where(g => g.Parent == null).ToList().FindIndex(g => g.Id == top.Id);
            return GroupColours[Math.Max(0, i) % GroupColours.Length];
        }

        /// <summary>The mods shown (the filters and the search hold here too), translation packs
        /// left to the mod they translate, as in the load order.</summary>
        List<ModRow> GroupShown()
        {
            var shown = st.Mods.Where(Shown).ToList();
            var ids = new HashSet<string>(shown.Select(x => x.Id));
            return shown.Where(x => x.Lu?.PackOf == null || !ids.Contains(x.Lu.PackOf)).ToList();
        }

        // HD2QD_OPEN_GROUP=Looks/Armour (pictures only): that group opened and chosen at the start
        string openAtStart = Environment.GetEnvironmentVariable("HD2QD_OPEN_GROUP");

        void RenderGroups()
        {
            if (openAtStart != null && st.Groups.Count > 0)
            {
                groupPath.Clear();
                string parent = null;
                foreach (var name in openAtStart.Split('/'))
                {
                    var g = st.Groups.FirstOrDefault(x => x.Parent == parent && x.Name == name);
                    if (g == null) break;
                    groupPath.Add(g.Id);
                    parent = g.Id;
                }
                selectedId = null;
                groupCol = Math.Max(0, groupPath.Count - 1);
                openAtStart = null;
                RenderDetail();
            }
            var shown = GroupShown();
            // narrowed: a filter or the search is on, so a group shows only while it holds a mod that passes
            bool narrowed = shown.Count < st.Mods.Count(m => InScope(m) && (m.Lu?.PackOf == null || !st.Mods.Any(x => x.Id == m.Lu.PackOf && InScope(x))));
            // every group's mods, its groups inside counted
            var under = st.Groups.ToDictionary(g => g.Id, g => new List<ModRow>());
            foreach (var m in shown)
                foreach (var g in PathTo(m.Group)) under[g.Id].Add(m);
            // a group hides only when it has mods and the filter passes none of them: an empty one,
            // a group just made among them, always shows (a remembered filter such as labels left
            // out hid them all, the user, 2026-10-27)
            var holding = new HashSet<string>(st.Mods.Where(InScope).SelectMany(m => PathTo(m.Group)).Select(g => g.Id));
            bool Visible(GroupRow g) => !narrowed || under[g.Id].Count > 0 || !holding.Contains(g.Id);

            if (openWhenMade != null)
            {
                var made = st.Groups.FirstOrDefault(g => g.Parent == openWhenMade.Value.Parent && g.Name == openWhenMade.Value.Name);
                if (made != null)
                {
                    int at = made.Parent == null ? 0 : groupPath.IndexOf(made.Parent) + 1;
                    if (made.Parent == null || at > 0)
                    {
                        groupPath.RemoveRange(at, groupPath.Count - at);
                        groupPath.Add(made.Id);
                        selectedId = null;
                        groupCol = at;
                    }
                    openWhenMade = null;
                }
            }
            // the path holds while each group in it is still there, inside the one before, and shown
            for (int i = 0; i < groupPath.Count; i++)
            {
                var g = GroupOf(groupPath[i]);
                if (g == null || g.Parent != (i == 0 ? null : groupPath[i - 1]) || !Visible(g)) { groupPath.RemoveRange(i, groupPath.Count - i); break; }
            }
            groupCol = Math.Min(groupCol, groupPath.Count);

            GroupCols.Children.Clear();
            GroupCols.ColumnDefinitions.Clear();
            var parents = new List<string> { null };
            parents.AddRange(groupPath);
            for (int i = 0; i < parents.Count; i++)
            {
                bool last = i == parents.Count - 1;
                GroupCols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var col = GroupColumn(i, parents[i], shown, under, Visible, narrowed, last);
                Grid.SetColumn(col, i);
                GroupCols.Children.Add(col);
                if (last) lastGroupCol = col;
            }
            FitLastColumn();
            // the newest column in view, as Finder scrolls to it
            Dispatcher.BeginInvoke(new Action(() => GroupScroll.ScrollToRightEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        FrameworkElement lastGroupCol;

        /// <summary>The last column takes the room the others leave, never less than one column:
        /// a width of its own, so a long line in it cannot push the first column out of view.</summary>
        void FitLastColumn()
        {
            if (lastGroupCol == null) return;
            int n = GroupCols.ColumnDefinitions.Count;
            lastGroupCol.Width = Math.Max(GroupColWidth, Math.Floor(GroupScroll.ViewportWidth) - (n - 1) * GroupColWidth);
        }

        FrameworkElement GroupColumn(int index, string parent, List<ModRow> shown, Dictionary<string, List<ModRow>> under,
                                     Func<GroupRow, bool> visible, bool narrowed, bool last)
        {
            bool active = index == groupCol;
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
            var col = new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, last ? 0 : 1, 0), Background = Brushes.Transparent,
                Tag = new GroupDrop { Group = parent },
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = list },
            };
            col.Width = GroupColWidth;
            col.PreviewMouseDown += (s, e) => groupCol = index;
            // a click on the empty part of a column opens nothing further: what it holds stays
            col.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled || gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                selectedId = null;
                groupCol = index;
                RenderGroups();
                RenderDetail();
            };
            col.ContextMenu = ColumnMenu(parent);

            var groups = st.Groups.Where(g => g.Parent == parent && visible(g)).ToList();
            var mods = shown.Where(m => m.Group == parent).ToList();
            if (groups.Count == 0 && mods.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = Texts.Get(narrowed ? "g_none" : "g_empty"), FontSize = 13, Foreground = B("Muted"),
                    Margin = new Thickness(14, 8, 14, 8), TextWrapping = TextWrapping.Wrap,
                });
            foreach (var g in groups) list.Children.Add(FolderItem(g, index, active, under[g.Id]));
            foreach (var m in mods) list.Children.Add(ModItem(m, index, active));
            if (parent == null || LevelOf(parent) < Groups.Depth)
            {
                var add = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Left };
                add.Content = new TextBlock { Text = Texts.Get("g_new"), FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(14, 10, 14, 10) };
                add.Click += (s, e) => NewGroup(parent);
                list.Children.Add(add);
            }
            return col;
        }

        /// <summary>The look of a chosen item: yellow in the column the keys work in, grey on the
        /// path behind it (Finder: blue and grey).</summary>
        static readonly Brush ChosenHere = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xD2, 0x3F));

        Border ItemShell(bool chosen, bool active, object tag)
        {
            var edge = new Border
            {
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = chosen ? B(active ? "Yellow" : "Muted") : Brushes.Transparent,
            };
            var item = new Border { Tag = tag, Background = chosen ? (active ? ChosenHere : B("Ground900")) : Brushes.Transparent, Child = edge, Cursor = Cursors.Hand };
            item.MouseEnter += (s, e) => { if (!chosen) item.Background = B("Band"); };
            item.MouseLeave += (s, e) => { if (!chosen) item.Background = Brushes.Transparent; };
            return item;
        }

        /// <summary>A folder in the group's colour: its tab, and its front over it.</summary>
        static FrameworkElement Folder(Color colour, double width)
        {
            var c = new Canvas { Width = 40, Height = 34 };
            c.Children.Add(new Path { Data = Geometry.Parse("M2,6 A3,3 0 0 1 5,3 H15 L19,7 H35 A3,3 0 0 1 38,10 V29 A3,3 0 0 1 35,32 H5 A3,3 0 0 1 2,29 Z"),
                                      Fill = new SolidColorBrush(colour) { Opacity = 0.55 } });
            c.Children.Add(new Path { Data = Geometry.Parse("M2,11 H38 V29 A3,3 0 0 1 35,32 H5 A3,3 0 0 1 2,29 Z"), Fill = new SolidColorBrush(colour) });
            return new Viewbox { Width = width, Height = width * 34 / 40, Child = c, VerticalAlignment = VerticalAlignment.Center };
        }

        static Path Chevron() => new Path
        {
            Data = Geometry.Parse("M0,0 L5,5 L0,10"), Stroke = B("Muted"), StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };

        Border FolderItem(GroupRow g, int index, bool active, List<ModRow> mods)
        {
            bool chosen = index < groupPath.Count && groupPath[index] == g.Id;
            var item = ItemShell(chosen, active, new GroupDrop { Group = g.Id });
            var line = new DockPanel { MinHeight = 52, Margin = new Thickness(11, 6, 10, 6) };
            var pic = Folder(ColourOf(g.Id), 40);
            DockPanel.SetDock(pic, Dock.Left);
            line.Children.Add(pic);
            var chev = Chevron();
            DockPanel.SetDock(chev, Dock.Right);
            line.Children.Add(chev);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            names.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Name });
            var meta = new WrapPanel();
            int on = mods.Count(m => m.Enabled);
            meta.Children.Add(new TextBlock
            {
                Text = on < mods.Count ? Texts.F("g_mods_off", mods.Count, on) : Texts.F("g_mods", mods.Count),
                FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            if (mods.Any(m => m.Enabled && m.Conflicts > 0)) meta.Children.Add(ClashTag(null));
            names.Children.Add(meta);
            line.Children.Add(names);
            ((Border)item.Child).Child = line;
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                groupPath.Add(g.Id);
                selectedId = null;
                groupCol = index;
                RenderGroups();
                RenderDetail();
            };
            item.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameGroup(g); return; }
                gDragFrom = ("group", g.Id, g.Name, e.GetPosition(GroupScroll));
            };
            item.ContextMenu = FolderMenu(g);
            return item;
        }

        Border ClashTag(string tip) => new Border
        {
            BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            Child = new TextBlock { Text = Texts.Get("g_clash"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Yellow") },
        };

        Border ModItem(ModRow m, int index, bool active)
        {
            bool chosen = m.Id == selectedId && index == groupPath.Count;
            var item = ItemShell(chosen, active, null);
            var line = new DockPanel { MinHeight = 52, Margin = new Thickness(11, 6, 10, 6) };
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = m.Name };
            sw.Click += (s, e) =>
            {
                bool on = sw.IsChecked == true;
                if (on && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                Change(m.Id, c => c.Enabled = on, on ? "enable" : "disable", m.Id);
            };
            if (st.ProfilesOn && !m.InProfile) { item.Opacity = 0.55; sw.ToolTip = Texts.Get("pf_outside"); }
            DockPanel.SetDock(sw, Dock.Left);
            line.Children.Add(sw);
            var pic = Picture(m.Icon, GlyphMod, 40, m.Enabled ? "Blue" : "Muted", 24);
            pic.Opacity = m.Enabled ? 1 : 0.55;
            DockPanel.SetDock(pic, Dock.Left);
            line.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = m.Enabled ? 1 : 0.55 };
            names.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Name });
            var meta = new WrapPanel();
            if (m.Options.Count > 0)
                meta.Children.Add(new TextBlock { Text = Texts.F("opts_some", m.Chosen, m.Choices), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var t in m.Tags)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Ink"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("tag_tip"),
                    Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Ink") },
                });
            if (m.Lua)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("lua_tip"),
                    Child = new TextBlock { Text = "LUA", FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Blue") },
                });
            if (m.Enabled && m.Conflicts > 0) meta.Children.Add(ClashTag(Texts.F("row_clash", m.Conflicts)));
            if (m.Error != null) meta.Children.Add(new TextBlock { Text = Texts.Get("broken"), FontSize = 12, Foreground = B("Yellow"), Margin = new Thickness(0, 2, 0, 0) });
            if (meta.Children.Count > 0) names.Children.Add(meta);
            line.Children.Add(names);
            ((Border)item.Child).Child = line;
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                groupCol = index;
                Take(m);
                RenderGroups();
            };
            item.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameMod(m); return; }
                gDragFrom = ("mod", m.Id, m.Name, e.GetPosition(GroupScroll));
            };
            var menu = Menu(m);
            if (m.Group != null)
            {
                var outItem = new MenuItem { Header = Texts.Get("g_out") };
                outItem.Click += (s, e) => Run(new[] { "group-put" }.Concat(Taken(m).Select(x => x.Id)).Concat(new[] { "--top" }).ToArray());
                menu.Items.Insert(0, outItem);
                menu.Items.Insert(1, new Separator());
            }
            item.ContextMenu = menu;
            return item;
        }

        ContextMenu ColumnMenu(string parent)
        {
            var menu = new ContextMenu();
            if (parent == null || LevelOf(parent) < Groups.Depth)
            {
                var it = new MenuItem { Header = Texts.Get(parent == null ? "g_new_top" : "g_new_in") };
                it.Click += (s, e) => NewGroup(parent);
                menu.Items.Add(it);
            }
            return menu.Items.Count > 0 ? menu : null;
        }

        ContextMenu FolderMenu(GroupRow g)
        {
            var menu = new ContextMenu();
            if (LevelOf(g.Id) < Groups.Depth)
            {
                var inner = new MenuItem { Header = Texts.Get("g_new_in") };
                inner.Click += (s, e) => NewGroup(g.Id);
                menu.Items.Add(inner);
            }
            var rename = new MenuItem { Header = Texts.Get("g_rename") };
            rename.Click += (s, e) => RenameGroup(g);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = Texts.Get("g_delete"), Foreground = B("Red") };
            del.Click += (s, e) => DeleteGroup(g);
            menu.Items.Add(del);
            return menu;
        }

        void NewGroup(string parent)
        {
            var name = Dialog.AskText(this, Texts.Get("g_rename_ask"), Texts.Get("g_create"), Texts.Get("g_new_name"));
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            openWhenMade = (parent, name);
            Run(parent == null ? new[] { "group-new", name } : new[] { "group-new", name, "--in", parent });
        }

        void RenameGroup(GroupRow g)
        {
            var name = Dialog.AskText(this, Texts.Get("g_rename_ask"), Texts.Get("rename_save"), g.Name);
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name == g.Name) return;
            Run("group-rename", g.Id, name);
        }

        void DeleteGroup(GroupRow g)
        {
            if (!Dialog.Ask(this, Texts.F("g_delete_ask", g.Name), Texts.Get("g_delete"))) return;
            Run("group-delete", g.Id);
        }

        /// <summary>A group as the details show it: its folder, what it holds, where it is, and
        /// its mods turned on or off at once.</summary>
        void GroupDetail(GroupRow g)
        {
            var mods = st.Mods.Where(m => InScope(m) && m.Group != null && PathTo(m.Group).Any(x => x.Id == g.Id)).ToList();
            var head = new DockPanel();
            var pic = Folder(ColourOf(g.Id), 72);
            DockPanel.SetDock(pic, Dock.Left);
            head.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var title = Selectable.Text(g.Name, 22);
            title.FontWeight = FontWeights.Bold;
            names.Children.Add(title);
            int on = mods.Count(m => m.Enabled);
            names.Children.Add(new TextBlock { Text = Texts.F("g_mods_off", mods.Count, on), FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(0, 4, 0, 0) });
            head.Children.Add(names);
            Detail.Children.Add(head);

            var facts = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            facts.ColumnDefinitions.Add(new ColumnDefinition());
            void Fact(string key, string value)
            {
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var k = new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(k, r);
                facts.Children.Add(k);
                var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                facts.Children.Add(v);
            }
            Fact("g_where", g.Parent == null ? Texts.Get("g_top") : string.Join(" › ", PathTo(g.Parent).Select(x => x.Name)));
            Fact("g_subs", st.Groups.Count(x => x.Parent == g.Id).ToString());
            Detail.Children.Add(facts);

            var acts = new WrapPanel { Margin = new Thickness(-8, 12, 0, 0) };
            Button Act(string key, Action act, string style = "Ghost")
            {
                var b = new Button { Content = Texts.Get(key), Style = (Style)FindResource(style), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                b.Click += (s, e) => act();
                acts.Children.Add(b);
                return b;
            }
            Act("g_all_on", () => { foreach (var m in mods.Where(x => !x.Enabled)) Change(m.Id, c => c.Enabled = true, "enable", m.Id); }).IsEnabled = on < mods.Count;
            Act("g_all_off", () => { foreach (var m in mods.Where(x => x.Enabled)) Change(m.Id, c => c.Enabled = false, "disable", m.Id); }).IsEnabled = on > 0;
            Act("g_rename", () => RenameGroup(g));
            Act("g_delete", () => DeleteGroup(g), "Danger");
            Detail.Children.Add(acts);
            if (Texts.Hints) Detail.Children.Add(Small(Texts.Get("g_note"), new Thickness(0, 16, 0, 0)));
        }

        // ---------------------------------------------------------------- dragging into a group

        (string Kind, string Id, string Name, Point Start)? gDragFrom;
        bool gDragging;
        Border gTarget, gGhost;
        bool gHooked;

        /// <summary>The mouse on the columns: a press that moves becomes a drag of that mod (the
        /// mods taken with it) or group; let go over a folder or a column, it goes in there.
        /// Our own drag, as the load order's: WPF's DoDragDrop freezes the picture.</summary>
        void HookGroupDrag()
        {
            if (gHooked) return;
            gHooked = true;
            GroupScroll.PreviewMouseMove += (s, e) =>
            {
                if (gDragFrom == null) return;
                var at = e.GetPosition(GroupScroll);
                if (!gDragging)
                {
                    if (e.LeftButton != MouseButtonState.Pressed) { gDragFrom = null; return; }
                    if (Math.Abs(at.X - gDragFrom.Value.Start.X) < 6 && Math.Abs(at.Y - gDragFrom.Value.Start.Y) < 6) return;
                    gDragging = true;
                    GroupScroll.CaptureMouse();
                    gGhost = new Border
                    {
                        Background = B("Ground900"), BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
                        Child = new TextBlock { Text = DragLabel(), FontWeight = FontWeights.SemiBold, Foreground = B("Ink") },
                    };
                    DragLayer.Children.Add(gGhost);
                }
                var onLayer = e.GetPosition(DragLayer);
                Canvas.SetLeft(gGhost, onLayer.X + 14);
                Canvas.SetTop(gGhost, onLayer.Y + 10);
                Target(DropAt(at));
                e.Handled = true;
            };
            GroupScroll.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!gDragging) { gDragFrom = null; return; }
                var to = gTarget?.Tag as GroupDrop;
                var from = gDragFrom.Value;
                EndGroupDrag();
                e.Handled = true;
                if (to != null) DropInto(from.Kind, from.Id, to.Group);
            };
            GroupScroll.LostMouseCapture += (s, e) => { if (gDragging) EndGroupDrag(); };
            PreviewKeyDown += (s, e) => { if (gDragging && e.Key == Key.Escape) { EndGroupDrag(); e.Handled = true; } };
            PreviewKeyDown += GroupKeys;
            GroupScroll.SizeChanged += (s, e) => FitLastColumn();
        }

        string DragLabel()
        {
            var from = gDragFrom.Value;
            if (from.Kind == "group") return from.Name;
            var m = st.Mods.FirstOrDefault(x => x.Id == from.Id);
            var many = m != null ? Taken(m) : new List<ModRow>();
            return many.Count > 1 ? Texts.F("many_drag", many.Count) : from.Name;
        }

        /// <summary>The folder or column under a point, when what is dragged may go there.</summary>
        Border DropAt(Point at)
        {
            var hit = GroupScroll.InputHitTest(at) as DependencyObject;
            for (var d = hit; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is Border b && b.Tag is GroupDrop drop) return Allowed(drop.Group) ? b : null;
            return null;
        }

        bool Allowed(string into)
        {
            var from = gDragFrom.Value;
            if (from.Kind == "mod") return st.Mods.FirstOrDefault(m => m.Id == from.Id)?.Group != into;
            if (into == from.Id || (into != null && PathTo(into).Any(g => g.Id == from.Id))) return false;
            if (GroupOf(from.Id)?.Parent == into) return false;
            int below(string id) => st.Groups.Where(g => g.Parent == id).Select(g => 1 + below(g.Id)).DefaultIfEmpty(0).Max();
            return (into == null ? 0 : LevelOf(into)) + 1 + below(from.Id) <= Groups.Depth;
        }

        void Target(Border b)
        {
            if (gTarget == b) return;
            if (gTarget != null) { gTarget.BorderBrush = gTarget.Tag is GroupDrop && gTarget.Child is ScrollViewer ? B("Grid") : Brushes.Transparent; gTarget.BorderThickness = TargetRest(gTarget); }
            gTarget = b;
            if (b != null) { b.BorderBrush = B("Yellow"); b.BorderThickness = new Thickness(1); }
        }

        Thickness TargetRest(Border b) =>
            b.Child is ScrollViewer ? new Thickness(0, 0, Grid.GetColumn(b) == GroupCols.ColumnDefinitions.Count - 1 ? 0 : 1, 0) : new Thickness(0);

        void EndGroupDrag()
        {
            Target(null);
            if (gGhost != null) DragLayer.Children.Remove(gGhost);
            gGhost = null;
            gDragging = false;
            gDragFrom = null;
            if (GroupScroll.IsMouseCaptured) GroupScroll.ReleaseMouseCapture();
        }

        void DropInto(string kind, string id, string group)
        {
            var where = group == null ? new[] { "--top" } : new[] { "--in", group };
            if (kind == "group") { Run(new[] { "group-move", id }.Concat(where).ToArray()); return; }
            var m = st.Mods.FirstOrDefault(x => x.Id == id);
            var ids = m != null ? Taken(m).Select(x => x.Id) : new[] { id };
            Run(new[] { "group-put" }.Concat(ids).Concat(where).ToArray());
        }

        // ---------------------------------------------------------------- the arrow keys, as Finder's

        void GroupKeys(object sender, KeyEventArgs e)
        {
            if (!groupView || e.Handled || gDragging || Body.Visibility != Visibility.Visible || st == null) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
            if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Left && e.Key != Key.Right) return;
            var shown = GroupShown();
            List<(bool Group, string Id)> Items(string parent)
            {
                var narrowed = shown.Count < st.Mods.Count(InScope);
                bool Holds(IEnumerable<ModRow> ms, string id) => ms.Any(m => m.Group != null && PathTo(m.Group).Any(x => x.Id == id));
                var gs = st.Groups.Where(g => g.Parent == parent && (!narrowed || Holds(shown, g.Id) || !Holds(st.Mods.Where(InScope), g.Id)));
                return gs.Select(g => (true, g.Id)).Concat(shown.Where(m => m.Group == parent).Select(m => (false, m.Id))).ToList();
            }
            int i = Math.Min(groupCol, groupPath.Count);
            var items = Items(i == 0 ? null : groupPath[i - 1]);
            int cur = items.FindIndex(x => x.Group ? i < groupPath.Count && groupPath[i] == x.Id : i == groupPath.Count && x.Id == selectedId);
            void PickAt(int j)
            {
                if (j < 0 || j >= items.Count) return;
                groupPath.RemoveRange(i, groupPath.Count - i);
                if (items[j].Group) { groupPath.Add(items[j].Id); selectedId = null; }
                else selectedId = items[j].Id;
            }
            if (e.Key == Key.Down) PickAt(cur < 0 ? 0 : Math.Min(items.Count - 1, cur + 1));
            else if (e.Key == Key.Up) PickAt(cur < 0 ? 0 : Math.Max(0, cur - 1));
            else if (e.Key == Key.Right)
            {
                if (i < groupPath.Count)
                {
                    var inner = Items(groupPath[i]);
                    if (inner.Count > 0) { groupCol = i + 1; i = groupCol; items = inner; PickAt(0); }
                }
            }
            else if (e.Key == Key.Left && i > 0)
            {
                groupCol = i - 1;
                groupPath.RemoveRange(i, groupPath.Count - i);
                selectedId = null;
            }
            e.Handled = true;
            RenderGroups();
            RenderDetail();
        }
    }
}
