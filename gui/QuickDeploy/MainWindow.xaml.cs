using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hd2Mod;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;

namespace QuickDeploy
{
    /// <summary>The design board's Manager (gui\design\Manager.dc.html), in HD2 Mod
    /// Setting GUI's colours: everything shown comes from `hd2qd state`, every
    /// change is an hd2qd command, run in this process.</summary>
    public partial class MainWindow : Window
    {
        State st;
        string selectedId, filter = "all";
        readonly HashSet<string> open = new HashSet<string>();
        // more than one mod taken at once, with Ctrl or Shift (the user, 2026-10-24); the one
        // the details show is selectedId, which is also where a Shift range starts
        readonly HashSet<string> picked = new HashSet<string>();
        bool busy, overMax, acrylic, descOpen, checking;
        string lastDone;
        // the last update check, by mod id (kept while the window is open)
        readonly Dictionary<string, UpdateInfo> updates = new Dictionary<string, UpdateInfo>();
        string startWith = Arg("--select");
        readonly string startSearch = Arg("--search");
        string startPage = Arg("--page");

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
        FileSystemWatcher watcher;
        readonly DispatcherTimer settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        DateTime quietUntil = DateTime.MinValue;
        Point dragStart;
        ModRow dragFrom;
        Border dropRow;

        // a line drawing for a mod or an option without a picture (the board's sketch icons)
        const string GlyphMod = "M3 5h18v14H3z M7 9h10 M7 13h6";
        // the row's update mark, in its three states: check this mod (an arrow coming round
        // again), a newer version to install (down), nothing to do (a tick)
        const string GlyphCheck = "M20.5 12a8.5 8.5 0 1 1-2.6-6.1 M20.5 3.5v4.2h-4.2";
        const string GlyphDown = "M12 3.5 V18.5 M5.5 12 L12 18.5 L18.5 12";
        const string GlyphTick = "M4.5 12.5 L9.75 18 L19.5 6.5";
        const string GlyphPart = "M12 3v11 M7 9l5 5 5-5 M4 20h16";
        const string GlyphTop = "M5 4h14 M12 20V9 M7 14l5-5 5 5";
        const string GlyphBottom = "M5 20h14 M12 4v11 M7 10l5 5 5-5";

        /// <summary>Whether this build has the download page.</summary>
        const bool HasNexus = false;

        static Brush B(string key) => (Brush)Application.Current.Resources[key];
        FontFamily Mono => (FontFamily)FindResource("Mono");

        public MainWindow()
        {
            InitializeComponent();
            Words();
            if (startSearch != null) SearchBox.Text = startSearch;
            StateChanged += (s, e) => FitMaximized();
            PreviewMouseWheel += Wheel;
            settle.Tick += (s, e) =>
            {
                if (dragRow != null) return;            // a row in hand: the list stays as it is
                if (busy || DateTime.Now <= quietUntil) return;   // not now: the timer tries again
                settle.Stop();
                Refresh();
            };
            // a row dragged to another place follows the pointer until the button is let go
            ListArea.PreviewMouseMove += (s, e) => { if (dragRow != null) { DragAt(e.GetPosition(ListArea)); e.Handled = true; } };
            ListArea.PreviewMouseLeftButtonUp += (s, e) => { if (dragRow != null) { EndDrag(true); e.Handled = true; } };
            // the switch under the pointer loses the mouse to the list when a drag starts, and
            // that bubbles here too: only the list's own loss ends the drag
            ListArea.LostMouseCapture += (s, e) => { if (!ReferenceEquals(Mouse.Captured, ListArea)) EndDrag(false); };
            PreviewKeyDown += (s, e) => { if (dragRow != null && e.Key == Key.Escape) { EndDrag(false); e.Handled = true; } };
            edgeScroll.Tick += (s, e) =>
            {
                if (dragRow == null) { edgeScroll.Stop(); return; }
                ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + edgeBy);
                DragAt(Mouse.GetPosition(ListArea));
            };
            Loaded += (s, e) => Start();
        }

        /// <summary>The window's fixed words, in its language.</summary>
        void Words()
        {
            TitleText.Text = Texts.Get("title");
            StatLinksLabel.Text = Texts.Get("stat_links");
            AddBtn.Content = Texts.Get("add");
            UpdatesBtn.Content = Texts.Get(checking ? "up_checking_btn" : "up_check");
            RefreshBtn.Content = Texts.Get("refresh");
            JumpButtons();
            DeployBtn.Content = Texts.Get("deploy");
            LaunchBtn.Content = Texts.Get("launch");
            ListHint.Text = Texts.Get(Texts.TopWins ? "list_hint_top" : "list_hint");
            PageDeploy.Content = Texts.Get("page_deploy");
            PageDownload.Content = Texts.Get("page_download");
            PageStorage.Content = Texts.Get("page_storage");
            PageTags.Content = Texts.Get("page_tags");
            PageDownload.Visibility = HasNexus ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Text = Texts.Get("search_hint");
            SettingsBtn.ToolTip = Texts.Get("settings");
            FilterWords();
            ListHint.Visibility = Texts.Hints ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The whole window in the other language at once, kept for the next start.</summary>
        void Language(bool zh)
        {
            if (zh == Texts.Zh) { Words(); return; }
            Texts.Choose(zh);
            Words();
            rows.Clear();                       // every row's words, built again
            List.Children.Clear();
            lastDone = null;
            if (st != null) Render();
            if (StoragePage.Visibility == Visibility.Visible) RenderStorage();
        }

        // ---------------------------------------------------------------- start, state, commands

        void Start()
        {
            Watch();
            Refresh();
            ClickTest();
            DragTest();
            Listen();
            SelfUpdateAtStart();
        }

        /// <summary>The red dot on the settings icon, from the last check of this program's own
        /// releases: shown while that version is newer than the one running.</summary>
        void ShowUpdateDot()
        {
            var u = SelfUpdate.Last();
            UpdateDot.Visibility = u.Newer ? Visibility.Visible : Visibility.Collapsed;
            SettingsBtn.ToolTip = u.Newer ? Texts.F("self_tip", u.Latest) : null;
        }

        /// <summary>The last result at once, then, when the automatic check is on and due (once a
        /// day), GitHub asked in the background. A check that fails says nothing here: the next
        /// start tries again, and Settings says why.</summary>
        async void SelfUpdateAtStart()
        {
            ShowUpdateDot();
            if (!SelfUpdate.Due()) return;
            try { await Task.Run(() => SelfUpdate.Check()); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException || e is TaskCanceledException) { return; }
            ShowUpdateDot();
        }

        // the switch of each row on screen, for the click test
        readonly Dictionary<string, CheckBox> switches = new Dictionary<string, CheckBox>();

        /// <summary>HD2QD_CLICK_TEST=N (tests only): 3 s after the start, the switch of mod
        /// N is clicked the way a click does it, so a test outside can time what follows.</summary>
        void ClickTest()
        {
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "storage")
            {
                // "storage": the mod folders with a couple of mods ticked, for their picture
                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t2.Tick += (s, e) =>
                {
                    t2.Stop();
                    ShowPage("storage");
                    foreach (var m in (st?.Mods ?? new List<ModRow>()).Take(2)) ticked.Add(m.Id);
                    RenderStorage();
                };
                t2.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "old")
            {
                // "old": the storage page scrolled to the folders of old versions, two ticked
                var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t3.Tick += (s, e) =>
                {
                    t3.Stop();
                    ShowPage("storage");
                    foreach (var l in (st?.Leftovers ?? new List<Leftover>()).Take(2)) oldPick.Add(l.Path);
                    RenderStorage();
                    Dispatcher.BeginInvoke(new Action(() => StoragePage.ScrollToEnd()), DispatcherPriority.Loaded);
                };
                t3.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "sync")
            {
                // "sync": writes what the window is showing, once a second, so a test outside can
                // watch a change made on the command line arrive here
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t8 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                t8.Tick += (s, e) =>
                {
                    if (path == null || st == null) return;
                    try
                    {
                        File.WriteAllText(path, "mods=" + st.Mods.Count +
                                                "\r\non=" + st.Mods.Count(m => m.Enabled) +
                                                "\r\nfirst=" + (st.Mods.Count > 0 ? st.Mods[0].Name + "|" + st.Mods[0].Enabled : "-") +
                                                "\r\nhome=" + Store.Home + "\r\n");
                    }
                    catch (IOException) { }
                };
                t8.Start();
                // and at twelve seconds it turns the first mod the other way, the way a click
                // does, so the test can look for that in the library file afterwards
                var t9 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                t9.Tick += (s, e) =>
                {
                    t9.Stop();
                    var m = st?.Mods.FirstOrDefault();
                    if (m == null) return;
                    Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id);
                };
                t9.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "where")
            {
                // "where": writes down where the list came back to, 5 s after the start
                var t7 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t7.Tick += (s, e) =>
                {
                    t7.Stop();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path == null) return;
                    try
                    {
                        File.WriteAllText(path, "offset=" + ListScroll.VerticalOffset +
                                                "\r\nheight=" + ListScroll.ScrollableHeight + "\r\n");
                    }
                    catch (IOException) { }
                    ListScroll.ScrollToVerticalOffset(2600);   // a different place, to see it saved
                };
                t7.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "wheel")
            {
                // "wheel": the tag filter menu opened and left open. A test outside turns the
                // wheel over the list; this writes down afterwards whether the menu closed and
                // where the list scrolled to.
                var t5 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t5.Tick += (s, e) =>
                {
                    t5.Stop();
                    PreviewMouseWheel += (s3, e3) => wheelSeen++;
                    // HD2QD_WHEEL_NOMENU: the same test without a menu at all, as a control
                    if (Environment.GetEnvironmentVariable("HD2QD_WHEEL_NOMENU") == null)
                        TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    var t55 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t55.Tick += (s4, e4) =>
                    {
                        t55.Stop();
                        var root = lastMenu == null ? null : PresentationSource.FromVisual(lastMenu) as HwndSource;
                        if (root?.RootVisual is UIElement rv)
                            rv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                            {
                                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                            });
                    };
                    t55.Start();
                    var t6 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    t6.Tick += (s2, e2) =>
                    {
                        t6.Stop();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path == null) return;
                        try
                        {
                            File.WriteAllText(path, "menu_open=" + (lastMenu != null && lastMenu.IsOpen) +
                                                    "\r\noffset=" + ListScroll.VerticalOffset +
                                                    "\r\nheight=" + ListScroll.ScrollableHeight +
                                                    "\r\nwheel_seen=" + wheelSeen +
                                                    "\r\nfocus_is_mine=" + (GetFocus() == new System.Windows.Interop.WindowInteropHelper(this).Handle) +
                                                    "\r\nforeground_is_mine=" + (GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(this).Handle) + "\r\n");
                        }
                        catch (IOException) { }
                    };
                    t6.Start();
                };
                t5.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "pick")
            {
                // "pick": three rows taken together, the third one the one shown
                var t4 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t4.Tick += (s, e) =>
                {
                    t4.Stop();
                    var three = (st?.Mods ?? new List<ModRow>()).Where(Shown).Take(3).ToList();
                    foreach (var x in three) picked.Add(x.Id);
                    if (three.Count > 0) selectedId = three[three.Count - 1].Id;
                    RenderList();
                    RenderDetail();
                };
                t4.Start();
                return;
            }
            // tests only: what a row or the details hold, walked through the logical tree
            IEnumerable<DependencyObject> All(DependencyObject root)
            {
                yield return root;
                foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                    foreach (var d in All(c)) yield return d;
            }
            IEnumerable<Button> FindButtons(DependencyObject root) => All(root).OfType<Button>();
            IEnumerable<string> Texts_(DependencyObject root) => All(root).OfType<TextBlock>().Select(t => t.Text);
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "checkone")
            {
                // "checkone": the update mark on the third row clicked while the first is shown;
                // writes down which mod the details show before and after
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var shown = st.Mods.Where(Shown).ToList();
                    if (shown.Count < 3) return;
                    Current(shown[0].Id);
                    RenderList();
                    RenderDetail();
                    var before = selectedId == shown[0].Id ? "first" : selectedId;
                    var row = rows[shown[2].Id].Row;
                    var mark = FindButtons(row).FirstOrDefault(b => b.Content is Path p && p.Width == 18);
                    mark?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, mark));
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    var title = Detail.Children.OfType<FrameworkElement>().SelectMany(Texts_).FirstOrDefault(t => t == shown[2].Name);
                    if (path != null) File.WriteAllText(path, "before=" + before + "\r\nafter=" + (selectedId == shown[2].Id ? "third" : selectedId) +
                                                               "\r\nmark_found=" + (mark != null) + "\r\ndetails_name=" + (title ?? "-") + "\r\n");
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "tagmenu")
            {
                // "tagmenu": a label made from the first row's menu, then the second row's menu
                // opened the way a right click opens it. Writes down whether that menu has the new
                // label before it opens (the menu the row was drawn with) and once it has opened.
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var a = st?.Mods.ElementAtOrDefault(0);
                    var b = st?.Mods.ElementAtOrDefault(1);
                    if (a == null || b == null) return;
                    const string label = "新建测试标签";
                    PutTag(new List<ModRow> { a }, label, true);
                    var k2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    k2.Tick += (s2, e2) =>
                    {
                        k2.Stop();
                        if (!rows.TryGetValue(b.Id, out var r)) return;
                        bool Has(ContextMenu cm) =>
                            cm != null && cm.Items.OfType<MenuItem>().Any(x => x.Items.OfType<MenuItem>().Any(y => (y.Header as string) == label));
                        bool before = Has(r.Row.ContextMenu);
                        // what a right click does first: the opening event, then the menu read off the row
                        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                            new object[] { r.Row, true, 0.0, 0.0 }, null);
                        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                        r.Row.RaiseEvent(args);
                        bool after = Has(r.Row.ContextMenu);
                        // and the mods taken together: three taken and drawn, then a fourth added,
                        // which draws only the fourth row again
                        var four = st.Mods.Where(Shown).Take(4).ToList();
                        picked.Clear();
                        foreach (var x in four.Take(3)) picked.Add(x.Id);
                        RenderList();
                        picked.Add(four[3].Id);
                        RenderList();
                        var r0 = rows[four[0].Id];
                        string Header(ContextMenu cm) => cm?.Items.OfType<MenuItem>().FirstOrDefault()?.Header as string ?? "";
                        var stale = Header(r0.Row.ContextMenu);
                        var args2 = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                            new object[] { r0.Row, true, 0.0, 0.0 }, null);
                        args2.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                        r0.Row.RaiseEvent(args2);
                        var fresh = Header(r0.Row.ContextMenu);
                        picked.Clear();
                        RenderList();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null) File.WriteAllText(path, "before_open=" + before + "\r\nafter_open=" + after +
                                                                   "\r\nmany_before_open=" + stale + "\r\nmany_after_open=" + fresh + "\r\n");
                        r.Row.ContextMenu.PlacementTarget = r.Row;
                        r.Row.ContextMenu.IsOpen = true;
                        var sub = r.Row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Items.Count > 0);
                        if (sub != null) Dispatcher.BeginInvoke(new Action(() => sub.IsSubmenuOpen = true), DispatcherPriority.Loaded);
                    };
                    k2.Start();
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "menu")
            {
                // "menu": a row's menu with the labels open under it, for its picture
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var first = st?.Mods.FirstOrDefault();
                    if (first == null || !rows.TryGetValue(first.Id, out var r)) return;
                    r.Row.ContextMenu.PlacementTarget = r.Row;
                    r.Row.ContextMenu.IsOpen = true;
                    var sub = r.Row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Items.Count > 0);
                    if (sub != null) Dispatcher.BeginInvoke(new Action(() => sub.IsSubmenuOpen = true), DispatcherPriority.Loaded);
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "filter")
            {
                // "filter": one choice taken and the menu open, for its picture
                var f = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                f.Tick += (s, e) =>
                {
                    f.Stop();
                    fromFilter.Add("nexus");
                    kindFilter.Add("loader");
                    AfterFilter();
                    Filter_Click(FilterBtn, new RoutedEventArgs());
                };
                f.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "tags")
            {
                // "tags": the labels page drawn again and again, as clicking about does
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    ShowPage("tags");
                    tagHunt.Text = "x";
                    tagHunt.Text = "";
                    RenderTags();
                    RenderTags();
                    // a label taken, two mods ticked in the add box: the whole page at work
                    tagSel = st?.Tags.FirstOrDefault() ?? st?.Mods.SelectMany(m => m.Tags).FirstOrDefault();
                    tagAuto = false;
                    RenderTags();
                    if (tagSel != null)
                        foreach (var m in (st?.Mods ?? new List<ModRow>()).Where(x => !Carries(x, tagSel, false)).Take(2))
                            tagPick.Add(m.Id);
                    RenderTags();
                    Dispatcher.BeginInvoke(new Action(() => TagsPage.ScrollToEnd()), DispatcherPriority.Loaded);
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "profilemenu" || Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "libview")
            {
                // "profilemenu": the menu at the list's title left open; "libview": the mod library on show
                bool menu = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "profilemenu";
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    if (menu) ProfileBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, ProfileBtn));
                    else
                    {
                        viewLibrary = true; RenderNav(); RenderList(); RenderDetail();
                        // where the profile's own mods end
                        Dispatcher.BeginInvoke(new Action(() => ListScroll.ScrollToVerticalOffset(4 * 73)), DispatcherPriority.Loaded);
                    }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "dltime")
            {
                // "dltime": how long the download page takes to draw, written down
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    ShowPage("download");
                    UpdateLayout();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    var said = "download page drawn in " + clock.ElapsedMilliseconds + " ms";
                    var list = Downloads.Children.OfType<Border>().Select(b => b.Child).OfType<ScrollViewer>().FirstOrDefault(v => v.MaxHeight == 470);
                    if (list != null)
                    {
                        int Rows() => ((Panel)list.Content).Children.Count;
                        said += "\nrows at first: " + Rows();
                        for (int i = 0; i < 30; i++) { list.ScrollToEnd(); UpdateLayout(); }
                        said += "\nrows after scrolling to the end: " + Rows();
                    }
                    if (path != null) try { File.WriteAllText(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "menutoggle")
            {
                // "menutoggle": the labels menu opened, then closed by the press on its own button
                // and that button's click at once; then a click later. Writes down what was open.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var said = new List<string>();
                    void Click() => TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    Click();
                    var first = lastMenu;
                    said.Add("after the first click: open " + (first?.IsOpen == true));
                    var g2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    g2.Tick += (s2, e2) =>
                    {
                        g2.Stop();
                        first.IsOpen = false;          // the press outside closes it
                        Click();                       // and the same click lands on the button
                        said.Add("a click on its button while open: open " + (lastMenu?.IsOpen == true) + ", a new menu " + !ReferenceEquals(lastMenu, first));
                        var g3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        g3.Tick += (s3, e3) =>
                        {
                            g3.Stop();
                            Click();
                            said.Add("a click later: open " + (lastMenu?.IsOpen == true));
                            var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                            if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                        };
                        g3.Start();
                    };
                    g2.Start();
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "addlib")
            {
                // "addlib": HD2QD_ADDLIB (a folder) added as a library folder, as the button does,
                // and its question left open for a picture
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) => { g.Stop(); ShowPage("storage"); AddLibraryFolder(Environment.GetEnvironmentVariable("HD2QD_ADDLIB")); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "listtop")
            {
                // "listtop": the list scrolled back to its top after the start, for the README's
                // picture (--select opens a mod and scrolls it to the top of the list)
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                g.Tick += (s, e) => { g.Stop(); ListScroll.ScrollToVerticalOffset(0); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "topwins")
            {
                // "topwins": the list shown with the winner at the top. Writes down the rows' order,
                // where a row dropped just below the top row would land, and the row menu's moves.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    Texts.ShowTopWins(true);
                    rows.Clear();
                    List.Children.Clear();
                    Words();
                    Render();
                    UpdateLayout();
                    var said = new List<string>();
                    var seen = Seen().ToList();
                    said.Add("rows, top first: " + string.Join(", ", seen.Take(3).Select(x => x.Index)) + " ... " + seen.Last().Index + " of " + st.Mods.Count);
                    var dragged = seen[2];
                    var onto = rows[seen[0].Id].Row;
                    double top = onto.TranslatePoint(new Point(0, 0), ListArea).Y;
                    BeginDrag(dragged, rows[dragged.Id].Row, new Point(10, top + onto.ActualHeight * 0.75), 5, false);
                    said.Add("row " + dragged.Index + " dropped just below the top row (" + seen[0].Index + "): lands at " + dropTo);
                    EndDrag(false);
                    said.Add("row menu: " + string.Join(" | ", Menu(seen[0]).Items.OfType<MenuItem>().Select(x => x.Header as string).Where(x => x != null)));
                    said.Add("hint: " + ListHint.Text);
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "exclude")
            {
                // "exclude": the labels menu opened, Alt pressed and let go on it as the keyboard
                // sends it, then an Alt+click on Probe. Writes down whether the menu stayed open,
                // what the list shows, and leaves the menu open for its picture.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var said = new List<string>();
                    int Rows() => (st?.Mods ?? new List<ModRow>()).Count(Shown);
                    int Probes() => (st?.Mods ?? new List<ModRow>()).Where(Shown).Count(m => m.Tags.Any(t => string.Equals(t, "Probe", StringComparison.CurrentCultureIgnoreCase)));
                    said.Add("before: shown " + Rows() + ", of them Probe " + Probes());
                    TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    var g2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    g2.Tick += (s2, e2) =>
                    {
                        g2.Stop();
                        var hwnd = (PresentationSource.FromVisual(lastMenu) as HwndSource)?.Handle ?? IntPtr.Zero;
                        PostMessage(hwnd, 0x0104, (IntPtr)0x12, (IntPtr)0x20380001);              // WM_SYSKEYDOWN Alt
                        PostMessage(hwnd, 0x0105, (IntPtr)0x12, unchecked((IntPtr)(int)0xC0380001)); // WM_SYSKEYUP Alt
                        var g3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        g3.Tick += (s3, e3) =>
                        {
                            g3.Stop();
                            said.Add("menu open after Alt: " + (lastMenu?.IsOpen == true));
                            var probe = lastMenu?.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string ?? "").StartsWith("Probe "));
                            if (probe == null) said.Add("no Probe label in the menu");
                            else
                            {
                                altForTest = true;
                                probe.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, probe));
                                altForTest = false;
                                said.Add("after Alt+click: shown " + Rows() + ", of them Probe " + Probes() + ", item tag " + probe.Tag);
                                said.Add("menu open after the click: " + (lastMenu?.IsOpen == true));
                            }
                            var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                            if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                        };
                        g3.Start();
                    };
                    g2.Start();
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "nestwheel")
            {
                // "nestwheel": the wheel over the tags page's add list. Writes down where the list
                // and the page are after one turn, a long turn, the next turn, and a turn over the
                // list with nothing in it to scroll.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    Height = 640;
                    ShowPage("tags");
                    tagSel = st?.Tags.FirstOrDefault() ?? st?.Mods.SelectMany(m => m.Tags).FirstOrDefault();
                    tagAuto = false;
                    RenderTags();
                    UpdateLayout();
                    var inner = tagHits?.Children.OfType<ScrollViewer>().FirstOrDefault();
                    var said = new List<string>();
                    if (inner == null) said.Add("no add list");
                    else
                    {
                        int t = Environment.TickCount;
                        void Turn(int after)
                        {
                            t += after;
                            var under = (inner.Content as Panel)?.Children.OfType<UIElement>().FirstOrDefault() ?? inner;
                            under.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, t, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                            UpdateLayout();
                        }
                        void Say(string what) =>
                            said.Add(what + ": list " + inner.VerticalOffset + "/" + inner.ScrollableHeight +
                                     " page " + TagsPage.VerticalOffset + "/" + TagsPage.ScrollableHeight);
                        TagsPage.ScrollToVerticalOffset(0); inner.ScrollToVerticalOffset(0); UpdateLayout();
                        Say("start");
                        Turn(1000); Say("one turn");
                        // one notch from the end, then the wheel kept turning, a notch each 50 ms
                        inner.ScrollToVerticalOffset(inner.ScrollableHeight - 48); UpdateLayout();
                        Turn(1000); Say("a turn to the end");
                        for (int i = 0; i < 4; i++) Turn(50);
                        Say("0.2 s more (held)");
                        for (int i = 0; i < 4; i++) Turn(50);
                        Say("0.4 s more (the page)");
                        TagsPage.ScrollToVerticalOffset(0); UpdateLayout();
                        Turn(600); Say("next turn, after a rest");
                        // the page turning, then a list comes under the pointer: the list takes over at once
                        inner.ScrollToVerticalOffset(0); UpdateLayout();
                        var pageBefore = TagsPage.VerticalOffset;
                        Turn(30); Say("still the same turn, the list under the pointer (the list moves)");
                        inner.MaxHeight = double.PositiveInfinity;
                        TagsPage.ScrollToVerticalOffset(0); UpdateLayout();
                        Say("no scroll on the list");
                        Turn(600); Say("a turn over it");
                    }
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "settings")
            {
                // "settings": the settings window, so a test outside can take its picture
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) => { g.Stop(); SettingsBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, SettingsBtn)); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "conflict")
            {
                // "conflict": the list of mods in conflict, the first one's details on show
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) => { g.Stop(); filter = "conflict"; RenderNav(); FollowList(); RenderList(); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "updates")
            {
                // "updates": the update check's button, the same way
                var u = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                u.Tick += (s, e) => { u.Stop(); UpdatesBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, UpdatesBtn)); };
                u.Start();
                return;
            }
            if (!int.TryParse(Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST"), out var n)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var m = st?.Mods.FirstOrDefault(x => x.Index == n);
                if (m == null || !switches.TryGetValue(m.Id, out var sw)) return;
                sw.IsChecked = sw.IsChecked != true;
                sw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, sw));
            };
            t.Start();
        }

        /// <summary>HD2QD_DRAG_TEST=N,DY (tests only): 4 s after the start, mod N's row is taken
        /// up and carried DY pixels down, and left hanging there — the picture a test outside
        /// takes then shows what a hand would see mid-drag. Nothing is moved: the drag is never
        /// let go.</summary>
        void DragTest()
        {
            var said = (Environment.GetEnvironmentVariable("HD2QD_DRAG_TEST") ?? "").Split(',');
            if (said.Length != 2 || !int.TryParse(said[0], out var n) || !double.TryParse(said[1], out var dy)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var m = st?.Mods.FirstOrDefault(x => x.Index == n);
                if (m == null || !rows.TryGetValue(m.Id, out var r)) return;
                var top = r.Row.TranslatePoint(new Point(0, 0), ListArea).Y;
                BeginDrag(m, r.Row, new Point(200, top + 20 + dy), 20, false);   // no mouse to hold it
            };
            t.Start();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveWhere();
            base.OnClosing(e);
        }

        /// <summary>The library and the deploy record, watched: a change from the
        /// command line shows here half a second later.</summary>
        void Watch()
        {
            var home = IOPath.GetDirectoryName(Cli.LibraryFile);
            try { Directory.CreateDirectory(home); }               // a first start: nothing saved yet
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            watcher = new FileSystemWatcher(home, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(new Action(() => { settle.Stop(); settle.Start(); }));
            watcher.Changed += h;
            watcher.Created += h;
            watcher.Renamed += (s, e) => h(s, e);
            // it dies silently on an overflow or when the folder is taken away: without this a
            // window left open would never see another change (the user, 2026-10-25)
            watcher.Error += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try { watcher.Dispose(); } catch (Exception x) when (x is IOException || x is ObjectDisposedException) { }
                watcher = null;
                Watch();
                settle.Stop();
                settle.Start();
            }));
            watcher.EnableRaisingEvents = true;
            again.Tick += (s, e) => Refresh();
            // and whatever the watcher still misses: the library and the deploy record are
            // looked at every few seconds, which is two file reads and nothing else
            beatSaw = Written();
            beat.Tick += (s, e) =>
            {
                var now = Written();
                if (now == beatSaw) return;
                beatSaw = now;
                if (!busy && DateTime.Now > quietUntil) Refresh();
            };
            beat.Start();
        }

        readonly DispatcherTimer beat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        string beatSaw;

        /// <summary>The size and time of the library and the deploy record, as one string.</summary>
        static string Written()
        {
            var out_ = new System.Text.StringBuilder();
            foreach (var name in new[] { "library.json", "deployed.json" })
                try
                {
                    var f = new FileInfo(Store.PathOf(name));
                    out_.Append(f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "-").Append(" ");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { out_.Append("? "); }
            return out_.ToString();
        }

        // commands and state reads run one at a time, in the order they were asked for
        readonly System.Threading.SemaphoreSlim gate = new System.Threading.SemaphoreSlim(1, 1);
        // quick changes (a switch, an option, the order) asked for and not yet done
        int waiting;
        bool refreshing, refreshAgain;

        async Task<T> Queued<T>(Func<T> work)
        {
            await gate.WaitAsync();
            try { return await Task.Run(work); }
            finally { gate.Release(); }
        }

        /// <summary>The state read again in the background and shown without holding the
        /// window; a state read while a click is still waiting is not shown (the next one is).</summary>
        async void Refresh()
        {
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            if (st == null) Foot(Texts.Get("reading"), "Muted");
            try
            {
                do
                {
                    refreshAgain = false;
                    var r = await Queued(() => Cli.Run("state"));
                    if (!r.Ok)
                    {
                        // the list on show was read before this one failed. It is not the library
                        // any more, so it is taken away rather than left standing there looking
                        // current, and the reading goes on until it works (the user, 2026-10-25)
                        Lost(Last(r.Message));
                        return;
                    }
                    Found();
                    // a quick change is still on its way: what was just read is already out of
                    // date, so read it again rather than drop it and show nothing (the user,
                    // 2026-10-25: another session deployed and the window never caught up)
                    if (waiting > 0) { refreshAgain = true; await Task.Delay(250); continue; }
                    try { st = Cli.ReadState(r.Out); }
                    catch (FormatException e) { Foot(Texts.F("failed", e.Message), "Yellow"); return; }
                    Render();
                } while (refreshAgain);
            }
            finally { refreshing = false; }
            if (!setupDone)
            {
                setupDone = true;
                Setup();
            }
        }

        bool setupDone;

        /// <summary>The first start, in the window and never on a command line: the game
        /// folder (found from HD2 Arsenal's or Steam's settings, else chosen), then, with
        /// an empty library, HD2 Arsenal's mod list if it has one.</summary>
        async void Setup()
        {
            bool changed = false;
            if (st.GameDir == null)
            {
                var r = await Exec("init");
                if (!r.Ok)
                {
                    Dialog.Tell(this, Texts.Get("game_ask"));
                    var g = PickGame();
                    if (g != null) await Exec("init", "--game", g);
                }
                changed = true;
            }
            if (changed) Refresh();
            // an empty library: the lists of the managers found (HD2 Arsenal, HD2ModManager)
            if (st.Mods.Count == 0) ImportFromManagers(true);
            NoPurgeTip();
            // the versions and sources HD2 Arsenal recorded for its mods, learnt once per start
            await Queued(() =>
            {
                try
                {
                    var lib = Library.Load();
                    if (Origins.FromArsenal(lib) > 0) lib.Save();
                }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
                return 0;
            });
        }

        /// <summary>A version as shown: "v1.2" for one that starts with a digit, else as written.</summary>
        static string V(string v) => v.Length > 0 && char.IsDigit(v[0]) ? "v" + v : v;

        static string VersionTip(ModRow m) => m.ShownVersion == null ? Texts.Get("ver_none") : Texts.F("ver_tip", Texts.Get("ver_from_" + m.VersionFrom));

        /// <summary>Once, on the first start (the user, 2026-10-24): installing mods never needs a
        /// purge; deploying does it all, only what changed. Remembered in gui.json.</summary>
        void NoPurgeTip()
        {
            Dictionary<string, object> d;
            try { d = Store.Load("gui.json"); }
            catch (Exception e) when (e is IOException || e is FormatException) { d = new Dictionary<string, object>(); }
            if (Json.Bool(d, "tip_no_purge")) return;
            Dialog.Tell(this, Texts.Get("tip_no_purge"));
            d["tip_no_purge"] = true;
            try { Store.Save("gui.json", d); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The game folder chosen in a folder dialog: the game folder, its data
        /// or its bin; asked again when it is none of them; null when cancelled.</summary>
        string PickGame()
        {
            while (true)
            {
                var chosen = Pick.Folder(this, Texts.Get("game_pick_title"), st?.GameDir);
                if (chosen == null) return null;
                var g = Ops.GameFolderOf(chosen);
                if (g != null) return g;
                Dialog.Tell(this, Texts.F("not_game", chosen));
            }
        }

        void ChooseGame()
        {
            var g = PickGame();
            if (g != null) Run("init", "--game", g);
            else Refresh();
        }

        /// <summary>A big change (deploy, purge, add, ...): the window held meanwhile.</summary>
        async Task<CliResult> Exec(Func<CliResult> work)
        {
            busy = true;
            SetEnabled(false);
            Foot(Texts.Get("working"), "Blue");
            var r = await Queued(work);
            quietUntil = DateTime.Now.AddSeconds(1.5);
            busy = false;
            SetEnabled(true);
            return r;
        }

        Task<CliResult> Exec(params string[] args) => Exec(() => Cli.Run(args));

        /// <summary>One hd2qd command, then the state again.</summary>
        void Run(params string[] args) => _ = Act(() => Cli.Run(args), false);

        /// <summary>Several commands, each waited for before the next: Act turns away a command
        /// while another is running, so firing them in a loop ran only the first.</summary>
        async void RunMany(List<string[]> commands)
        {
            foreach (var args in commands)
                if (!await Act(() => Cli.Run(args), false)) return;
        }

        /// <summary>A change, then the state again. done: its text (the window's own) goes
        /// to the footer with the time it took. A refusal is said in the window's own words
        /// when it has them, never as a command to type.</summary>
        async Task<bool> Act(Func<CliResult> work, bool done)
        {
            if (busy) return false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await Exec(work);
            clock.Stop();
            if (Refused(r)) return false;
            if (done && r.Ok) lastDone = Texts.F("took", Last(r.Out), Seconds(clock.Elapsed.TotalSeconds));
            Refresh();
            return r.Ok;
        }

        /// <summary>A quick change (a switch, an option, the order): shown at once from
        /// what it implies, done in the background in order, the window never held; the
        /// rows it did not change stay as they are.</summary>
        async void Change(string id, Action<ModRow> local, params string[] args)
        {
            var m = st?.Mods.FirstOrDefault(x => x.Id == id);
            if (m == null || busy) return;
            local(m);
            m.Assumed = m.Enabled ? "pending" : "off";
            RenderList();
            if (id == selectedId) RenderDetail();
            waiting++;
            var r = await Queued(() => Cli.Run(args));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

        /// <summary>Says a refusal; true when it took over (the game folder is asked for).</summary>
        bool Refused(CliResult r)
        {
            if (r.Ok) return false;
            switch (r.Refused?.Kind)
            {
                case "no_game":
                    Dialog.Tell(this, Texts.Get("game_ask"));
                    ChooseGame();
                    return true;
                case "duplicate": Dialog.Tell(this, Texts.F("add_dupe", r.Refused.Arg)); break;
                case "game_running": Dialog.Tell(this, Texts.Get("game_running")); break;
                case "protected": Dialog.Tell(this, Texts.F("del_protected", r.Refused.Arg)); break;
                case "shared": Dialog.Tell(this, Texts.F("del_shared", r.Refused.Arg)); break;
                case "delete_failed": Dialog.Tell(this, Texts.F("delete_failed", r.Refused.Arg)); break;
                case "not_zip": Dialog.Tell(this, Texts.F("nx_not_zip", r.Refused.Arg)); break;
                default: Dialog.Tell(this, Texts.F("failed", r.Message)); break;
            }
            return false;
        }

        static string Seconds(double s) =>
            s.ToString(s < 10 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);

        static string Last(string text)
        {
            var lines = (text ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        void SetEnabled(bool on)
        {
            Body.IsEnabled = on;
            DeployBtn.IsEnabled = LaunchBtn.IsEnabled = on;
        }

        /// <summary>The library could not be read: what is on show is from before, so it goes
        /// away and the reading keeps trying by itself.</summary>
        void Lost(string why)
        {
            st = null;
            List.Children.Clear();
            rows.Clear();
            Detail.Children.Clear();
            LostTitle.Text = Texts.Get("lost");
            LostWhy.Text = why;
            LostBox.Visibility = Visibility.Visible;
            Foot(Texts.F("failed", why), "Yellow");
            again.Start();
        }

        void Found()
        {
            again.Stop();
            LostBox.Visibility = Visibility.Collapsed;
        }

        // while the library cannot be read, it is tried again until it can
        readonly DispatcherTimer again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        void Foot(string text, string colour)
        {
            FootText.Text = text;
            FootText.Foreground = B(colour == "Muted" ? "Ink" : colour);
            FootDot.Fill = B(colour);
        }

        // ---------------------------------------------------------------- the whole window

        // what the filter beside the search box keeps, by kind; each kind empty = everything.
        // Within one kind any of them will do; a mod must pass every kind that has something.
        readonly HashSet<string> tagFilter = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        // the labels left out: a mod carrying any of them is not shown, whatever else it carries
        readonly HashSet<string> tagExclude = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        readonly HashSet<string> fromFilter = new HashSet<string>();
        readonly HashSet<string> kindFilter = new HashSet<string>();

        bool Shown(ModRow m) => InScope(m) && InFilter(m) && Matches(m) && Labelled(m) && FromOne(m) && OfKind(m);

        // the mod library shown instead of the profile in use (profiles on)
        bool viewLibrary;

        /// <summary>In what the list shows: the profile in use, or with the library on show (or
        /// profiles off) every mod.</summary>
        bool InScope(ModRow m) => st == null || !st.ProfilesOn || viewLibrary || m.InProfile;

        /// <summary>A profile's name as shown: the first one's in the window's language.</summary>
        static string ProfileName(string name) => name == Profiles.First ? Texts.Get("pf_default") : name;

        /// <summary>The mods the list shows, top row first: the load order, or the other way round
        /// when the mod that wins is shown at the top (Texts.TopWins).</summary>
        IEnumerable<ModRow> Seen() => Texts.TopWins ? st.Mods.Where(Shown).Reverse() : st.Mods.Where(Shown);

        /// <summary>Any one of the labels the filter holds (or no filter at all), and none it
        /// leaves out.</summary>
        bool Labelled(ModRow m) =>
            (tagFilter.Count == 0 || m.Tags.Any(t => tagFilter.Contains(t))) && !m.Tags.Any(t => tagExclude.Contains(t));

        /// <summary>Where the mod comes from: nexus, github, or none recorded.</summary>
        static string FromOf(ModRow m) => m.Channel == "Nexus Mods" ? "nexus" : m.Channel == "GitHub" ? "github" : "none";

        bool FromOne(ModRow m) => fromFilter.Count == 0 || fromFilter.Contains(FromOf(m));

        /// <summary>What the mod is: a loader, another Lua mod, or files only.</summary>
        static string KindOf(ModRow m) => m.Loader ? "loader" : m.Lua ? "lua" : "asset";

        bool OfKind(ModRow m) => kindFilter.Count == 0 || kindFilter.Contains(KindOf(m));

        string[] searchWords = new string[0];
        TextBlock noMatch;

        /// <summary>Every word typed is somewhere in the mod's metadata (any case): its name,
        /// the author's description, its options' and sub-options' names and descriptions,
        /// its folder and source, its patch files (archive ids), its Nexus mod id, "lua".</summary>
        bool Matches(ModRow m)
        {
            if (searchWords.Length == 0) return true;
            var hay = new System.Text.StringBuilder();
            void Add(string x) { if (!string.IsNullOrEmpty(x)) hay.Append(x).Append('\n'); }
            Add(m.Name); Add(m.Description); Add(m.Path); Add(m.Source); Add(m.Error);
            foreach (var o in m.Options)
            {
                Add(o.Name); Add(o.Description);
                foreach (var x in o.Subs) { Add(x.Name); Add(x.Description); }
            }
            foreach (var f in m.Files) { Add(f.Name); Add(f.Src); }
            if (m.Lua) Add("lua");
            if (m.Loader) Add("loader");
            foreach (var t in m.Tags) Add(t);
            if (m.NexusMod > 0) { Add("nexus"); Add(m.NexusMod.ToString()); Add(m.NexusPage); }
            Add(m.Channel); Add(m.Page); Add(m.Version); Add(m.ShownVersion);
            var text = hay.ToString();
            return searchWords.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            searchWords = SearchBox.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (st == null) return;
            FollowList();
            RenderList();
        }

        /// <summary>The details follow what the list shows: the chosen mod while it is in the
        /// list, else the first one in it. For the search, and for the list on the left (the
        /// user, 2026-10-25: 冲突 showed a mod in no conflict).</summary>
        void FollowList()
        {
            var first = Seen().FirstOrDefault();
            if (first != null && !st.Mods.Any(m => m.Id == selectedId && Shown(m)))
            {
                selectedId = first.Id;
                RenderDetail();
            }
        }

        void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        bool InFilter(ModRow m)
        {
            switch (filter)
            {
                case "pending": return m.Enabled && m.Changes > 0;
                case "options": return m.Options.Count > 0;
                case "off": return !m.Enabled;
                case "conflict": return m.Conflicts > 0;
                case "broken": return m.Error != null;
                case "lua": return m.Lua;
                case "updates": return HasUpdate(m);
                default: return true;
            }
        }

        void Render()
        {
            int on = st.Mods.Count(m => m.Enabled);
            // only the missing game folder is said under the title: the counts were noise there (the user, 2026-10-25)
            SummaryText.Text = st.GameDir == null ? Texts.Get("no_game") : "";
            SummaryText.Visibility = st.GameDir == null ? Visibility.Visible : Visibility.Collapsed;
            StatLinks.Text = Texts.F("stat_links_v", Texts.N(st.LinkedFiles), Texts.Size(st.LinkedBytes));
            DeployBtn.Content = st.Pending > 0 ? Texts.F("deploy_n", st.Pending) : Texts.Get("deploy");
            DeployBtn.IsEnabled = st.Deploying == null;      // two at once would undo each other
            // what was just done comes first (a download leaves changes to deploy: the Deploy
            // button counts them); else whether the game folder matches
            if (st.GameDir == null) Foot(Texts.Get("no_game"), "Yellow");
            // another program is writing to the game folder: what was just read is a state on
            // its way somewhere, and its numbers are nobody's fault (the user, 2026-10-25)
            else if (st.Deploying != null) Foot(Texts.F("deploy_busy", st.Deploying.Since), "Yellow");
            // files a deploy would leave alone that are not what it thinks they are: an
            // interrupted deploy, another manager, an antivirus. Said before anything else,
            // because nothing else here is true while it holds. What a deploy is on its way to
            // put right is not said here: the Deploy button already counts that (the user,
            // 2026-10-25: a library waiting to be deployed is not a fault)
            else if (st.Check.Wrong > 0) Foot(Texts.F("mismatch", st.Check.Wrong), "Yellow");
            else if (lastDone != null) Foot(Texts.F("done", lastDone), "Blue");
            else if (st.Pending == 0) Foot(Texts.Get("synced"), "Blue");
            else Foot(Texts.F("pending", st.Written, st.Moved, st.Deleted, Texts.Size(st.CopiedBytes)), "Yellow");
            lastDone = null;
            if (startWith != null)
            {
                // --select TEXT: the first mod whose name holds it, chosen and open
                var first = st.Mods.FirstOrDefault(m => m.Name.IndexOf(startWith, StringComparison.OrdinalIgnoreCase) >= 0);
                if (first != null)
                {
                    selectedId = first.Id;
                    open.Add(first.Id);
                    // scrolled to, once the list is laid out
                    Dispatcher.BeginInvoke(new Action(() => { if (rows.TryGetValue(first.Id, out var r)) r.Row.BringIntoView(); }), DispatcherPriority.Loaded);
                }
                startWith = null;
            }
            // the chosen mod stays while it is in the list; else the first the list shows
            WhereItWas();
            if (!st.Mods.Any(m => m.Id == selectedId && (searchWords.Length == 0 || Shown(m))))
                selectedId = (Seen().FirstOrDefault() ?? st.Mods.FirstOrDefault())?.Id;
            RenderNav();
            FilterWords();
            RenderList();
            RenderDetail();
            if (StoragePage.Visibility == Visibility.Visible) RenderStorage();
            if (startPage == "download" || startPage == "storage" || startPage == "tags") ShowPage(startPage);
            startPage = null;
        }

        // ---------------------------------------------------------------- the filters and the target

        void RenderNav()
        {
            Nav.Children.Clear();
            Nav.Children.Add(Label("library", new Thickness(8, 0, 0, 4)));
            if (filter == "lua") filter = "all";
            var scope = st.Mods.Where(InScope).ToList();
            var defs = new[]
            {
                // what needs doing something, counted in what the list shows (the profile in use,
                // or the library); what a mod is (Lua and the rest) is the Filter's (the user, 2026-10-25)
                ("all", scope.Count), ("pending", scope.Count(m => m.Enabled && m.Changes > 0)),
                ("updates", scope.Count(HasUpdate)),
                ("off", scope.Count(m => !m.Enabled)),
                ("conflict", scope.Count(m => m.Conflicts > 0)), ("broken", scope.Count(m => m.Error != null)),
            };
            foreach (var (key, n) in defs)
            {
                if ((key == "broken" || key == "updates") && n == 0 && filter != key) continue;
                bool cur = filter == key, hot = (key == "pending" || key == "broken" || key == "updates") && n > 0;
                var btn = new Button { Cursor = Cursors.Hand, FocusVisualStyle = null, Margin = new Thickness(0, 2, 0, 2) };
                var row = new Border
                {
                    MinHeight = 44, Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(2, 0, 0, 0),
                    BorderBrush = cur ? B("Yellow") : Brushes.Transparent, Background = cur ? B("Ground700") : Brushes.Transparent,
                };
                var dp = new DockPanel { LastChildFill = false };
                var badge = new Border
                {
                    Background = hot ? B("Yellow") : B("Grid"), Padding = new Thickness(6, 0, 6, 0), MinWidth = 22, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = n.ToString(), FontFamily = Mono, FontSize = 11, Foreground = hot ? B("TapeInk") : B("Muted"), HorizontalAlignment = HorizontalAlignment.Center },
                };
                DockPanel.SetDock(badge, Dock.Right);
                dp.Children.Add(badge);
                dp.Children.Add(new TextBlock { Text = Texts.Get("f_" + key), FontWeight = FontWeights.SemiBold, Foreground = cur ? B("Yellow") : B("Ink"), VerticalAlignment = VerticalAlignment.Center });
                row.Child = dp;
                btn.Template = Plain();
                btn.Content = row;
                btn.Click += (s, e) => { filter = key; RenderNav(); FollowList(); RenderList(); };
                Nav.Children.Add(btn);
            }

            // the deploy target, without a heading: its state says what it is (the user, 2026-10-25)
            Nav.Children.Add(new Border { Height = 24 });
            var card = new StackPanel();
            int far = st.Mods.Count(m => m.Exists && !m.SameDrive);
            string state = far == 0 ? "target_link" : far == st.Mods.Count(m => m.Exists) ? "target_copy" : "target_mixed";
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(far == 0 ? "Blue" : "Yellow"), Margin = new Thickness(0, 0, 8, 0) });
            head.Children.Add(new TextBlock { Text = Texts.Get(state), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(head);
            // no folders here: the Storage page shows them and changes them (the user, 2026-10-25)
            // only shown here: a "Change…" in this card read as changing the mod library too (the
            // user, 2026-10-25); the game folder is changed on the Storage page. With none set yet
            // the card offers to choose one.
            if (st.GameDir == null)
            {
                var pickGame = new Button
                {
                    Content = Texts.Get("game_pick"), Style = (Style)FindResource("Ghost"),
                    MinHeight = 32, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                pickGame.Click += (s, e) => ChooseGame();
                card.Children.Add(pickGame);
            }
            if (Texts.Hints) card.Children.Add(Small(Texts.Get("target_note"), new Thickness(0, 8, 0, 0)));
            // copies in the game folder (another manager's) that can be hard links
            var copies = st.Mods.Where(m => m.Enabled && m.SameDrive).SelectMany(m => m.Files).Where(f => f.Action == "keep" && !f.Linked).ToList();
            if (copies.Count > 0)
            {
                long bytes = copies.Sum(f => f.Bytes);
                card.Children.Add(new TextBlock { Text = Texts.F("copies", Texts.N(copies.Count), Texts.Size(bytes)), FontWeight = FontWeights.SemiBold, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
                if (Texts.Hints) card.Children.Add(Small(Texts.Get("copies_note"), new Thickness(0, 4, 0, 0)));
                // its words wrap: "Make hard links · free 1.5 MB" is wider than the card
                var relink = new Button
                {
                    Content = new TextBlock { Text = Texts.F("relink", Texts.Size(bytes)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                relink.Click += (s, e) => _ = Act(() => Cli.Call(() =>
                {
                    var r = Hd2Mod.Deploy.Relink(Library.Load(), Store.GameDir());
                    return Texts.F("relinked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                }), true);
                card.Children.Add(relink);
            }
            // the purge, out of the way: a deploy never needs one first
            card.Children.Add(new Border { Height = 1, Background = B("Grid"), Margin = new Thickness(0, 14, 0, 10) });
            if (Texts.Hints) card.Children.Add(Small(Texts.Get("purge_note"), new Thickness(0, 0, 0, 0)));
            var purge = new Button
            {
                Content = new TextBlock { Text = Texts.Get("purge_long"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            purge.Click += Purge_Click;
            card.Children.Add(purge);
            Nav.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(12), Child = card });
        }

        // ---------------------------------------------------------------- Nexus Mods downloads

        static string Exe => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;


        /// <summary>A row of a list on a faint band every other row, so a name on the left and its
        /// number on the right pair up at a glance (the user, 2026-10-25). In a card the band runs
        /// to the card's edges; in a list that scrolls, to the list's.</summary>
        Border Band(UIElement row, int index, bool toCardEdges = true) => new Border
        {
            Background = index % 2 == 1 ? B("Band") : Brushes.Transparent,
            Margin = toCardEdges ? new Thickness(-16, 0, -16, 0) : new Thickness(0),
            Padding = toCardEdges ? new Thickness(16, 0, 16, 0) : new Thickness(8, 0, 8, 0),
            Child = row,
        };

        Border Card(UIElement child) =>
            new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = child };

        TextBlock Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };


        /// <summary>Why a download failed, in the window's words.</summary>
        static string Why(string kind)
        {
            switch (kind)
            {
                case "not_zip": return Texts.Get("dl_not_zip");
                default: return kind ?? "";
            }
        }

        /// <summary>One of the three pages: "deploy", "download" or "storage".</summary>
        void ShowPage(string page)
        {
            PageDeploy.IsChecked = page == "deploy";
            PageDownload.IsChecked = page == "download";
            PageStorage.IsChecked = page == "storage";
            PageTags.IsChecked = page == "tags";
            Body.Visibility = page == "deploy" ? Visibility.Visible : Visibility.Collapsed;
            DownloadPage.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
            StoragePage.Visibility = page == "storage" ? Visibility.Visible : Visibility.Collapsed;
            TagsPage.Visibility = page == "tags" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "storage") RenderStorage();
            if (page == "tags") RenderTags();
        }

        void Page_Click(object sender, RoutedEventArgs e) =>
            ShowPage(sender == PageDownload ? "download" : sender == PageStorage ? "storage" : sender == PageTags ? "tags" : "deploy");

        // ---------------------------------------------------------------- the storage page

        // how the two lists of mods are sorted (the user, 2026-10-24)
        string storageSort = "size";

        /// <summary>The buttons that pick the order of a list: the one in use is lit.</summary>
        UIElement SortBy(string now, Action<string> pick, params (string Key, string Text)[] ways)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = Texts.Get("sort_by"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var group = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            var inside = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, text) in ways)
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), Content = text, MinWidth = 88, IsChecked = key == now, GroupName = "sort" + now.GetHashCode() };
                var mine = key;
                b.Click += (s, e) => pick(mine);
                inside.Children.Add(b);
            }
            group.Child = inside;
            row.Children.Add(group);
            return row;
        }

        // the folder shown with its mods, and the mods ticked in it
        string storageSel;
        readonly HashSet<string> ticked = new HashSet<string>();

        static string Gb(long b) => Texts.Size(b);

        string GameDir() => Store.GameDir(null, false);

        /// <summary>Where mods on another drive are best moved: the default folder when it is on the
        /// game's drive, else HD2 Mods on the game's drive.</summary>
        string GameDriveTarget(List<LibraryFolder> folders)
        {
            var def = folders.FirstOrDefault(f => f.IsDefault);
            if (def != null && def.GameDrive) return def.Path;
            var g = GameDir();
            return g == null ? def?.Path : IOPath.Combine(IOPath.GetPathRoot(g), "HD2 Mods");
        }

        /// <summary>Folders whose drive the player chose to keep (no advice for them again).</summary>
        static List<string> KeptDrives()
        {
            try { return Json.List(Store.Load("gui.json"), "drive_ok").OfType<string>().ToList(); }
            catch (Exception e) when (e is IOException || e is FormatException) { return new List<string>(); }
        }

        static void KeepDrives(IEnumerable<string> folders)
        {
            try
            {
                var d = Store.Load("gui.json");
                var list = Json.List(d, "drive_ok").OfType<string>().ToList();
                list.AddRange(folders);
                d["drive_ok"] = list.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The storage page: the mod folders as Steam lists its library folders (drive, free
        /// space, mods and their size, whether deploys can use hard links), the default for new mods,
        /// advice for mods on another drive than the game, and a chosen folder's mods to move.</summary>
        void RenderStorage()
        {
            Storages.Children.Clear();
            var lib = Library.Load();
            var folders = Storage.Folders(lib, GameDir());
            if (storageSel == null || folders.All(f => !string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase)))
                storageSel = (folders.FirstOrDefault(f => f.Mods.Count > 0) ?? folders.FirstOrDefault())?.Path;

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var addFolder = new Button { Content = Texts.Get("st_add_folder"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
            addFolder.Click += (s, e) => { var p = PickFolder("pick_lib_title"); if (p != null) AddLibraryFolder(p); };
            tools.Children.Add(addFolder);
            var import = new Button { Content = Texts.Get("st_import"), Style = (Style)FindResource("Ghost"), MinHeight = 40, Margin = new Thickness(8, 0, 0, 0) };
            import.Click += (s, e) => ImportFromManagers(false);
            tools.Children.Add(import);
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
            head.Children.Add(new TextBlock { Text = Texts.Get("st_title"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            Storages.Children.Add(head);
            Storages.Children.Add(Texts.Hints ? Small(Texts.Get("st_intro"), new Thickness(0, 0, 0, 20)) : new Border { Height = 12 });

            Storages.Children.Add(GameCard());
            Storages.Children.Add(new Border { Height = 20 });

            // what the hard links have saved over every deploy: a number that only grows, so it
            // lives here and not in the caption (the user, 2026-10-24)
            var won = new StackPanel();
            won.Children.Add(Heading(Texts.Get("st_saved_title")));
            var nums = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            void Stat(string key, string value)
            {
                var one = new StackPanel { Margin = new Thickness(0, 0, 48, 0) };
                one.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                one.Children.Add(new TextBlock { Text = value, FontFamily = Mono, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B("Blue") });
                nums.Children.Add(one);
            }
            Stat("stat_links", st == null ? "—" : Texts.F("stat_links_v", Texts.N(st.LinkedFiles), Texts.Size(st.LinkedBytes)));
            Stat("stat_saved", st == null ? "—" : Texts.Size(st.SavedBytes));
            won.Children.Add(nums);
            // an explaining line, gone with the others (the user, 2026-10-25)
            if (Texts.Hints) won.Children.Add(Small(Texts.Get("st_saved_note"), new Thickness(0, 12, 0, 0)));
            Storages.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = won });
            Storages.Children.Add(new Border { Height = 20 });

            // mods on another drive than the game, unless the player chose to keep them there
            var kept = KeptDrives();
            var far = folders.Where(f => !f.GameDrive && f.Mods.Count > 0 && !kept.Contains(f.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            if (far.Count > 0 && GameDir() != null)
            {
                var target = GameDriveTarget(folders);
                var advice = new StackPanel();
                advice.Children.Add(Heading(Texts.F("st_far", far.Sum(f => f.Mods.Count), string.Join("、", far.Select(f => f.Drive.TrimEnd('\\'))), IOPath.GetPathRoot(GameDir()).TrimEnd('\\'))));
                advice.Children.Add(Small(Texts.Get("st_far_why"), new Thickness(0, 8, 0, 0)));
                var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                var go = new Button { Content = Texts.F("st_far_move", target), Style = (Style)FindResource("Primary"), MinHeight = 40, Margin = new Thickness(0, 0, 8, 0) };
                go.Click += (s, e) => MoveMods(far.SelectMany(f => f.Mods).ToList(), target);
                row.Children.Add(go);
                var keep = new Button { Content = Texts.Get("st_far_keep"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
                keep.Click += (s, e) => { KeepDrives(far.Select(f => f.Path)); RenderStorage(); };
                row.Children.Add(keep);
                advice.Children.Add(row);
                Storages.Children.Add(new Border { BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = advice });
                Storages.Children.Add(new Border { Height = 20 });
            }

            // the folders
            foreach (var f in folders) Storages.Children.Add(FolderCard(f));

            // the chosen folder's mods
            var sel = folders.FirstOrDefault(f => string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase));
            if (sel == null) return;
            ticked.RemoveWhere(id => sel.Mods.All(m => m.Id != id));
            Storages.Children.Add(new Border { Height = 12 });
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            // plain, as "Open its folder": moving is not the page's main act (the user, 2026-10-25)
            var moveBtn = new Button { Content = Texts.F("st_move_n", ticked.Count), Style = (Style)FindResource("Ghost"), MinHeight = 40, IsEnabled = ticked.Count > 0 };
            moveBtn.Click += (s, e) =>
            {
                // no other library folder: a menu of one choice is a click for nothing (the user, 2026-10-25)
                if (!folders.Any(x => x != sel))
                {
                    var p = PickFolder("pick_move_title");
                    if (p != null) MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), p);
                    return;
                }
                var menu = new ContextMenu { PlacementTarget = moveBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var other in folders.Where(x => x != sel))
                {
                    var it = new MenuItem { Header = other.Path + (other.GameDrive ? "" : "  (" + Texts.Get("st_other_drive") + ")") };
                    var dest = other.Path;
                    it.Click += (s2, e2) => MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), dest);
                    menu.Items.Add(it);
                }
                var pick = new MenuItem { Header = Texts.Get("st_move_other") };
                pick.Click += (s2, e2) => { var p = PickFolder("pick_move_title"); if (p != null) MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), p); };
                menu.Items.Add(pick);
                menu.IsOpen = true;
            };
            // the page for seeing what takes space is where it gets deleted (the user, 2026-10-25);
            // Delete at the end, Move before it
            var delBtn = new Button
            {
                Content = Texts.F("st_delete_n", ticked.Count), Style = (Style)FindResource("Danger"), MinHeight = 40,
                IsEnabled = ticked.Count > 0,
            };
            delBtn.Click += (s, e) => DeleteTicked(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList());
            DockPanel.SetDock(delBtn, Dock.Right);
            bar.Children.Add(delBtn);
            moveBtn.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(moveBtn, Dock.Right);
            bar.Children.Add(moveBtn);
            var how = SortBy(storageSort, k => { storageSort = k; RenderStorage(); },
                             ("size", Texts.Get("sort_size")), ("name", Texts.Get("sort_name")));
            ((FrameworkElement)how).Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(how, Dock.Right);
            bar.Children.Add(how);
            var all = new CheckBox
            {
                Style = (Style)FindResource("Tick"),
                Content = new TextBlock { Text = Texts.F("st_mods_in", sel.Path), FontWeight = FontWeights.SemiBold, Foreground = B("Ink") },
                IsChecked = sel.Mods.Count > 0 && sel.Mods.All(m => ticked.Contains(m.Id)), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(17, 0, 0, 0),   // the card's border and padding, so it lines up with the ticks in it
            };
            all.Click += (s, e) =>
            {
                if (all.IsChecked == true) foreach (var m in sel.Mods) ticked.Add(m.Id);
                else ticked.Clear();
                RenderStorage();
            };
            bar.Children.Add(all);
            Storages.Children.Add(bar);
            if (sel.Mods.Count == 0) { Storages.Children.Add(Small(Texts.Get("st_empty"), new Thickness(0, 0, 0, 0))); return; }
            var list = new StackPanel();
            // keyed one by one, not with ToDictionary: two mods with the same id must not bring
            // the window down while it is only drawing a list (the user, 2026-10-24)
            var sizes = new Dictionary<string, long>();
            foreach (var x in sel.Mods)
                sizes[x.Id ?? ""] = Directory.Exists(x.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(x.Path)) : 0L;
            var shown = storageSort == "name"
                ? sel.Mods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : sel.Mods.OrderByDescending(x => sizes.TryGetValue(x.Id ?? "", out var b) ? b : 0L).ToList();
            int band = 0;
            foreach (var m in shown)
            {
                var id = m.Id;
                var box = new CheckBox { Style = (Style)FindResource("Tick"), IsChecked = ticked.Contains(id), Margin = new Thickness(0, 5, 0, 5) };
                var line = new DockPanel();
                var size = new TextBlock { Text = Directory.Exists(m.Path) && sizes.TryGetValue(m.Id ?? "", out var own) ? Gb(own) : "—", FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(size, Dock.Right);
                line.Children.Add(size);
                // the mod's own picture: a name and a folder are hard work to read (the user, 2026-10-24)
                var pic = Picture(st?.Mods.FirstOrDefault(x => x.Id == id)?.Icon, GlyphMod, 40, m.Enabled ? "Blue" : "Muted");
                pic.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(pic, Dock.Left);
                line.Children.Add(pic);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, Foreground = B(m.Enabled ? "Ink" : "Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock { Text = IOPath.GetFileName(m.Path.TrimEnd('\\')), FontFamily = Mono, FontSize = 11, Foreground = B("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
                line.Children.Add(words);
                box.Content = line;
                box.Click += (s, e) => { if (box.IsChecked == true) ticked.Add(id); else ticked.Remove(id); RenderStorage(); };
                list.Children.Add(Band(box, band++));
            }
            Storages.Children.Add(Card(list));
            OldVersions();
        }

        /// <summary>The ticked mods deleted from the disk, after one question that names them and their size.</summary>
        async void DeleteTicked(List<Mod> mods)
        {
            if (mods.Count == 0) return;
            long size = mods.Sum(m => Directory.Exists(m.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(m.Path)) : 0L);
            var names = string.Join("\n", mods.Take(10).Select(m => "· " + m.Name)) + (mods.Count > 10 ? "\n…" : "");
            if (!Dialog.AskDanger(this, Texts.F("st_delete_ask", mods.Count, Gb(size), names), Texts.Get("delete_btn"))) return;
            var ids = mods.Select(m => m.Id).ToList();
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var go = lib.Mods.Where(m => ids.Contains(m.Id)).ToList();
                var (_, bytes, failed) = Ops.DeleteMods(lib, go, Store.GameDir());
                var said = Texts.F("st_deleted_n", go.Count - failed.Count, Texts.Size(bytes));
                return failed.Count == 0 ? said : said + "\n\n" + Texts.F("st_delete_failed", string.Join("\n", failed));
            }));
            ticked.Clear();
            if (r.Ok) { if (r.Out.Contains("\n")) Dialog.Tell(this, r.Out); else Foot(r.Out, "Blue"); }
            else if (!Refused(r)) Dialog.Tell(this, Texts.F("failed", r.Message));
            Refresh();
        }

        /// <summary>The folders older versions were unpacked into, which no mod uses any more:
        /// nothing for a small mod, hundreds of megabytes for an armour pack (the user,
        /// 2026-10-25). Ticked off and deleted here; the game is not touched.</summary>
        void OldVersions()
        {
            var left = st?.Leftovers ?? new List<Leftover>();
            if (left.Count == 0) return;
            Storages.Children.Add(Label("st_old", new Thickness(0, 20, 0, 6)));
            var box = new StackPanel();
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            // one tick for all of them, as in the mod list; "Clear" beside "Select all" read as
            // something else again (the user, 2026-10-25)
            int picked = left.Count(l => oldPick.Contains(l.Path));
            var drop = new Button
            {
                Content = picked > 0 ? Texts.F("st_old_drop_n", picked) : Texts.Get("st_old_drop"), Style = (Style)FindResource("Danger"), MinHeight = 34,
                IsEnabled = picked > 0,
            };
            drop.Click += (s, e) => DropOldVersions();
            DockPanel.SetDock(drop, Dock.Right);
            bar.Children.Add(drop);
            var every = new CheckBox
            {
                Style = (Style)FindResource("Tick"), IsChecked = picked == left.Count, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = Texts.Get("pick_all") + "  ·  " + Texts.F("st_old_n", left.Count, Gb(left.Sum(l => l.Bytes))),
                    FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"),
                },
            };
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var l in left) oldPick.Add(l.Path);
                else foreach (var l in left) oldPick.Remove(l.Path);
                RenderStorage();
            };
            bar.Children.Add(every);
            box.Children.Add(bar);
            if (Texts.Hints) box.Children.Add(Small(Texts.Get("st_old_note"), new Thickness(0, 0, 0, 10)));
            int oldBand = 0;
            foreach (var l in left)
            {
                var path = l.Path;
                var line = new DockPanel();
                var size = new TextBlock
                {
                    Text = Gb(l.Bytes), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 90,
                    TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(size, Dock.Right);
                line.Children.Add(size);
                line.Children.Add(new TextBlock
                {
                    Text = l.Name, FontFamily = Mono, FontSize = 12, Foreground = B("Muted"),
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });
                var tick = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = oldPick.Contains(path), Content = line,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                tick.Click += (s, e) =>
                {
                    if (tick.IsChecked == true) oldPick.Add(path); else oldPick.Remove(path);
                    RenderStorage();
                };
                box.Children.Add(Band(tick, oldBand++));
            }
            Storages.Children.Add(Card(box));
        }

        async void DropOldVersions()
        {
            var go = (st?.Leftovers ?? new List<Leftover>()).Where(l => oldPick.Contains(l.Path)).ToList();
            if (go.Count == 0) return;
            if (!Dialog.AskDanger(this, Texts.F("st_old_ask", go.Count, Gb(go.Sum(l => l.Bytes))), Texts.Get("st_old_drop"))) return;
            var args = new List<string> { "leftovers", "--delete" };
            args.AddRange(go.Select(l => l.Path));
            long bytes = go.Sum(l => l.Bytes);
            oldPick.Clear();
            if (await Act(() => Cli.Run(args.ToArray()), false)) Foot(Texts.F("st_old_done", go.Count, Gb(bytes)), "Blue");
            Refresh();
        }

        /// <summary>One folder as Steam shows a library folder: path, badges, the drive's use, its
        /// mods and size, and what can be done with it.</summary>
        /// <summary>A library folder added, then what it holds offered, as Steam finds the games
        /// in a library folder it is given (the user, 2026-10-25).</summary>
        void AddLibraryFolder(string p)
        {
            var r = Cli.Call(() => Storage.AddFolder(p));
            if (!r.Ok) { Dialog.Tell(this, Texts.F("failed", r.Message)); return; }
            storageSel = r.Out;
            RenderStorage();
            var finds = Storage.Look(r.Out, Library.Load());
            if (finds.Managers.Count > 0 || finds.Loose.Count > 0) ImportFromManagers(false, finds, r.Out);
        }

        /// <summary>The menu at the list's title: the profiles (one is in use), the mod library,
        /// then making, renaming and deleting profiles (the user, 2026-10-25).</summary>
        void Profile_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(ProfileBtn) || st == null) return;
            var menu = new ContextMenu { PlacementTarget = ProfileBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var name in st.Profiles)
            {
                var it = new MenuItem { Header = ProfileName(name), IsChecked = !viewLibrary && name == st.Profile };
                var to = name;
                it.Click += async (s, e2) =>
                {
                    viewLibrary = false;
                    if (to != st.Profile) await Exec("profile-use", to);
                    Refresh();
                };
                menu.Items.Add(it);
            }
            menu.Items.Add(new Separator());
            var lib = new MenuItem { Header = Texts.F("pf_library_n", st.Mods.Count), IsChecked = viewLibrary };
            lib.Click += (s, e2) => { viewLibrary = true; RenderNav(); RenderList(); RenderDetail(); };
            menu.Items.Add(lib);
            menu.Items.Add(new Separator());
            var make = new MenuItem { Header = Texts.Get("pf_new") };
            make.Click += async (s, e2) =>
            {
                var name = Dialog.AskText(this, Texts.Get("pf_new_ask"), Texts.Get("ok"), "");
                if (string.IsNullOrWhiteSpace(name)) return;
                int how = Dialog.Choose(this, Texts.F("pf_new_how", name.Trim()), Texts.Get("pf_copy"), Texts.Get("pf_empty"), Texts.Get("cancel"));
                if (how != 0 && how != 1) return;
                var r = await Exec(how == 1 ? new[] { "profile-new", name.Trim(), "--empty" } : new[] { "profile-new", name.Trim() });
                if (!r.Ok) { Dialog.Tell(this, Texts.F("failed", r.Message)); return; }
                viewLibrary = false;
                await Exec("profile-use", name.Trim());
                Refresh();
            };
            menu.Items.Add(make);
            var rename = new MenuItem { Header = Texts.F("pf_rename", ProfileName(st.Profile)) };
            rename.Click += async (s, e2) =>
            {
                var was = st.Profile;
                var name = Dialog.AskText(this, Texts.Get("pf_rename_ask"), Texts.Get("ok"), ProfileName(was));
                if (string.IsNullOrWhiteSpace(name) || name.Trim() == ProfileName(was)) return;
                var r = await Exec("profile-rename", was, name.Trim());
                if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message));
                Refresh();
            };
            menu.Items.Add(rename);
            var others = st.Profiles.Where(p => p != st.Profile).ToList();
            var drop = new MenuItem { Header = Texts.Get("pf_delete"), IsEnabled = others.Count > 0 };
            drop.Click += async (s, e2) =>
            {
                var pick = Dialog.Ticks(this, Texts.Get("pf_delete_ask"), others.Select(ProfileName).ToArray(), Texts.Get("delete_btn"), false);
                if (pick == null) return;
                for (int i = 0; i < others.Count; i++)
                    if (pick[i]) await Exec("profile-delete", others[i]);
                Refresh();
            };
            menu.Items.Add(drop);
            Dismissable(menu);
            menu.IsOpen = true;
        }

        /// <summary>The row menu's way in and out of the profile in use, for the mods taken.</summary>
        void ProfileItems(ContextMenu menu, List<ModRow> mods)
        {
            if (st == null || !st.ProfilesOn || mods.Count == 0) return;
            var name = ProfileName(st.Profile);
            var outside = mods.Where(x => !x.InProfile).ToList();
            var inside = mods.Where(x => x.InProfile).ToList();
            if (outside.Count > 0)
            {
                var add = new MenuItem { Header = outside.Count == 1 ? Texts.F("pf_add", name) : Texts.F("pf_add_n", name, outside.Count) };
                add.Click += (s, e) => Run(new[] { "profile-add" }.Concat(outside.Select(x => x.Id)).ToArray());
                menu.Items.Add(add);
            }
            if (inside.Count > 0)
            {
                var take = new MenuItem { Header = inside.Count == 1 ? Texts.F("pf_remove", name) : Texts.F("pf_remove_n", name, inside.Count) };
                take.Click += (s, e) => Run(new[] { "profile-remove" }.Concat(inside.Select(x => x.Id)).ToArray());
                menu.Items.Add(take);
            }
            menu.Items.Add(new Separator());
        }

        /// <summary>The game folder on the Storage page: where it is, whether Helldivers 2 is
        /// really there, and changing it, by hand or found in the Steam libraries (the user,
        /// 2026-10-25: changed here, not in the deploy target card).</summary>
        Border GameCard()
        {
            var g = GameDir();
            var box = new StackPanel();
            var top = new DockPanel();
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var change = new Button { Content = Texts.Get("st_game_change"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
            change.Click += (s, e) => { var p = PickGame(); if (p != null) SwitchGame(p); };
            acts.Children.Add(change);
            var find = new Button { Content = Texts.Get("st_game_find"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
            find.Click += (s, e) =>
            {
                var found = Ops.FindGame();
                if (found == null) { Dialog.Tell(this, Texts.Get("st_game_none_found")); return; }
                if (g != null && string.Equals(Paths.Abs(found).TrimEnd('\\'), Paths.Abs(g).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                { Dialog.Tell(this, Texts.F("st_game_same", found)); return; }
                SwitchGame(found);
            };
            acts.Children.Add(find);
            if (g != null && Directory.Exists(g))
            {
                var open = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                open.Click += (s, e) => OpenFolder(g);
                acts.Children.Add(open);
            }
            DockPanel.SetDock(acts, Dock.Right);
            top.Children.Add(acts);
            top.Children.Add(Heading(Texts.Get("st_game")));
            box.Children.Add(top);
            bool ok = g != null && Ops.GameFolderOf(g) != null;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(ok ? "Blue" : "Yellow"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = Texts.Get(g == null ? "st_game_none" : ok ? "st_game_ok" : "st_game_gone"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(line);
            if (g != null) box.Children.Add(new TextBlock { Text = g, FontFamily = Mono, FontSize = 15, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            return new Border { BorderBrush = B(ok ? "Grid" : "Yellow"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = box };
        }

        /// <summary>Deploying to another game folder from now on, after saying what stays in the old one.</summary>
        void SwitchGame(string to)
        {
            var g = GameDir();
            if (g != null && Directory.Exists(g) &&
                Dialog.Choose(this, Texts.F("st_game_switch", to, g), Texts.Get("st_game_switch_go"), Texts.Get("cancel")) != 0) return;
            _ = Act(() => Cli.Run("init", "--game", to), false).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { if (StoragePage.Visibility == Visibility.Visible) RenderStorage(); })));
        }

        /// <summary>A library folder that is somewhere else now: its mods follow it.</summary>
        async void ChangeFolderPath(LibraryFolder f)
        {
            var to = Pick.Folder(this, Texts.Get("st_path_title"), Directory.Exists(f.Path) ? f.Path : IOPath.GetDirectoryName(f.Path));
            if (to == null || string.Equals(Paths.Abs(to).TrimEnd('\\'), Paths.Abs(f.Path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var (followed, missing) = Storage.ChangeFolder(lib, f.Path, to);
                lib.Save();
                return Texts.F("st_path_done", followed, Paths.Abs(to)) + (missing > 0 ? "\n\n" + Texts.F("st_path_missing", missing) : "");
            }));
            if (r.Ok) { storageSel = Paths.Abs(to).TrimEnd('\\'); Dialog.Tell(this, r.Out); }
            else Dialog.Tell(this, Texts.F("failed", r.Message));
            Refresh();
            RenderStorage();
        }

        Border FolderCard(LibraryFolder f)
        {
            bool cur = string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase);
            var panel = new StackPanel();
            var top = new DockPanel();
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (!f.IsDefault)
            {
                var def = new Button { Content = Texts.Get("st_make_default"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                def.Click += (s, e) => { var r = Cli.Call(() => { Storage.SetDefault(f.Path); return ""; }); if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message)); RenderStorage(); };
                acts.Children.Add(def);
            }
            if (Directory.Exists(f.Path))
            {
                var open = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                open.Click += (s, e) => OpenFolder(f.Path);
                acts.Children.Add(open);
            }
            if (f.Registered || f.IsDefault)
            {
                var path = new Button { Content = Texts.Get("st_change_path"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                path.Click += (s, e) => ChangeFolderPath(f);
                acts.Children.Add(path);
            }
            if (f.Registered && !f.IsDefault)
            {
                var off = new Button { Content = Texts.Get("st_remove"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                off.Click += (s, e) => { var r = Cli.Call(() => { Storage.RemoveFolder(f.Path); return ""; }); if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message)); RenderStorage(); };
                acts.Children.Add(off);
            }
            DockPanel.SetDock(acts, Dock.Right);
            top.Children.Add(acts);
            var title = new WrapPanel();
            title.Children.Add(new TextBlock { Text = f.Path, FontFamily = Mono, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 4) });
            void Badge(string text, string fill, string ink)
            {
                title.Children.Add(new Border
                {
                    Background = B(fill), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(ink) },
                });
            }
            if (f.IsDefault) Badge(Texts.Get("st_default"), "Yellow", "TapeInk");
            if (GameDir() != null) Badge(Texts.Get(f.GameDrive ? "st_game_drive" : "st_other_drive"), f.GameDrive ? "Blue" : "Grid", f.GameDrive ? "TapeInk" : "Ink");
            if (f.InAppData) Badge("AppData", "Grid", "Ink");
            top.Children.Add(title);
            panel.Children.Add(top);
            panel.Children.Add(new TextBlock
            {
                Text = Texts.F("st_line", f.Mods.Count, Gb(f.Bytes), f.Drive.TrimEnd('\\'), Gb(f.Free), Gb(f.Total)),
                FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 0, 8),
            });
            // the drive: used by others, by this folder's mods, free
            if (f.Total > 0)
            {
                var barGrid = new Grid { Height = 8 };
                double used = Math.Max(0, f.Total - f.Free - f.Bytes), mods = f.Bytes, free = f.Free;
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(mods, f.Total / 300.0), GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(free, GridUnitType.Star) });
                var a = new Border { Background = B("Muted"), Opacity = 0.5 };
                var b = new Border { Background = B("Yellow") };
                var c = new Border { Background = B("Grid") };
                Grid.SetColumn(b, 1);
                Grid.SetColumn(c, 2);
                barGrid.Children.Add(a);
                barGrid.Children.Add(b);
                barGrid.Children.Add(c);
                panel.Children.Add(barGrid);
            }
            var card = new Border
            {
                BorderBrush = B(cur ? "Yellow" : "Grid"), BorderThickness = new Thickness(cur ? 2 : 1), Background = B("Ground700"),
                Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = panel, Cursor = Cursors.Hand,
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                storageSel = f.Path;
                ticked.Clear();
                RenderStorage();
            };
            return card;
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = VisualTreeHelper.GetParent(d);
            return d as T;
        }

        /// <summary>Mods moved into a folder, after a question that says where to, why a move to the
        /// game's drive is worth it, and whether the old folders go or stay (copy: another manager
        /// may still use them); afterwards, what was moved where.</summary>
        void MoveMods(List<Mod> mods, string target)
        {
            if (mods.Count == 0) return;
            var g = GameDir();
            bool toGameDrive = g != null && string.Equals(IOPath.GetPathRoot(target), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase);
            bool fromOther = g != null && mods.Any(m => !string.Equals(IOPath.GetPathRoot(m.Path), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase));
            var text = Texts.F("mv_ask", mods.Count, target);
            if (toGameDrive && fromOther) text += "\n\n" + Texts.Get("mv_why");
            text += "\n\n" + Texts.Get("mv_how");
            int pick = Dialog.Choose(this, text, Texts.Get("mv_move"), Texts.Get("mv_copy"), Texts.Get("cancel"));
            if (pick != 0 && pick != 1) return;
            bool keep = pick == 1;
            var ids = mods.Select(m => m.Id).ToList();
            string summary = null;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var chosen = lib.Mods.Where(m => ids.Contains(m.Id)).ToList();
                var moved = Storage.Move(lib, chosen, target, keep, g);
                string links = "";
                if (moved.Count > 0 && g != null)
                {
                    try
                    {
                        var r = Hd2Mod.Deploy.Relink(Library.Load(), g);
                        if (r.Linked > 0) links = "\n\n" + Texts.F("mv_linked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                    }
                    catch (ModError) { links = "\n\n" + Texts.Get("mv_link_later"); }
                }
                summary = Texts.F(keep ? "mv_done_copy" : "mv_done_move", moved.Count, target) + "\n\n" +
                          string.Join("\n", moved.Take(12).Select(x => x.Mod.Name + "  →  " + x.To)) + (moved.Count > 12 ? "\n…" : "") + links;
                return Texts.F(keep ? "mv_foot_copy" : "mv_foot_move", moved.Count, target);
            }), true).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                ticked.Clear();
                if (summary != null) Dialog.Tell(this, summary);
                RenderStorage();
            })));
        }

        /// <summary>Other managers' lists into the library (ticked ones; all of them on a first start),
        /// then advice for the mods that landed on another drive than the game.</summary>
        async void ImportFromManagers(bool firstStart, FolderFinds finds = null, string folder = null)
        {
            // from a library folder just added: the lists kept in it and its other mod folders
            var found = finds?.Managers ?? Importers.Found();
            var loose = finds?.Loose ?? new List<string>();
            if (found.Count == 0 && loose.Count == 0) { if (!firstStart) Dialog.Tell(this, Texts.Get("im_none")); return; }
            var items = found.Select(x => Texts.F("im_item", x.Name, x.Mods, x.Where)).ToList();
            if (loose.Count > 0) items.Add(Texts.F("lf_loose", loose.Count));
            var pick = Dialog.Ticks(this, Texts.Get(folder != null ? "lf_ask" : firstStart ? "im_ask_first" : "im_ask"), items.ToArray(), Texts.Get("arsenal_import"));
            if (pick == null || !pick.Any(x => x)) return;
            // managers that keep several profiles: all of them as profiles, or each one's in use (the user, 2026-10-25)
            var several = found.Where((x, k) => pick[k] && x.ProfileNames.Count > 1).ToList();
            bool allProfiles = false;
            if (several.Count > 0)
            {
                var which = string.Join("\n", several.Select(x => Texts.F("im_profiles_line", x.Name, string.Join("、", x.ProfileNames))));
                int how = Dialog.Choose(this, Texts.F("im_profiles_ask", which), Texts.Get("im_profiles_all"), Texts.Get("im_profiles_one"), Texts.Get("cancel"));
                if (how != 0 && how != 1) return;
                allProfiles = how == 0;
            }
            var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
            var lines = new List<string>();
            // where each mod just added came from: 1, 2, ... one per manager (0, already here)
            var group = new Dictionary<string, int>();
            bool arsenalIn = false;
            for (int i = 0; i < found.Count; i++)
            {
                if (!pick[i]) continue;
                var args = new List<string> { "import-" + found[i].Key };
                if (found[i].Dir != null) { args.Add("--dir"); args.Add(found[i].Dir); }
                if (allProfiles && found[i].ProfileNames.Count > 1) args.Add("--profiles");
                var r = await Exec(args.ToArray());
                lines.Add(r.Ok ? Last(r.Out.Split('\n').FirstOrDefault() ?? "") : Texts.F("failed", r.Message));
                foreach (var m in Library.Load().Mods)
                    if (!before.Contains(m.Id) && !group.ContainsKey(m.Id)) group[m.Id] = i + 1;
                if (found[i].Key == "arsenal" && r.Ok) arsenalIn = true;
            }
            if (loose.Count > 0 && pick[found.Count])
            {
                var r = await Exec("add-loose", folder);
                lines.Add(r.Ok ? Last(r.Out) : Texts.F("failed", r.Message));
                foreach (var m in Library.Load().Mods)
                    if (!before.Contains(m.Id) && !group.ContainsKey(m.Id)) group[m.Id] = found.Count + 1;
            }
            Refresh();
            var lib = Library.Load();
            var added = lib.Mods.Where(m => !before.Contains(m.Id)).ToList();
            var said = Texts.F("im_done", added.Count, added.Count(m => m.Enabled));
            // HD2 Arsenal's list came in the other way round: say so, and leave the list's direction alone
            if (arsenalIn && Ops.ArsenalTopWins()) said += "\n\n" + Texts.Get("im_arsenal_top");
            // mods from different managers, and the ones already here, meet for the first time
            int cross = added.Count > 0 && group.Values.Distinct().Count() + (before.Count > 0 ? 1 : 0) > 1 ? Importers.CrossClashes(lib, group) : 0;
            if (cross > 0) said += "\n\n" + Texts.F("im_cross", cross);
            Dialog.Tell(this, said + "\n\n" + Texts.Get("arsenal_only_one"));
            AdviseDrive(added);
        }

        /// <summary>Mods just added that sit on another drive than the game: say what moving them
        /// gives and where they would go; moved, copied, or kept where they are as the player says
        /// (kept: no advice for those folders again).</summary>
        void AdviseDrive(List<Mod> mods)
        {
            var g = GameDir();
            if (g == null) return;
            var kept = KeptDrives();
            var far = mods.Where(m => !string.Equals(IOPath.GetPathRoot(m.Path), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase)
                                      && !kept.Contains(IOPath.GetDirectoryName(m.Path.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)).ToList();
            if (far.Count == 0) return;
            var target = GameDriveTarget(Storage.Folders(Library.Load(), g));
            var drives = string.Join("、", far.Select(m => IOPath.GetPathRoot(m.Path).TrimEnd('\\')).Distinct());
            var text = Texts.F("adv_text", far.Count, drives, IOPath.GetPathRoot(g).TrimEnd('\\')) + "\n\n" + Texts.Get("mv_why") + "\n\n" + Texts.F("adv_where", target);
            int pick = Dialog.Choose(this, text, Texts.Get("adv_move"), Texts.Get("adv_copy"), Texts.Get("adv_keep"));
            if (pick == 0 || pick == 1) MoveModsNow(far, target, pick == 1);
            else
            {
                KeepDrives(far.Select(m => IOPath.GetDirectoryName(m.Path.TrimEnd('\\'))).Distinct());
                Foot(Texts.Get("adv_kept"), "Blue");
            }
        }

        /// <summary>MoveMods without its question (the advice asked already).</summary>
        void MoveModsNow(List<Mod> mods, string target, bool keep)
        {
            var g = GameDir();
            var ids = mods.Select(m => m.Id).ToList();
            string summary = null;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var moved = Storage.Move(lib, lib.Mods.Where(m => ids.Contains(m.Id)).ToList(), target, keep, g);
                string links = "";
                try
                {
                    var r = Hd2Mod.Deploy.Relink(Library.Load(), g);
                    if (r.Linked > 0) links = "\n\n" + Texts.F("mv_linked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                }
                catch (ModError) { links = "\n\n" + Texts.Get("mv_link_later"); }
                summary = Texts.F(keep ? "mv_done_copy" : "mv_done_move", moved.Count, target) + links;
                return Texts.F(keep ? "mv_foot_copy" : "mv_foot_move", moved.Count, target);
            }), true).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (summary != null) Dialog.Tell(this, summary); })));
        }



        /// <summary>The window's pipe: a later start sends a line here, and the window comes forward.</summary>
        void Listen()
        {
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new System.IO.Pipes.NamedPipeServerStream(App.Pipe, System.IO.Pipes.PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var r = new StreamReader(server, System.Text.Encoding.UTF8))
                            {
                                var line = (r.ReadLine() ?? "").Trim();
                                Dispatcher.BeginInvoke(new Action(() => Arrived(line)));
                            }
                        }
                    }
                    catch (IOException) { System.Threading.Thread.Sleep(500); }
                }
            }) { IsBackground = true, Name = "window pipe" };
            t.Start();
        }

        void Arrived(string link)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }


        // ---------------------------------------------------------------- the list

        void RenderList()
        {
            if (dragRow != null) EndDrag(false);         // the rows are about to be built again
            ListTitle.Text = filter == "all" ? Texts.Get("t_all") : Texts.Get("f_" + filter);
            bool profiled = st.ProfilesOn;
            if (!profiled) viewLibrary = false;
            ListTitle.Visibility = profiled ? Visibility.Collapsed : Visibility.Visible;
            ProfileBtn.Visibility = profiled ? Visibility.Visible : Visibility.Collapsed;
            if (profiled)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = viewLibrary ? Texts.Get("pf_library") : ProfileName(st.Profile), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new Path
                {
                    Data = Geometry.Parse("M0,0 L7,7 L14,0"), Stroke = B("Yellow"), StrokeThickness = 2.2,
                    Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                });
                ProfileBtn.Template = Plain();
                ProfileBtn.Content = head;
            }
            // a row is built again only when what it shows changed; the others stay put
            var want = new List<UIElement>();
            var seen = new HashSet<string>();
            foreach (var m in Seen())
            {
                var sig = Sig(m);
                if (!rows.TryGetValue(m.Id, out var had) || had.Sig != sig) rows[m.Id] = had = (sig, Row(m));
                want.Add(had.Row);
                seen.Add(m.Id);
            }
            foreach (var gone in rows.Keys.Where(k => !seen.Contains(k)).ToList()) rows.Remove(gone);
            picked.RemoveWhere(id => !seen.Contains(id));
            if (want.Count == 0 && searchWords.Length > 0)
            {
                noMatch = noMatch ?? new TextBlock { Foreground = B("Muted"), Margin = new Thickness(20) };
                noMatch.Text = Texts.Get("search_none");
                want.Add(noMatch);
            }
            var keep = new HashSet<UIElement>(want);
            for (int i = 0; i < want.Count; i++)
            {
                if (i < List.Children.Count && List.Children[i] == want[i]) continue;
                if (i < List.Children.Count && !keep.Contains(List.Children[i])) List.Children.RemoveAt(i);
                if (List.Children.Contains(want[i])) List.Children.Remove(want[i]);
                List.Children.Insert(i, want[i]);
            }
            while (List.Children.Count > want.Count) List.Children.RemoveAt(List.Children.Count - 1);
        }

        readonly Dictionary<string, (string Sig, Border Row)> rows = new Dictionary<string, (string, Border)>();

        /// <summary>Everything a row shows or acts on, as one string.</summary>
        string Sig(ModRow m)
        {
            var sb = new System.Text.StringBuilder();
            void Add(object x) => sb.Append(x).Append('\u001f');
            Add(m.Id == selectedId); Add(picked.Contains(m.Id)); Add(open.Contains(m.Id)); Add(filter == "all");
            Add(m.InProfile); Add(viewLibrary); Add(st.Profile);
            Add(m.Index); Add(m.Name); Add(m.Path); Add(m.Enabled); Add(m.Icon); Add(m.Status); Add(m.Error); Add(m.Lua); Add(m.Loader); Add(string.Join(",", m.Tags)); Add(HasUpdate(m)); Add(m.ShownVersion); Add(m.VersionFrom);
            Add(Checked(m)?.Status); Add(checkingOne.Contains(m.Id)); Add(checking); Add(updated.Contains(m.Id));
            Add(m.Files.Count); Add(m.DeployBytes); Add(m.LibraryBytes); Add(m.Changes); Add(m.Conflicts); Add(m.Shared); Add(m.Chosen); Add(m.Choices);
            if (open.Contains(m.Id))
                foreach (var o in m.Options)
                {
                    Add(o.Name); Add(o.Enabled); Add(o.Icon); Add(o.Files); Add(o.Bytes); Add(o.Description);
                    foreach (var s in o.Subs) { Add(s.Name); Add(s.Enabled); Add(s.Icon); Add(s.Files); Add(s.Bytes); Add(s.Description); }
                }
            return sb.ToString();
        }

        Border Row(ModRow m)
        {
            bool cur = m.Id == selectedId, isOpen = open.Contains(m.Id), alsoTaken = picked.Contains(m.Id);
            var item = new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Tag = m, AllowDrop = filter == "all" && !viewLibrary,
                Background = cur || alsoTaken ? B("Ground900") : Brushes.Transparent,
            };
            var edge = new Border
            {
                // yellow is the row on its own; in a group every row taken is blue, the one whose
                // details are shown among them, so what is marked is what will be acted on
                BorderThickness = new Thickness(cur ? 4 : 2, 0, 0, 0),
                BorderBrush = alsoTaken ? B("Blue") : cur ? B("Yellow") : Brushes.Transparent,
            };
            item.Child = edge;
            var col = new StackPanel();
            edge.Child = col;

            var line = new DockPanel { Margin = new Thickness(4, 8, 16, 8) };
            col.Children.Add(line);
            // the load order's number
            // a mod outside the profile in use has no place in its load order
            var num = new TextBlock { Text = st.ProfilesOn && !m.InProfile ? "" : m.Index.ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(num, Dock.Left);
            line.Children.Add(num);
            // open / close
            var chev = new Button { Width = 36, Height = 44, Cursor = Cursors.Hand, Template = Plain(), ToolTip = Texts.Get(isOpen ? "collapse" : "expand"), FocusVisualStyle = null };
            chev.Content = new Path
            {
                Data = Geometry.Parse("M9 5l7 7-7 7"), Stroke = B(isOpen ? "Yellow" : "Muted"), StrokeThickness = 2.5, Width = 16, Height = 16, Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(isOpen ? 90 : 0), Margin = new Thickness(10),
            };
            chev.Click += (s, e) => { if (!open.Remove(m.Id)) open.Add(m.Id); Current(m.Id); RenderList(); RenderDetail(); };
            DockPanel.SetDock(chev, Dock.Left);
            line.Children.Add(chev);
            // on / off
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = m.Name };
            sw.Click += (s, e) =>
            {
                bool on = sw.IsChecked == true;
                // turned on from the library: it goes into the profile in use, on
                if (on && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                Change(m.Id, c => c.Enabled = on, on ? "enable" : "disable", m.Id);
            };
            // outside the profile in use (the library on show): faint, its switch says it is off
            if (st.ProfilesOn && !m.InProfile) { item.Opacity = 0.55; sw.ToolTip = Texts.Get("pf_outside"); }
            DockPanel.SetDock(sw, Dock.Left);
            line.Children.Add(sw);
            switches[m.Id] = sw;
            // the right column: size, then how it is deployed (the file count is a
            // detail nobody plays with: it stays on the mod's own page)
            var how = new StackPanel { Width = 104, VerticalAlignment = VerticalAlignment.Center };
            how.Children.Add(new TextBlock { Text = Texts.Size(m.DeployBytes > 0 ? m.DeployBytes : m.LibraryBytes), FontFamily = Mono, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
            var (stText, stColour) = StatusText(m);
            how.Children.Add(new TextBlock { Text = stText, FontFamily = Mono, FontSize = 12, Foreground = B(stColour), HorizontalAlignment = HorizontalAlignment.Right });
            DockPanel.SetDock(how, Dock.Right);
            line.Children.Add(how);
            // this one mod's update check, where its file count used to be (the user,
            // 2026-10-24): one small mark, not a button the size of the row. It keeps the
            // colour of the last answer, which its tip spells out
            bool asking = checkingOne.Contains(m.Id);
            var said = Checked(m);
            bool newer = said?.Status == "update", done = updated.Contains(m.Id) || said?.Status == "current";
            var ask = new Button
            {
                Width = 36, Height = 36, Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null,
                IsEnabled = !asking && !checking, Opacity = asking ? 0.4 : 1, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = newer ? Texts.F("up_row_get", said.Latest ?? "")
                        : done ? Texts.Get(updated.Contains(m.Id) ? "up_row_done" : "up_row_current") + " · " + Texts.Get("up_check_row_tip")
                        : said == null ? Texts.Get("up_check_row_tip")
                        : Texts.Get("up_row_" + said.Status) + " · " + Texts.Get("up_check_row_tip"),
                Content = new Path
                {
                    Data = Geometry.Parse(newer ? GlyphDown : done ? GlyphTick : GlyphCheck),
                    Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(9),
                    Stroke = B(newer ? "Yellow" : done ? "Blue" : "Muted"),
                    StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
                },
            };
            // the mark is the whole thing: it checks, and then installs what the check found
            ask.Click += (s, e) =>
            {
                // the mod checked is the one shown: its result is in the details, and showing
                // another mod's left it out of sight until the row was clicked (the user, 2026-10-25)
                if (selectedId != m.Id)
                {
                    Current(m.Id);
                    RenderList();
                    RenderDetail();
                }
                if (newer) ApplyUpdate(m, said); else CheckOne(m);
            };
            DockPanel.SetDock(ask, Dock.Right);
            line.Children.Add(ask);
            // the picture, the name and what is chosen
            var pick = new Button { Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Stretch, Opacity = m.Enabled ? 1 : 0.55, Foreground = B("Ink") };
            var who = new DockPanel { MinHeight = 56 };
            var pic = Picture(m.Icon, GlyphMod, 48, m.Enabled ? "Blue" : "Muted");
            DockPanel.SetDock(pic, Dock.Left);
            who.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            // the name, with nothing in front of it
            var nameLine = new DockPanel();
            // a newer version on its channel, after an update check
            if (HasUpdate(m))
            {
                var tag = new Border
                {
                    Background = B("Yellow"), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.F("up_tag_tip", updates[m.Id].Channel),
                    Child = new TextBlock { Text = Texts.Get("up_tag"), FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") },
                };
                DockPanel.SetDock(tag, Dock.Left);
                nameLine.Children.Add(tag);
            }
            nameLine.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Name });
            names.Children.Add(nameLine);
            // the line under the name: everything on it shares one centre, the LUA tag as
            // much as the words (the user, 2026-10-24)
            var meta = new WrapPanel();
            TextBlock Fact(string text, string colour, string tip = null) => new TextBlock
            {
                Text = text, FontSize = 12, Foreground = B(colour), ToolTip = tip,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            if (m.ShownVersion != null)
            {
                var ver = Fact(V(m.ShownVersion), "Ink", VersionTip(m));
                ver.FontFamily = Mono;
                meta.Children.Add(ver);
            }
            meta.Children.Add(Fact(m.Options.Count > 0 ? Texts.F("opts_some", m.Chosen, m.Choices) : Texts.Get("opts_none"), "Muted"));
            // a LUA tag when the mod ships a script (any option of it): here, beside the
            // other facts about the mod, not in front of its name
            // the player's own labels first, in their own colour
            foreach (var t in m.Tags)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Ink"), BorderThickness = new Thickness(1), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("tag_tip"),
                    Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center },
                });
            // a loader replaces one of the game's own scripts: its own colour, because two
            // loaders cannot both have their way (the user, 2026-10-24)
            if (m.Loader)
                meta.Children.Add(new Border
                {
                    Background = B("Ink"), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("loader_tip"),
                    Child = new TextBlock
                    {
                        Text = Texts.Get("loader_tag"), FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("TapeInk"),
                        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                    },
                });
            if (m.Lua)
                meta.Children.Add(new Border
                {
                    // the box is taller than the words it stands beside, so it is nudged up by
                    // its own overhang: the ink of both then shares one middle
                    BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("lua_tip"),
                    Child = new TextBlock
                    {
                        Text = "LUA", FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Blue"),
                        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                    },
                });
            if (m.Conflicts > 0) meta.Children.Add(Fact(Texts.F("row_clash", m.Conflicts), "Yellow"));
            // the same bytes in two mods are only worth a mention (the user, 2026-10-24)
            if (m.Shared > 0) meta.Children.Add(Fact(Texts.F("row_same", m.Shared), "Muted", Texts.Get("same_tip")));
            if (m.Error != null) meta.Children.Add(new TextBlock { Text = Texts.Get("broken"), FontSize = 12, Foreground = B("Yellow") });
            names.Children.Add(meta);
            who.Children.Add(names);
            pick.Content = who;
            pick.Click += (s, e) => Take(m);
            // a name of the player's own: two clicks on the mod (the user, 2026-10-24)
            pick.PreviewMouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { e.Handled = true; dragFrom = null; RenameMod(m); } };
            line.Children.Add(pick);

            if (isOpen) col.Children.Add(Options(m));

            // dragging changes the load order (in the full list only, not the library's)
            if (filter == "all" && !viewLibrary)
            {
                item.PreviewMouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(ListArea); dragFrom = m; };
                item.PreviewMouseMove += (s, e) =>
                {
                    if (dragFrom != m || dragRow != null || e.LeftButton != MouseButtonState.Pressed) return;
                    if (Math.Abs(e.GetPosition(ListArea).Y - dragStart.Y) < 6) return;
                    BeginDrag(m, item, e.GetPosition(ListArea), e.GetPosition(item).Y);
                };
            }
            item.PreviewMouseLeftButtonUp += (s, e) => dragFrom = null;
            // built when it opens, not with the row: rows are kept while what they show does not
            // change, and a menu built with one held the labels there were then -- a label made
            // from another row's menu was missing until something drew this row again (the user,
            // 2026-10-25). The mod is looked up again too, as the row may be from an older read.
            item.ContextMenu = Menu(m);
            var id = m.Id;
            item.ContextMenuOpening += (s, e) => item.ContextMenu = Menu(st?.Mods.FirstOrDefault(x => x.Id == id) ?? m);
            return item;
        }

        /// <summary>A row clicked: on its own it is the one shown; with Ctrl it joins or leaves
        /// the mods taken; with Shift every row from the one shown down to it is taken.</summary>
        void Take(ModRow m)
        {
            var mods = Seen().ToList();
            var keys = Keyboard.Modifiers;
            if ((keys & ModifierKeys.Shift) != 0 && selectedId != null)
            {
                int from = mods.FindIndex(x => x.Id == selectedId), to = mods.FindIndex(x => x.Id == m.Id);
                if (from >= 0 && to >= 0)
                {
                    picked.Clear();
                    for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) picked.Add(mods[i].Id);
                }
            }
            else if ((keys & ModifierKeys.Control) != 0)
            {
                if (!picked.Remove(m.Id))
                {
                    if (picked.Count == 0 && selectedId != null) picked.Add(selectedId);
                    picked.Add(m.Id);
                    selectedId = m.Id;
                }
            }
            else
            {
                picked.Clear();
                if (selectedId == m.Id) { RenderList(); return; }
                selectedId = m.Id;
                descOpen = false;
            }
            if (picked.Count == 1) picked.Clear();
            RenderList();
            RenderDetail();
        }

        /// <summary>The row whose details are shown, moved by something other than a click on
        /// the list: the chevron, a drag, a download, a jump to a mod with an update. A row taken
        /// up this way is not part of a group that was picked before it, and leaving the group
        /// behind it would show three rows marked and act on two (the user, 2026-10-25).</summary>
        void Current(string id)
        {
            if (id != null && !picked.Contains(id)) picked.Clear();
            selectedId = id;
        }

        /// <summary>The mods taken together, in load order, when this one is among them.</summary>
        List<ModRow> Taken(ModRow m) =>
            picked.Count > 1 && picked.Contains(m.Id) ? st.Mods.Where(x => picked.Contains(x.Id)).ToList() : new List<ModRow> { m };

        void ClearDrop()
        {
            if (dropRow == null) return;
            dropRow.BorderThickness = new Thickness(0, 0, 0, 1);
            dropRow.BorderBrush = B("Grid");
            dropRow = null;
        }

        // ---------------------------------------------------------------- dragging a row

        // While a row is dragged it follows the pointer: the row itself stays where it is,
        // dimmed, and its picture is carried on DragLayer (the user, 2026-10-24: the row has
        // to follow the pointer, not jump only once the button is let go). The mouse is this
        // window's meanwhile, so no OLE drag runs and dropping files in still works.
        Border dragRow;          // the row being dragged
        ModRow dragMod;          // the mod it is
        UIElement ghost;         // its picture, under the pointer
        double grabY;            // where inside the row the pointer took hold
        int dropTo;              // the place it would land, 1-based; 0 = where it already is
        double edgeBy;           // pixels per tick while the pointer is at the list's edge
        readonly DispatcherTimer edgeScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        /// <summary>The row's picture, taken before it is dimmed.</summary>
        UIElement Ghost(Border row)
        {
            double w = row.ActualWidth, h = row.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(row);
            var drawn = new DrawingVisual();
            using (var dc = drawn.RenderOpen())
                dc.DrawRectangle(new VisualBrush(row), null, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(drawn);
            bmp.Freeze();
            // the ghost must hide the row it is over, so its own ground is solid: the window's
            // is see-through while the acrylic backdrop is on
            var ground = (B("Ground700") as SolidColorBrush)?.Color ?? Colors.Black;
            ground.A = 0xFF;
            return new Border
            {
                Width = w, Height = h, Background = new SolidColorBrush(ground),
                BorderBrush = B("Yellow"), BorderThickness = new Thickness(1),
                Child = new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill },
            };
        }

        void BeginDrag(ModRow m, Border row, Point at, double inRow, bool grab = true)
        {
            if (row.ActualWidth < 1 || row.ActualHeight < 1) return;
            dragFrom = null;
            dragRow = row;
            dragMod = m;
            grabY = inRow;
            ghost = Ghost(row);
            DragLayer.Children.Add(ghost);
            row.Opacity = 0.3;
            if (grab) Mouse.Capture(ListArea, CaptureMode.SubTree);
            DragAt(at);
        }

        /// <summary>The picture put under the pointer, the place it would land marked, and the
        /// list scrolled on while the pointer rests against its top or bottom edge.</summary>
        void DragAt(Point at)
        {
            if (ghost == null) return;
            double h = ((FrameworkElement)ghost).Height;
            Canvas.SetLeft(ghost, 1);
            Canvas.SetTop(ghost, Math.Max(0, Math.Min(ListArea.ActualHeight - h, at.Y - grabY)));
            // the row the pointer is over, and which side of its middle
            Border over = null;
            ModRow onto = null;
            bool below = false;
            foreach (var child in List.Children)
            {
                if (!(child is Border r) || !(r.Tag is ModRow x)) continue;
                double top = r.TranslatePoint(new Point(0, 0), ListArea).Y;
                onto = x; over = r; below = at.Y > top + r.ActualHeight / 2;
                if (at.Y < top + r.ActualHeight) break;
            }
            int to = 0;
            if (onto != null && onto != dragMod)
            {
                // below on screen is later in the load order, unless the list is shown the other way round
                bool later = Texts.TopWins ? !below : below;
                to = later ? onto.Index + 1 : onto.Index;
                if (dragMod.Index < to) to -= 1;
            }
            dropTo = to == dragMod.Index ? 0 : to;
            if (dropTo == 0 || over == null) ClearDrop();
            else
            {
                ClearDrop();
                dropRow = over;
                over.BorderThickness = below ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 1);
                over.BorderBrush = B("Yellow");
            }
            edgeBy = at.Y < 40 ? -10 : at.Y > ListArea.ActualHeight - 40 ? 10 : 0;
            if (edgeBy == 0) edgeScroll.Stop();
            else if (!edgeScroll.IsEnabled) edgeScroll.Start();
        }

        /// <summary>The drag over: the row put where it was let go (commit), or left alone.</summary>
        void EndDrag(bool commit)
        {
            if (dragRow == null) return;
            edgeScroll.Stop();
            dragRow.Opacity = 1;
            if (ghost != null) DragLayer.Children.Remove(ghost);
            ghost = null;
            var moving = dragMod;
            int to = dropTo;
            dragRow = null; dragMod = null; dragFrom = null; dropTo = 0;
            ClearDrop();
            if (Mouse.Captured == ListArea) Mouse.Capture(null);
            if (!commit || moving == null || to == 0 || to == moving.Index) return;
            Current(moving.Id);
            Change(moving.Id, c =>
            {
                st.Mods.Remove(c);
                st.Mods.Insert(to - 1, c);
                for (int k = 0; k < st.Mods.Count; k++) st.Mods[k].Index = k + 1;
            }, "move", moving.Id, "--to", to.ToString());
        }

        static (string, string) StatusText(ModRow m)
        {
            switch (m.Status)
            {
                case "broken": return (Texts.Get("st_broken"), "Yellow");
                case "off": return (Texts.Get("st_off"), "Muted");
                case "pending": return (Texts.Get("st_pending"), "Yellow");
                case "link": return (Texts.Get("st_link"), "Blue");
                case "copy": return (Texts.Get("st_copy"), "Muted");
                case "partial": return (Texts.Get("st_partial"), "Blue");
                default: return (Texts.Get("st_keep"), "Muted");
            }
        }

        /// <summary>An open mod's options, as the board's cards: a manifest option with
        /// sub-options is a group (pick one; Ctrl+click for more), the options without
        /// any are one group of parts that can each be on or off.</summary>
        UIElement Options(ModRow m)
        {
            var box = new StackPanel { Margin = new Thickness(72, 4, 16, 16) };
            if (m.Options.Count == 0)
            {
                box.Children.Add(new TextBlock { Text = Texts.Get("no_options"), FontSize = 12, Foreground = B("Muted") });
                return box;
            }
            var parts = new List<(Option, int)>();
            for (int i = 0; i < m.Options.Count; i++)
            {
                var o = m.Options[i];
                string oref = (i + 1).ToString();
                if (o.Subs.Count == 0) { parts.Add((o, i)); continue; }
                var head = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
                var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = o.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                int oi = i;
                sw.Click += (s, e) =>
                {
                    bool on = sw.IsChecked == true;
                    Change(m.Id, c => c.Options[oi].Enabled = on, "set", m.Id, oref, on ? "on" : "off");
                };
                DockPanel.SetDock(sw, Dock.Left);
                head.Children.Add(sw);
                var t = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = o.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.Get("rule_one"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                head.Children.Add(t);
                box.Children.Add(head);
                if (!string.IsNullOrWhiteSpace(o.Description)) box.Children.Add(new TextBlock { Text = o.Description, FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) });
                var cards = new WrapPanel { IsEnabled = o.Enabled, Opacity = o.Enabled ? 1 : 0.45 };
                for (int j = 0; j < o.Subs.Count; j++)
                {
                    var sub = o.Subs[j];
                    int sj = j;
                    string sref = (i + 1) + "." + (j + 1);
                    cards.Children.Add(Card(sub.Name, sub.Icon, sub.Files, sub.Bytes, sub.Enabled, sub.Description, () =>
                    {
                        bool was = sub.Enabled;
                        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                            Change(m.Id, c => c.Options[oi].Subs[sj].Enabled = !was, "set", m.Id, sref, was ? "off" : "on");
                        else if (!was)
                            Change(m.Id, c => { for (int k = 0; k < c.Options[oi].Subs.Count; k++) c.Options[oi].Subs[k].Enabled = k == sj; },
                                   "set", m.Id, sref, "on", "--only");
                    }));
                }
                box.Children.Add(cards);
            }
            if (parts.Count > 0)
            {
                var t = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
                t.Children.Add(new TextBlock { Text = Texts.Get("extras"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.F("rule_any", parts.Count(p => p.Item1.Enabled), parts.Count), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                box.Children.Add(t);
                var cards = new WrapPanel();
                foreach (var (o, i) in parts)
                {
                    string oref = (i + 1).ToString();
                    cards.Children.Add(Card(o.Name, o.Icon, o.Files, o.Bytes, o.Enabled, o.Description, () =>
                    {
                        bool was = o.Enabled;
                        Change(m.Id, c => c.Options[i].Enabled = !was, "set", m.Id, oref, was ? "off" : "on");
                    }));
                }
                box.Children.Add(cards);
            }
            var chosenFiles = m.Files.Count;
            box.Children.Add(new TextBlock
            {
                Text = Texts.F("opt_foot", m.Chosen, Texts.N(chosenFiles), Texts.Size(m.DeployBytes)),
                FontFamily = Mono, FontSize = 12, Foreground = B("Blue"), Margin = new Thickness(0, 14, 0, 0),
            });
            return box;
        }

        Button Card(string name, string icon, int files, long bytes, bool on, string description, Action act)
        {
            // a picture when the mod has one; else only the words, all of them (the name and
            // the author's description wrap; nothing is cut or left to a tooltip)
            bool hasPicture = LoadImage(icon) != null;
            var b = new Button { Width = hasPicture ? 140 : 200, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Top };
            var frame = new Border { BorderBrush = on ? B("Yellow") : B("Grid"), BorderThickness = new Thickness(2), Background = B("Ground900"), Padding = new Thickness(8) };
            var grid = new Grid();
            var col = new StackPanel();
            if (hasPicture)
                col.Children.Add(new Border
                {
                    Height = 120, BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Opacity = on ? 1 : 0.6,
                    Child = Picture(icon, GlyphPart, 0, on ? "Yellow" : "Muted", 56),
                });
            // the tick sits top right: words beside it keep clear of it
            var right = hasPicture ? 0 : 26;
            col.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, hasPicture ? 8 : 0, right, 2) });
            if (!string.IsNullOrWhiteSpace(description))
                col.Children.Add(new TextBlock { Text = description.Trim(), FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, hasPicture ? 0 : right, 4) });
            col.Children.Add(new TextBlock { Text = files > 0 ? Texts.F("card_meta", files, Texts.Size(bytes)) : Texts.Get("card_vanilla"), FontFamily = Mono, FontSize = 11, Foreground = B("Muted") });
            grid.Children.Add(col);
            if (on)
                grid.Children.Add(new Border
                {
                    Width = 22, Height = 22, Background = B("Yellow"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0),
                    Child = new Path { Data = Geometry.Parse("M5 12l5 5L20 7"), Stroke = B("TapeInk"), StrokeThickness = 3.5, Width = 14, Height = 14, Stretch = Stretch.Uniform },
                });
            frame.Child = grid;
            b.Content = frame;
            b.Click += (s, e) => act();
            return b;
        }

        // ---------------------------------------------------------------- the chosen mod

        void RenderDetail()
        {
            Detail.Children.Clear();
            var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
            if (m == null) return;

            // the picture, the name and its files
            var head = new DockPanel();
            var pic = Picture(m.Icon, GlyphMod, 72, m.Enabled ? "Blue" : "Muted", 44);
            DockPanel.SetDock(pic, Dock.Left);
            head.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            names.Children.Add(new TextBlock { Text = m.Name, FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            // a name the player gave it: the one it came with is kept beneath (the user, 2026-10-24)
            if (m.SiteAuthor != null)
                names.Children.Add(new TextBlock { Text = Texts.F("by_author", m.SiteAuthor), FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            if (m.RealName != null)
                names.Children.Add(new TextBlock
                {
                    Text = Texts.F("real_name", m.RealName), FontSize = 12, Foreground = B("Muted"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), ToolTip = Texts.Get("real_name_tip"),
                });
            names.Children.Add(new TextBlock { Text = m.ShownVersion != null ? V(m.ShownVersion) : Texts.Get("ver_none"), FontFamily = Mono, FontSize = 14,
                                               Foreground = B(m.ShownVersion != null ? "Ink" : "Muted"), ToolTip = VersionTip(m), Margin = new Thickness(0, 2, 0, 2) });
            names.Children.Add(new TextBlock { Text = Texts.F("n_files", Texts.N(m.Files.Count)) + " · " + Texts.Size(m.DeployBytes), FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold });
            head.Children.Add(names);
            Detail.Children.Add(head);

            // the manifest, or the site's own words when the mod folder says nothing
            bool fromSite = string.IsNullOrWhiteSpace(m.Description) && !string.IsNullOrWhiteSpace(m.SiteSummary);
            var man = new StackPanel();
            man.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, Background = B("Yellow"), Padding = new Thickness(8, 0, 8, 0),
                Child = new TextBlock { Text = Texts.Get(fromSite ? "site_words" : "manifest"), FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") },
            });
            string desc = fromSite ? m.SiteSummary.Trim() : string.IsNullOrWhiteSpace(m.Description) ? Texts.Get("no_desc") : m.Description.Trim();
            bool longDesc = desc.Length > 90 || desc.Contains("\n");
            var dt = new TextBlock { Text = desc, Foreground = B(string.IsNullOrWhiteSpace(desc) || (!fromSite && string.IsNullOrWhiteSpace(m.Description)) ? "Muted" : "Ink"), TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 12, 0, 0) };
            if (longDesc && !descOpen) { dt.MaxHeight = 42; dt.TextTrimming = TextTrimming.WordEllipsis; }
            man.Children.Add(dt);
            if (longDesc)
            {
                var more = new Button { Content = Texts.Get(descOpen ? "less" : "more"), Template = Plain(), Foreground = B("Blue"), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                more.Click += (s, e) => { descOpen = !descOpen; RenderDetail(); };
                man.Children.Add(more);
            }
            if (m.Error != null) man.Children.Add(new TextBlock { Text = Texts.Get("broken") + ": " + m.Error, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            Detail.Children.Add(Panel(man, new Thickness(0, 20, 0, 0)));

            // the facts
            var facts = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Fact(string key, string value, bool mono = false)
            {
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var k = new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 3, 16, 3) };
                Grid.SetRow(k, r);
                facts.Children.Add(k);
                var v = new TextBlock { Text = value, FontSize = 12, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), ToolTip = value };
                if (mono) v.FontFamily = Mono;
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                facts.Children.Add(v);
            }
            if (st.ProfilesOn && !m.InProfile) Fact("d_order", Texts.Get("pf_outside"));
            else Fact("d_order", Texts.F("d_order_v", m.Index, st.ProfilesOn ? st.Mods.Count(x => x.InProfile) : st.Mods.Count), true);
            Fact("d_opts", m.Options.Count > 0 ? m.Chosen + " / " + m.Choices : "—");
            Fact("d_files", Texts.N(m.Files.Count), true);
            Fact("d_folder", m.Path, true);
            {
                // remove from the library, and open the folder
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 6) };
                var removeBtn = new Button { Content = Texts.Get("remove_btn"), Style = (Style)FindResource("Ghost"), MinHeight = 32 };
                removeBtn.Click += (s, e) => { if (Dialog.Ask(this, Texts.F("remove_ask", m.Name), Texts.Get("remove_btn"))) Run("remove", m.Id); };
                // red: deleting from the disk cannot be undone
                var deleteBtn = new Button { Content = Texts.Get("delete_btn"), Style = (Style)FindResource("Danger"), MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
                deleteBtn.Click += (s, e) => DeleteFromDisk(m);
                acts.Children.Add(deleteBtn);
                acts.Children.Add(removeBtn);
                if (m.Exists)
                {
                    var openBtn = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                    openBtn.Click += (s, e) => OpenFolder(m.Path);
                    acts.Children.Add(openBtn);
                }
                Grid.SetRow(acts, r);
                Grid.SetColumnSpan(acts, 2);      // the whole row: three buttons do not fit the value column
                facts.Children.Add(acts);
            }
            if (!string.IsNullOrEmpty(m.Source) && m.Source != m.Path) Fact("d_source", m.Source, true);
            Detail.Children.Add(facts);
            Detail.Children.Add(UpdatePanel(m));

            // what a deploy does to it
            if (m.Enabled && m.Changes > 0) Detail.Children.Add(Changes(m));
            else
            {
                var ok = new StackPanel();
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(m.Enabled ? "Blue" : "Muted"), Margin = new Thickness(0, 0, 8, 0) });
                line.Children.Add(new TextBlock { Text = Texts.Get(m.Enabled ? "in_sync" : "is_off"), FontWeight = FontWeights.SemiBold });
                ok.Children.Add(line);
                ok.Children.Add(Small(m.Enabled ? Texts.F("in_sync_n", Texts.N(m.Files.Count)) : Texts.Get("is_off_n"), new Thickness(0, 8, 0, 0)));
                Detail.Children.Add(Panel(ok, new Thickness(0, 20, 0, 0)));
            }

            // conflicts, and below them the resources another mod ships identically: those
            // are said plainly, because nothing is overridden by them
            foreach (var c in st.Clashes.Where(c => c.Mods.Contains(m.Name)).OrderBy(c => c.Same))
            {
                var box = new StackPanel();
                var others = string.Join(" / ", c.Mods.Where(x => x != m.Name));
                box.Children.Add(new TextBlock
                {
                    Text = Texts.F(c.Same ? "same" : "clash", c.Count, others), FontWeight = FontWeights.SemiBold,
                    Foreground = B(c.Same ? "Ink" : "Yellow"), TextWrapping = TextWrapping.Wrap,
                });
                box.Children.Add(Small(c.Same ? Texts.Get("same_tip") : Texts.F("clash_win", c.Winner), new Thickness(0, 6, 0, 0)));
                // which resources (the user, 2026-10-25): type, then the name or the id
                const int lines = 8;
                var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var r in c.Resources.Take(lines))
                {
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 1, 0, 1) };
                    tb.Inlines.Add(new System.Windows.Documents.Run(TypeLabel(r.Type) + "  ·  ") { Foreground = B("Muted") });
                    if (r.Name != null) tb.Inlines.Add(new System.Windows.Documents.Run(r.Name) { Foreground = B("Ink") });
                    else tb.Inlines.Add(new System.Windows.Documents.Run(r.Id) { Foreground = B("Muted"), FontFamily = Mono, FontSize = 12 });
                    list.Children.Add(tb);
                }
                if (c.Count > Math.Min(lines, c.Resources.Count))
                    list.Children.Add(Small(Texts.F("clash_more", c.Count - Math.Min(lines, c.Resources.Count)), new Thickness(0, 2, 0, 0)));
                if (list.Children.Count > 0) box.Children.Add(list);
                if (!st.ResourceNames && c.Resources.Count > 0 && Texts.Hints)
                    box.Children.Add(Small(Texts.Get("clash_no_names"), new Thickness(0, 8, 0, 0)));
                Detail.Children.Add(new Border { BorderBrush = B(c.Same ? "Grid" : "Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 16, 0, 0), Child = box });
            }

            // how it is deployed
            var dep = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            dep.Children.Add(Label("deploy_info", new Thickness(0, 0, 0, 6)));
            void Pair(string key, string value, string colour = "Ink", bool mono = false)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var v = new TextBlock { Text = value, FontSize = 12, Foreground = B(colour) };
                if (mono) v.FontFamily = Mono;
                DockPanel.SetDock(v, Dock.Right);
                dp.Children.Add(v);
                dp.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                dep.Children.Add(dp);
            }
            string mode = !m.Enabled ? "mode_off" : m.Changes > 0 ? "mode_pending" : m.Status == "link" ? "mode_link" : m.Status == "partial" ? "mode_partial" : m.Files.Count == 0 ? "mode_off" : "mode_copy";
            Pair("di_mode", Texts.Get(mode));
            Pair("di_files", m.Enabled ? Texts.N(m.Files.Count(f => f.Action == "keep")) : "0", "Ink", true);
            Pair("di_lib", Texts.Size(m.LibraryBytes), "Ink", true);
            long extra = m.Enabled ? m.Files.Where(f => f.Action == "keep" && !f.Linked).Sum(f => f.Bytes) : 0;
            Pair("di_extra", m.Enabled ? Texts.Size(extra) : "—", extra == 0 ? "Blue" : "Ink", true);
            Detail.Children.Add(dep);
        }

        /// <summary>A mod's part of the next deploy: the board's update section, made of
        /// what hd2qd will keep, rename, write and delete.</summary>
        UIElement Changes(ModRow m)
        {
            var box = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            long keep = m.Files.Where(f => f.Action == "keep").Sum(f => f.Bytes);
            long move = m.Files.Where(f => f.Action == "move").Sum(f => f.Bytes);
            long write = m.Files.Where(f => f.Action == "write").Sum(f => f.Bytes);
            int moved = m.Files.Count(f => f.Action == "move"), written = m.Files.Count(f => f.Action == "write"), kept = m.Files.Count(f => f.Action == "keep");
            box.Children.Add(new TextBlock { Text = Texts.Get("changes"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Yellow") });

            // by size
            var head = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
            var total = new TextBlock { Text = Texts.Size(keep + move + write), FontFamily = Mono, FontSize = 12, Foreground = B("Muted") };
            DockPanel.SetDock(total, Dock.Right);
            head.Children.Add(total);
            head.Children.Add(new TextBlock { Text = Texts.Get("by_size"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted") });
            box.Children.Add(head);
            var bar = new Grid { Height = 16 };
            void Part(long size, Brush fill, Brush stroke, int minPx)
            {
                if (size <= 0) return;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(size, 1), GridUnitType.Star), MinWidth = minPx });
                var r = new Border { Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(stroke == null ? 0 : 1), Margin = new Thickness(0, 0, 2, 0) };
                Grid.SetColumn(r, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(r);
            }
            var blueSoft = new SolidColorBrush(Color.FromArgb(0x59, 0x5A, 0xAA, 0xFF));
            Part(keep, blueSoft, B("Blue"), 2);
            Part(move, B("Yellow"), null, 4);
            Part(write, B("Ink"), null, 4);
            box.Children.Add(bar);
            var legend = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            legend.ColumnDefinitions.Add(new ColumnDefinition());
            legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Key(Brush fill, Brush stroke, string text, string value)
            {
                int r = legend.RowDefinitions.Count;
                legend.RowDefinitions.Add(new RowDefinition());
                var k = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                k.Children.Add(new Border { Width = 10, Height = 10, Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(stroke == null ? 0 : 1), Margin = new Thickness(0, 0, 6, 0) });
                k.Children.Add(new TextBlock { Text = text, FontSize = 12 });
                Grid.SetRow(k, r);
                legend.Children.Add(k);
                var v = new TextBlock { Text = value, FontFamily = Mono, FontSize = 12 };
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                legend.Children.Add(v);
            }
            Key(blueSoft, B("Blue"), Texts.Get("c_keep"), kept + " · " + Texts.Size(keep));
            Key(B("Yellow"), null, Texts.Get("c_move"), moved + " · " + Texts.Size(move));
            Key(B("Ink"), null, Texts.Get("c_write"), written + " · " + Texts.Size(write));
            box.Children.Add(legend);

            // the two numbers
            var tiles = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            long copyBytes = m.SameDrive ? 0 : write;
            tiles.Children.Add(Tile(Texts.Get("tile_write"), Texts.Size(copyBytes), m.SameDrive ? "Blue" : "Yellow", Texts.Get(m.SameDrive ? "tile_write_n" : "tile_write_c"), 0));
            tiles.Children.Add(Tile(Texts.Get("tile_reuse"), Texts.Size(keep + move), "Blue", Texts.F("tile_reuse_n", Texts.N(kept + moved)), 1));
            box.Children.Add(tiles);

            // the files
            box.Children.Add(new TextBlock { Text = Texts.Get("files_changed"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), Margin = new Thickness(0, 16, 0, 6) });
            var list = new StackPanel();
            foreach (var f in m.Files.Where(f => f.Action != "keep").Take(40))
            {
                var row = new DockPanel { MinHeight = 32, Margin = new Thickness(10, 0, 10, 0) };
                bool w = f.Action == "write";
                var tag = new Border
                {
                    Width = 36, Background = w ? B("Ink") : B("Yellow"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock { Text = Texts.Get(w ? "tag_write" : "tag_move"), FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("TapeInk"), HorizontalAlignment = HorizontalAlignment.Center },
                };
                DockPanel.SetDock(tag, Dock.Left);
                row.Children.Add(tag);
                var size = new TextBlock { Text = Texts.Size(f.Bytes), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(size);
                row.Children.Add(new TextBlock
                {
                    Text = f.Name + (f.From != null ? "  " + Texts.F("moved_from", f.From) : ""), FontFamily = Mono, FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Src,
                });
                list.Children.Add(new Border { BorderBrush = B("Ground700"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
            }
            box.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Child = list });
            return box;
        }

        Border Tile(string label, string value, string colour, string note, int column)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = B("Muted") });
            s.Children.Add(new TextBlock { Text = value, FontFamily = Mono, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B(colour) });
            s.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap });
            var t = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Padding = new Thickness(12), Child = s, Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0) };
            Grid.SetColumn(t, column);
            return t;
        }

        // ---------------------------------------------------------------- small pieces

        TextBlock Label(string key, Thickness margin) => new TextBlock
        {
            Text = Texts.Get(key), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), Margin = margin,
        };

        TextBlock Small(string text, Thickness margin) => new TextBlock
        {
            Text = text, FontSize = 12, LineHeight = 16, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin,
        };

        TextBlock Code(string text) => new TextBlock
        {
            Text = text, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        };

        Border Panel(UIElement child, Thickness margin) => new Border
        {
            BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Padding = new Thickness(16), Margin = margin, Child = child,
        };

        /// <summary>A picture from a mod's files (read into memory, so the file is not held
        /// open: a deploy or a new version may need it), else the board's line drawing.</summary>
        readonly Dictionary<string, (DateTime Written, ImageSource Image)> images = new Dictionary<string, (DateTime, ImageSource)>();

        /// <summary>A picture decoded once (to 256 px: a mod's icon is often 1254 px and
        /// 2.5 MB) and kept until its file changes; null without one.</summary>
        ImageSource LoadImage(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written.Year < 1700) return null;                 // no such file
                if (images.TryGetValue(file, out var had) && had.Written == written) return had.Image;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = 256;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                images[file] = (written, bmp);
                return bmp;
            }
            catch (Exception e) when (e is IOException || e is NotSupportedException || e is UriFormatException || e is ArgumentException
                                      || e is UnauthorizedAccessException) { return null; }
        }

        Border Picture(string file, string glyph, double size, string tint, double glyphSize = 28)
        {
            var frame = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900") };
            if (size > 0) { frame.Width = size; frame.Height = size; }
            else { frame.BorderThickness = new Thickness(0); frame.Background = Brushes.Transparent; }
            var img = LoadImage(file);
            if (img != null) frame.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
            else
                frame.Child = new Path
                {
                    Data = Geometry.Parse(glyph), Stroke = B(tint), StrokeThickness = 1.75, Width = glyphSize, Height = glyphSize, Stretch = Stretch.Uniform,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            return frame;
        }

        /// <summary>A button that draws only its content.</summary>
        static ControlTemplate Plain()
        {
            var t = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            var bg = new FrameworkElementFactory(typeof(Border));
            bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bg.AppendChild(cp);
            t.VisualTree = bg;
            return t;
        }

        ContextMenu Menu(ModRow m)
        {
            var menu = new ContextMenu();
            void Item(string key, Action act, bool danger = false)
            {
                var it = new MenuItem { Header = Texts.Get(key) };
                if (danger) it.Foreground = B("Red");
                it.Click += (s, e) => act();
                menu.Items.Add(it);
            }
            // several mods taken at once: what can be done to all of them (the user, 2026-10-24)
            var many = Taken(m);
            if (many.Count > 1)
            {
                var on = new MenuItem { Header = Texts.F("many_on", many.Count) };
                on.Click += (s, e) => { foreach (var x in many.Where(x => !x.Enabled)) Change(x.Id, c => c.Enabled = true, "enable", x.Id); };
                menu.Items.Add(on);
                var off = new MenuItem { Header = Texts.F("many_off", many.Count) };
                off.Click += (s, e) => { foreach (var x in many.Where(x => x.Enabled)) Change(x.Id, c => c.Enabled = false, "disable", x.Id); };
                menu.Items.Add(off);
                // the top and bottom of the screen: with the winner shown at the top, the other
                // ends of the load order
                List<string[]> First() => Enumerable.Reverse(many).Select(x => new[] { "move", x.Id, "--top" }).ToList();
                List<string[]> Last() => many.Select(x => new[] { "move", x.Id, "--bottom" }).ToList();
                ProfileItems(menu, many);
                var up = new MenuItem { Header = Texts.F("many_top", many.Count) };
                up.Click += (s, e) => RunMany(Texts.TopWins ? Last() : First());
                menu.Items.Add(up);
                var down = new MenuItem { Header = Texts.F("many_bottom", many.Count) };
                down.Click += (s, e) => RunMany(Texts.TopWins ? First() : Last());
                menu.Items.Add(down);
                menu.Items.Add(TagMenu(m));
                if (many.Any(x => x.Channel == "Nexus Mods" || x.Channel == "GitHub"))
                {
                    var match = new MenuItem { Header = Texts.F("many_match", many.Count) };
                    match.Click += (s, e) => MatchSource(m);
                    menu.Items.Add(match);
                }
                menu.Items.Add(new Separator());
                var gone = new MenuItem { Header = Texts.F("many_remove", many.Count) };
                gone.Click += (s, e) =>
                {
                    if (!Dialog.Ask(this, Texts.F("many_remove_ask", many.Count), Texts.Get("remove"))) return;
                    RunMany(many.Select(x => new[] { "remove", x.Id }).ToList());
                };
                menu.Items.Add(gone);
                return menu;
            }
            // only what this menu alone can do: opening the folder, matching the source,
            // removing and deleting are all on the mod's own page (the user, 2026-10-24:
            // the menu had grown too long)
            ProfileItems(menu, Taken(m));
            Item("rename", () => RenameMod(m));
            menu.Items.Add(TagMenu(m));
            // the top and bottom of the screen, as the list is shown
            bool topWins = Texts.TopWins;
            Item(topWins ? "top_wins_top" : "top", () => Run("move", m.Id, topWins ? "--bottom" : "--top"));
            Item(topWins ? "top_wins_bottom" : "bottom", () => Run("move", m.Id, topWins ? "--top" : "--bottom"));
            menu.Items.Add(new Separator());
            Item("replace_zip", () => { var p = PickZip(); if (p != null) Run("replace", m.Id, p); });
            Item("replace_folder", () => { var p = PickFolder(); if (p != null) Run("replace", m.Id, p); });
            return menu;
        }

/// <summary>The labels, as a menu of its own: every label there is, lit when the mods taken
        /// all carry it, and a line at the end to make a new one. A list of them in a question was
        /// no good with ten of them (the user, 2026-10-24).</summary>
        MenuItem TagMenu(ModRow m)
        {
            var many = Taken(m);
            var top = new MenuItem
            {
                Header = many.Count > 1 ? Texts.F("many_tag", many.Count) : Texts.Get("tag_add"),
                Style = (Style)FindResource("SubMenuItem"),
            };
            foreach (var (name, _) in AllTags())
            {
                bool all = many.All(x => Carries(x, name, false));
                var it = new MenuItem { Header = name, IsCheckable = true, IsChecked = all };
                var tag = name;
                it.Click += (s, e) => PutTag(many, tag, !all);
                top.Items.Add(it);
            }
            if (top.Items.Count > 0) top.Items.Add(new Separator());
            var make = new MenuItem { Header = Texts.Get("tag_new_dlg") };
            make.Click += (s, e) =>
            {
                var name = Dialog.AskText(this, Texts.Get("tag_new_ask"), Texts.Get("tag_new_save"), "");
                if (name == null || name.Trim().Length == 0) return;
                PutTag(many, name.Trim(), true);
            };
            top.Items.Add(make);
            return top;
        }

        /// <summary>A label put on the mods, or taken off all of them.</summary>
        void PutTag(List<ModRow> mods, string tag, bool on)
        {
            foreach (var x in mods)
            {
                bool has = Carries(x, tag, false);
                if (on && !has) Change(x.Id, c => c.Tags.Add(tag), "tag", x.Id, tag);
                else if (!on && has) Change(x.Id, c => c.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)), "untag", x.Id, tag);
            }
        }

                /// <summary>A name of the player's own for a mod, asked for with the current one in the
        /// box. The mod's folder and files are left alone; the name it came with is kept and the
        /// details show it. An empty box, or the same name, changes nothing.</summary>
        void RenameMod(ModRow m)
        {
            var name = Dialog.AskText(this, Texts.Get("rename_ask"), Texts.Get("rename_save"), m.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name == m.Name) return;
            Run("rename", m.Id, name);
        }

        /// <summary>A mod off the disk for good, after a red question that names its folder:
        /// out of the library and the game (a deploy), its folder deleted.</summary>
        void DeleteFromDisk(ModRow m)
        {
            if (!Dialog.AskDanger(this, Texts.F("delete_ask", m.Name, m.Path), Texts.Get("delete_btn"))) return;
            var id = m.Id;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == id) ?? throw new ModError("not in the library any more: " + m.Name);
                var r = Ops.DeleteMod(lib, mod, Store.GameDir());
                return Texts.F("deleted_mod", mod.Name, Texts.Size(r.Bytes));
            }), true);
        }

        // ---------------------------------------------------------------- the buttons

        // ---------------------------------------------------------------- update checks

        // what each mod was when it was checked: a result stops counting once the mod changes
        readonly Dictionary<string, string> checkedAs = new Dictionary<string, string>();

        static string CheckedAs(string path, string version, string page) => path + "\u001f" + version + "\u001f" + page;

        /// <summary>The last check's result for the mod as it is now; null when not checked or since changed.</summary>
        UpdateInfo Checked(ModRow m) =>
            updates.TryGetValue(m.Id, out var u) && checkedAs.TryGetValue(m.Id, out var was) && was == CheckedAs(m.Path, m.Version, m.Page) ? u : null;

        bool HasUpdate(ModRow m) => Checked(m)?.Status == "update";

        /// <summary>Every mod checked on its own channel (Nexus Mods, GitHub), the window kept usable
        /// meanwhile; then the mods with updates are listed and a summary is shown.</summary>
        async void Updates_Click(object sender, RoutedEventArgs e)
        {
            if (checking || busy) return;
            checking = true;
            UpdatesBtn.Content = Texts.Get("up_checking_btn");
            UpdatesBtn.IsEnabled = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<UpdateInfo> found = null;
            var was = new Dictionary<string, string>();
            string failed = null;
            try
            {
                // the origins HD2 Arsenal recorded are learnt first, in turn with the other commands
                var lib = await Queued(() =>
                {
                    var l = Library.Load();
                    if (Origins.FromArsenal(l) > 0) l.Save();
                    return l;
                });
                foreach (var m in lib.Mods) was[m.Id] = CheckedAs(m.Path, m.Nexus?.Version ?? m.GitHub?.Tag, Origins.Of(m).Page);
                found = await Task.Run(() => Updates.Check(lib, (i, n) =>
                    Dispatcher.BeginInvoke(new Action(() => Foot(Texts.F("up_progress", i, n), "Blue")))));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally
            {
                checking = false;
                UpdatesBtn.IsEnabled = true;
                UpdatesBtn.Content = Texts.Get("up_check");
            }
            if (failed != null) { Foot(Texts.F("failed", failed), "Yellow"); Dialog.Tell(this, Texts.F("failed", failed)); return; }

            updates.Clear();
            checkedAs.Clear();
            updated.Clear();
            foreach (var u in found)
            {
                updates[u.ModId] = u;
                checkedAs[u.ModId] = was[u.ModId];
            }
            int Count(string status, Func<UpdateInfo, bool> also = null) => found.Count(u => u.Status == status && (also == null || also(u)));
            int upd = Count("update"), err = Count("error"), none = Count("no_source"), other = Count("no_check");
            Foot(Texts.F("up_foot", upd, Seconds(clock.Elapsed.TotalSeconds)), upd > 0 ? "Yellow" : "Blue");
            if (upd > 0)
            {
                filter = "updates";
                Current(found.First(u => u.Status == "update").ModId);
            }
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }

            var text = Texts.F("up_summary", found.Count, upd, Count("current"), Count("unknown"), none);
            if (other > 0) text += "\n\n" + Texts.F("up_summary_other", other);
            if (err > 0) text += "\n\n" + Texts.F("up_summary_err", err);
            if (none > 0) text += "\n\n" + Texts.Get("up_summary_source");
            Dialog.Tell(this, text);
        }

        // the mods whose own check is running: their row says so meanwhile
        readonly HashSet<string> checkingOne = new HashSet<string>();

        // the mods updated while this window has been open: their mark is a tick (the user,
        // 2026-10-24), which the next check takes away again
        readonly HashSet<string> updated = new HashSet<string>();

        /// <summary>One mod checked on its own channel, from its own row (the user, 2026-10-24).
        /// The row says "checking", then keeps the answer; the window stays usable and the other
        /// rows keep theirs. What came back is said in the footer, not in a window to close.</summary>
        async void CheckOne(ModRow m)
        {
            if (checking || !checkingOne.Add(m.Id)) return;
            RenderList();
            string was = null, failed = null;
            UpdateInfo u = null;
            try
            {
                // the origin HD2 Arsenal recorded is learnt first, as the whole library's check does
                var mod = await Queued(() =>
                {
                    var l = Library.Load();
                    if (Origins.FromArsenal(l) > 0) l.Save();
                    return l.Mods.FirstOrDefault(x => x.Id == m.Id);
                });
                if (mod == null) return;
                was = CheckedAs(mod.Path, mod.Nexus?.Version ?? mod.GitHub?.Tag, Origins.Of(mod).Page);
                u = await Task.Run(() => Updates.One(mod));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally { checkingOne.Remove(m.Id); }
            if (failed != null)
            {
                Foot(Texts.F("failed", failed), "Yellow");
                if (st != null) RenderList();
                return;
            }
            updates[u.ModId] = u;
            checkedAs[u.ModId] = was;
            updated.Remove(u.ModId);
            Foot(Texts.F("up_one_foot", m.Name, Texts.Get("up_row_" + u.Status)), u.Status == "update" ? "Yellow" : "Blue");
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }
        }

        /// <summary>The details' update section: where the mod comes from, its version, the last
        /// check's result, and what can be done (update, open its page, set its source).</summary>
        /// <summary>Buttons in two columns of the same width, in the order given; a last one
        /// with no partner takes the whole width, so no row is left ragged.</summary>
        UIElement Columns(List<Button> buttons, Thickness margin)
        {
            var grid = new Grid { Margin = margin };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < buttons.Count; i++)
            {
                bool alone = i == buttons.Count - 1 && i % 2 == 0;
                int row = i / 2, col = i % 2;
                while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition());
                var b = buttons[i];
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(alone || col == 0 ? 0 : 4, row == 0 ? 0 : 8, alone || col == 1 ? 0 : 4, 0);
                Grid.SetRow(b, row);
                Grid.SetColumn(b, col);
                if (alone) Grid.SetColumnSpan(b, 2);
                grid.Children.Add(b);
            }
            return grid;
        }

        UIElement UpdatePanel(ModRow m)
        {
            var box = new StackPanel();
            box.Children.Add(Label("up_section", new Thickness(0, 0, 0, 6)));
            void Line(string key, string value, string tip = null)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var v = new TextBlock { Text = value, FontSize = 12, FontFamily = Mono, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip ?? value, Margin = new Thickness(16, 0, 0, 0) };
                DockPanel.SetDock(v, Dock.Right);
                dp.Children.Add(v);
                dp.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                box.Children.Add(dp);
            }
            Line("d_channel", m.Channel ?? Texts.Get("up_none"));
            // the version alone: where it was found is already on the line above, and the tip
            // says it for the versions that were guessed (the user, 2026-10-24)
            Line("d_version", m.ShownVersion == null ? Texts.Get("up_none") : V(m.ShownVersion), VersionTip(m));
            // what the source's page said its newest was, when it was last asked: never the
            // installed version, and only shown when it is not the same (the user, 2026-10-25)
            if (!string.IsNullOrWhiteSpace(m.SiteVersion) &&
                !string.Equals((m.SiteVersion ?? "").TrimStart('v', 'V'), (m.ShownVersion ?? "").TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                Line("d_site_version", V(m.SiteVersion));

            var u = Checked(m);
            string text, colour = "Muted";
            bool apply = false;
            if (u == null) text = m.Channel == null ? Texts.Get("up_no_source") : Texts.Hints ? Texts.Get("up_not_checked") : "";
            else switch (u.Status)
            {
                case "update":
                    text = string.IsNullOrEmpty(u.Current) ? Texts.F("up_new_unknown", u.Latest) : Texts.F("up_new", u.Current, u.Latest);
                    colour = "Yellow";
                    apply = true;
                    break;
                case "unknown":
                    text = Texts.F("up_new_unknown", u.Latest ?? "?");
                    colour = "Ink";
                    apply = u.Latest != null;
                    break;
                case "current": text = Texts.Get("up_current"); colour = "Blue"; break;
                case "no_source": text = Texts.Get("up_no_source"); break;
                case "no_check": text = Texts.F("up_no_check", u.Channel); break;
                default: text = Texts.F("up_error", CheckWhy(u.Why)); colour = "Yellow"; break;
            }
            // a GitHub release without an archive to download: its page only
            if (apply && m.Channel == "GitHub" && string.IsNullOrEmpty(u.Download)) apply = false;
            if (text.Length > 0) box.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = B(colour), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

            // the buttons in two columns of the same width, the odd one last across both:
            // a row of them wrapped into a ragged second line (the user, 2026-10-24)
            var buttons = new List<Button>();
            if (apply)
            {
                var go = new Button { Content = Texts.Get(u.Status == "update" ? "up_apply" : "up_apply_latest"), Style = (Style)FindResource("Primary"), MinHeight = 34 };
                go.Click += (s, e) => ApplyUpdate(m, u);
                buttons.Add(go);
            }
            var page = u?.Page ?? m.Page;
            if (page != null)
            {
                var see = new Button { Content = Texts.Get("up_page"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
                see.Click += (s, e) => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                buttons.Add(see);
            }
            if (m.Channel != null)
            {
                var again = new Button { Content = Texts.Get("match"), Style = (Style)FindResource("Ghost"), MinHeight = 34, ToolTip = Texts.Get("match_tip") };
                again.Click += (s, e) => MatchSource(m);
                buttons.Add(again);
            }
            var set = new Button { Content = Texts.Get("up_set_source"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
            set.Click += (s, e) => SetSource(m);
            buttons.Add(set);
            box.Children.Add(Columns(buttons, new Thickness(0, 10, 0, 0)));
            return Panel(box, new Thickness(0, 20, 0, 0));
        }

        /// <summary>Why a check failed, in the window's words.</summary>
        static string CheckWhy(string why)
        {
            switch (why)
            {
                case "offline": return Texts.Get("up_why_offline");
                case "missing": return Texts.Get("up_why_missing");
                case "busy": return Texts.Get("up_why_busy");
                default: return Why(why);
            }
        }

        /// <summary>A mod's newer version put in its place (its place in the order, on/off and
        /// options kept), from GitHub at once.</summary>
        async void ApplyUpdate(ModRow m, UpdateInfo u)
        {
            if (busy) return;
            if (m.Channel == "GitHub")
            {
                if (await Act(() => Cli.Call(() => Texts.F("up_done", Updates.ApplyGitHub(m.Id, u).Name, u.Latest)), true))
                { updates.Remove(m.Id); updated.Add(m.Id); }
                return;
            }
        }

        /// <summary>Where a mod comes from, typed in: a Nexus Mods page, a GitHub repository or release, another page.</summary>
        void SetSource(ModRow m)
        {
            var url = Dialog.AskText(this, Texts.Get("up_source_ask"), Texts.Get("up_source_save"), m.Page ?? "");
            if (url == null) return;
            url = url.Trim();
            if (url.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Dialog.Tell(this, Texts.F("up_source_bad", url)); return; }
            // the address is kept, and then the site is asked what this mod is: a source typed
            // in by hand should fill the rest in by itself (the user, 2026-10-24)
            SetThenMatch(m, url);
        }

        async void SetThenMatch(ModRow m, string url)
        {
            bool ok = await Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == m.Id) ?? throw new ModError("not in the library any more", "gone");
                Origins.FromUrl(mod, url);
                lib.Save();
                return Texts.F("up_source_done", mod.Name, Origins.Of(mod).Channel);
            }), true);
            if (ok) MatchSource(m);
        }

        /// <summary>A mod matched against its source: the name, version, author, words and picture
        /// the site has for it are filled in (the user, 2026-10-24, as a reference manager fills a
        /// paper in). The name it came with is kept and the details say it.</summary>
        async void MatchSource(ModRow m)
        {
            if (busy) return;
            var many = Taken(m).Where(x => x.Channel == "Nexus Mods" || x.Channel == "GitHub").ToList();
            if (many.Count == 0) { Dialog.Tell(this, Texts.Get("match_no_source")); return; }
            var ids = many.Select(x => x.Id).ToList();
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                int done = 0;
                string last = null;
                foreach (var id in ids)
                {
                    var mod = lib.Mods.FirstOrDefault(x => x.Id == id);
                    if (mod == null) continue;
                    try { Site.Fill(lib, mod); done++; last = mod.Name; }
                    catch (ModError e) when (ids.Count > 1) { last = e.Message; }
                }
                lib.Save();
                return ids.Count == 1 ? Texts.F("match_done", last) : Texts.F("match_done_n", done, ids.Count);
            }));
            if (!Refused(r) && r.Ok) lastDone = Last(r.Out);
            Refresh();
        }

/// <summary>The first filter inside the search field: where a mod comes from and what it
        /// is. Any one of them in a group will do; a mod must pass every group that has one.</summary>
        void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(FilterBtn)) return;
            var mods = st?.Mods ?? new List<ModRow>();
            var menu = new ContextMenu { PlacementTarget = FilterBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            Group(menu, false, fromFilter, new List<(string, string, int)>
            {
                ("nexus", Texts.Get("filter_nexus"), mods.Count(m => FromOf(m) == "nexus")),
                ("github", Texts.Get("filter_github"), mods.Count(m => FromOf(m) == "github")),
                ("none", Texts.Get("filter_no_from"), mods.Count(m => FromOf(m) == "none")),
            });
            Group(menu, true, kindFilter, new List<(string, string, int)>
            {
                ("lua", Texts.Get("filter_lua"), mods.Count(m => KindOf(m) == "lua")),
                ("loader", Texts.Get("filter_loader"), mods.Count(m => KindOf(m) == "loader")),
                ("asset", Texts.Get("filter_asset"), mods.Count(m => KindOf(m) == "asset")),
            });
            Dismissable(menu);
            menu.IsOpen = true;
        }

        /// <summary>The second one, beside it: the player's own labels.</summary>
        void TagFilter_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(TagFilterBtn)) return;
            var menu = new ContextMenu { PlacementTarget = TagFilterBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var tags = AllTags().Concat(AutoTags().Where(t => !hiddenTags.Contains(t.Name))).ToList();
            if (tags.Count == 0) menu.Items.Add(new MenuItem { Header = Texts.Get("filter_no_tags"), IsEnabled = false });
            else TagGroup(menu, tags);
            Dismissable(menu);
            menu.IsOpen = true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        int wheelSeen;             // tests only
        bool altForTest;           // tests only: a click taken as Alt+click

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        ContextMenu lastMenu;      // tests only: the filter menu last opened

        /// <summary>A filter's menu stays open while choices are taken, so several can be lit at
        /// once. A menu is a window of its own, though, and while it is open the wheel goes to
        /// it wherever the pointer is: the list would not scroll anywhere until something was
        /// clicked to dismiss it (the user, 2026-10-25). So a turn of the wheel from outside closes
        /// it and scrolls the page on show instead of being spent on the menu. The pointer
        /// leaving it does not close it: a menu stays open until a choice, a click outside or Esc
        /// (the user, 2026-10-25: closing on leave is not how menus behave).
        ///
        /// The handlers go on that window's own root, not on the menu: with the mouse held by
        /// the menu, an event never reaches a child of the element that holds it. Only for menus
        /// without submenus: a submenu is outside its parent, and this would close both.</summary>
        void Dismissable(ContextMenu menu)
        {
            lastMenu = menu;
            UIElement root = null;
            MouseWheelEventHandler wheel = null;
            wheel = (s, e) =>
            {
                if (Over(menu, e)) return;
                wheelSeen++;
                menu.IsOpen = false;
                var page = OnShow();
                if (page != null) page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta);
                e.Handled = true;
            };
            menu.Opened += (s, e) =>
            {
                root = (PresentationSource.FromVisual(menu) as HwndSource)?.RootVisual as UIElement;
                if (root == null) return;
                root.PreviewMouseWheel += wheel;
            };
            // the moment it closes, not its Closed event: that comes after the click that closed
            // it has already reached the button underneath
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler shut = (s, e) => { if (!menu.IsOpen) { closedFor = menu.PlacementTarget; closedAt = Environment.TickCount; } };
            isOpen.AddValueChanged(menu, shut);
            menu.Closed += (s, e) =>
            {
                isOpen.RemoveValueChanged(menu, shut);
                // the menu's own window had the keyboard, and Windows sends the wheel to the
                // window that has it: without this the list stays unscrollable until something
                // is clicked, which is the whole fault being fixed here
                // the menu held the mouse and the keyboard: both have to come back, or the
                // wheel keeps going to a window that is no longer there
                if (Mouse.Captured != null && !ReferenceEquals(Mouse.Captured, ListArea)) Mouse.Capture(null);
                Activate();
                SetFocus(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (root == null) return;
                root.PreviewMouseWheel -= wheel;
                root = null;
            };
        }

        // the scroll the wheel is turning now, when the wheel last turned, and when that scroll last moved
        ScrollViewer wheelTarget;
        int wheelLast, wheelMoved;
        const int WheelRestMs = 400;
        const int WheelHoldMs = 250;

        /// <summary>The wheel the way browsers have it, for a list with a scroll of its own inside
        /// a scrolling page (the user, 2026-10-25). A ScrollViewer keeps every turn of the wheel,
        /// even with nothing to scroll, so the tags page never moved with the pointer over its
        /// add list, at the list's end or with no scroll bar on it at all. Now a turn goes to the
        /// innermost scroll under the pointer that can still move that way, and stays with it
        /// until the wheel rests for WheelRestMs. A list that reaches its end holds the wheel for
        /// WheelHoldMs, so a flick past its end does not throw the page; a wheel still turning
        /// after that moves the page at once. Holding until the wheel rested kept the page still
        /// for as long as the wheel turned, 4 to 10 seconds (the user, 2026-10-25).</summary>
        void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
            var from = e.OriginalSource as DependencyObject;
            var scrolls = Scrolls(from).ToList();
            bool fresh = unchecked(e.Timestamp - wheelLast) > WheelRestMs || wheelTarget == null ||
                         !wheelTarget.IsVisible || !scrolls.Contains(wheelTarget);
            wheelLast = e.Timestamp;
            var innermost = scrolls.FirstOrDefault(v => CanMove(v, e.Delta));
            if (fresh) { wheelTarget = innermost; wheelMoved = e.Timestamp; }
            // the page brought a list under a pointer that stayed put: the list takes the wheel at
            // once, not when the wheel rests (the user, 2026-10-25: it took a long while)
            else if (innermost != null && innermost != wheelTarget && scrolls.IndexOf(innermost) < scrolls.IndexOf(wheelTarget))
            { wheelTarget = innermost; wheelMoved = e.Timestamp; }
            if (wheelTarget == null) return;                    // nothing here moves that way
            e.Handled = true;
            if (!CanMove(wheelTarget, e.Delta))
            {
                if (unchecked(e.Timestamp - wheelMoved) < WheelHoldMs) return;      // just reached its end: held
                // still turning: on to the next scroll out that can move
                var next = scrolls.SkipWhile(v => v != wheelTarget).Skip(1).FirstOrDefault(v => CanMove(v, e.Delta));
                if (next == null) return;
                wheelTarget = next;
            }
            wheelMoved = e.Timestamp;
            wheelTarget.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent, Source = wheelTarget,
            });
        }

        /// <summary>The scrolls d is inside, innermost first.</summary>
        static IEnumerable<ScrollViewer> Scrolls(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ScrollViewer v) yield return v;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
        }

        /// <summary>Whether v can still scroll the way a turn of delta goes.</summary>
        static bool CanMove(ScrollViewer v, int delta) =>
            v.ScrollableHeight > 0.5 && (delta < 0 ? v.VerticalOffset < v.ScrollableHeight - 0.5 : v.VerticalOffset > 0.5);

        // the button whose menu closed last, and when
        UIElement closedFor;
        int closedAt;

        /// <summary>Whether a click on the button that opens a menu is the one that just closed that
        /// menu: the press outside closes it, then the same click opened it again, so it flashed
        /// instead of going (the user, 2026-10-25). A second click on the button closes its menu.</summary>
        bool JustClosed(UIElement button) =>
            ReferenceEquals(closedFor, button) && unchecked(Environment.TickCount - closedAt) < 300;

        /// <summary>The scroll of the page on show.</summary>
        ScrollViewer OnShow() =>
            StoragePage.Visibility == Visibility.Visible ? StoragePage
            : TagsPage.Visibility == Visibility.Visible ? TagsPage
            : DownloadPage.Visibility == Visibility.Visible ? DownloadPage
            : ListScroll;

        /// <summary>Whether the pointer is inside the menu itself.</summary>
        static bool Over(ContextMenu menu, MouseEventArgs e)
        {
            var p = e.GetPosition(menu);
            return p.X >= 0 && p.Y >= 0 && p.X <= menu.ActualWidth && p.Y <= menu.ActualHeight;
        }

        /// <summary>The choices in a filter's menu, nothing else: a line above them when they
        /// follow another group, and each one lit while it is taken (the user, 2026-10-24).</summary>
        void Group(ContextMenu menu, bool line, HashSet<string> set, List<(string Key, string Name, int N)> items)
        {
            if (line) menu.Items.Add(new Separator());
            foreach (var (key, name, n) in items)
            {
                var it = new MenuItem { Header = name + "   " + n, IsCheckable = true, IsChecked = set.Contains(key), StaysOpenOnClick = true };
                var mine = key;
                it.Click += (s, e) =>
                {
                    if (!set.Remove(mine)) set.Add(mine);
                    AfterFilter();
                };
                menu.Items.Add(it);
            }
        }

        /// <summary>The labels in their menu. A click takes a label: a mod carrying any taken one
        /// is shown. Alt+click leaves a label out instead: a mod carrying it is not shown, drawn
        /// in faded hazard stripes (the user, 2026-10-25: Alt+click on Probe hides every Probe
        /// mod). One label is taken, left out or neither. A line at the bottom says how.</summary>
        void TagGroup(ContextMenu menu, List<(string Name, int N)> items)
        {
            foreach (var (name, n) in items)
            {
                var it = new MenuItem { Header = name + "   " + n, StaysOpenOnClick = true };
                var mine = name;
                void Paint()
                {
                    it.IsChecked = tagFilter.Contains(mine);
                    it.Tag = tagExclude.Contains(mine) ? "exclude" : null;
                }
                Paint();
                it.Click += (s, e) =>
                {
                    if (altForTest || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    {
                        tagFilter.Remove(mine);
                        if (!tagExclude.Remove(mine)) tagExclude.Add(mine);
                    }
                    else
                    {
                        tagExclude.Remove(mine);
                        if (!tagFilter.Remove(mine)) tagFilter.Add(mine);
                    }
                    Paint();
                    AfterFilter();
                };
                menu.Items.Add(it);
            }
            // the line that says how: an explaining line, gone with the others (the user, 2026-10-25)
            if (Texts.Hints)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem
                {
                    Header = new TextBlock { Text = Texts.Get("filter_tags_alt"), FontSize = 12, Foreground = B("Muted") },
                    IsEnabled = false, Cursor = Cursors.Arrow,
                });
            }
            // Alt on its own would take the menu into keyboard mode and close it: here it is held
            // for a click, so the menu keeps it
            KeyEventHandler alt = (s, e) =>
            {
                if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)) e.Handled = true;
            };
            menu.PreviewKeyDown += alt;
            menu.PreviewKeyUp += alt;
        }

        /// <summary>What each filter button says: its name, how many choices are lit, and the
        /// little arrow that says it opens.</summary>
        void FilterWords()
        {
            int narrow = fromFilter.Count + kindFilter.Count;
            FilterBtn.Content = Caret(narrow == 0 ? Texts.Get("filter") : Texts.F("filter_n", narrow), narrow > 0);
            int labels = tagFilter.Count + tagExclude.Count;
            TagFilterBtn.Content = Caret(labels == 0 ? Texts.Get("filter_tags") : Texts.F("filter_tags_n", labels), labels > 0);
        }

        UIElement Caret(string text, bool lit)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = text, Foreground = B(lit ? "Yellow" : "Ink"), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new Path
            {
                Data = Geometry.Parse("M0,0 L5,5 L10,0"), Stroke = B(lit ? "Yellow" : "Muted"), StrokeThickness = 1.6,
                Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            return line;
        }

        void AfterFilter()
        {
            FilterWords();
            if (st != null) { RenderList(); RenderDetail(); }
        }

        /// <summary>Every label in the library, the most used first.</summary>
        List<(string Name, int N)> AllTags()
        {
            var counts = new List<(string Name, int N)>();
            foreach (var m in st?.Mods ?? new List<ModRow>())
                foreach (var t in m.Tags)
                {
                    int i = counts.FindIndex(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase));
                    if (i < 0) counts.Add((t, 1));
                    else counts[i] = (counts[i].Name, counts[i].N + 1);
                }
            // the labels made but not yet given to a mod: the library keeps them too
            foreach (var t in st?.Tags ?? new List<string>())
                if (!counts.Any(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase))) counts.Add((t, 0));
            if (newTag != null && !counts.Any(x => string.Equals(x.Name, newTag, StringComparison.CurrentCultureIgnoreCase))) counts.Add((newTag, 0));
            return counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------------------------------------------------------------- the labels page

        // the label the page is showing (and whether it is one of the manifests'), one just
        // made that is on no mod yet, and what the page last did
        string tagSel, newTag, tagDone;
        bool tagAuto;

        /// <summary>The labels page: every label with how many mods carry it, and under the chosen
        /// one every mod in the library with a tick for whether it carries it (the user,
        /// 2026-10-24: a whole page for the labels, after storage).</summary>
        // the two boxes the page types into, kept across renders so typing is not interrupted
        TextBox tagFind, tagHunt;
        /// <summary>The mods ticked in the add box, so a label goes onto many at once, and
        /// whether that box leaves out the mods that already carry a label of the player's
        /// (the user's tester, 2026-10-25: labelling mod by mod is too slow).</summary>
        /// <summary>The folders of old versions ticked on the storage page.</summary>
        readonly HashSet<string> oldPick = new HashSet<string>();
        readonly HashSet<string> tagPick = new HashSet<string>();
        bool tagBare;
        TextBlock tagHint;
        StackPanel tagHits;

        /// <summary>A control kept from the last render, put into the new one. It has to leave its
        /// old parent first: WPF refuses a child that is already somewhere else.</summary>
        static void Reuse(FrameworkElement kept, Panel into)
        {
            if (kept.Parent is Panel was) was.Children.Remove(kept);
            into.Children.Add(kept);
        }

        /// <summary>The name in the box made into a label. It carries no mod yet, so it lives in
        /// the window until one is given it; a name already there is simply shown.</summary>
        void NewTag()
        {
            var name = (tagHunt?.Text ?? "").Trim();
            if (name.Length == 0) { tagHunt?.Focus(); return; }
            tagHunt.Text = "";
            tagAuto = false;
            tagSel = name;
            if (!AllTags().Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                newTag = name;              // shown at once; the library is told as well
                Run("tag-new", name);
            }
            RenderTags();
        }

        /// <summary>A label's note and whether it is hidden from the filter, in gui.json: the
        /// labels themselves live on the mods, these belong to the window.</summary>
        static Dictionary<string, object> TagNotes()
        {
            try { return Json.Obj(Store.Load("gui.json"), "tag_notes") ?? new Dictionary<string, object>(); }
            catch (Exception e) when (e is IOException || e is FormatException) { return new Dictionary<string, object>(); }
        }

        static void SaveTagNote(string tag, string note)
        {
            try
            {
                var d = Store.Load("gui.json");
                var notes = Json.Obj(d, "tag_notes") ?? new Dictionary<string, object>();
                if (string.IsNullOrWhiteSpace(note)) notes.Remove(tag);
                else notes[tag] = note.Trim();
                d["tag_notes"] = notes;
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        readonly HashSet<string> hiddenTags = LoadHiddenTags();

        static HashSet<string> LoadHiddenTags()
        {
            var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            try { foreach (var t in Json.List(Store.Load("gui.json"), "tag_hidden")) if (t is string x) set.Add(x); }
            catch (Exception e) when (e is IOException || e is FormatException) { }
            return set;
        }

        void SaveHiddenTags()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["tag_hidden"] = hiddenTags.Cast<object>().ToList();
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Every label a mod's own manifest declares, the most used first.</summary>
        List<(string Name, int N)> AutoTags()
        {
            var counts = new List<(string Name, int N)>();
            foreach (var m in st?.Mods ?? new List<ModRow>())
                foreach (var t in m.AutoTags)
                {
                    int i = counts.FindIndex(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase));
                    if (i < 0) counts.Add((t, 1));
                    else counts[i] = (counts[i].Name, counts[i].N + 1);
                }
            return counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        bool Carries(ModRow m, string tag, bool auto) =>
            (auto ? m.AutoTags : m.Tags).Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>The labels page (the design board's Tags.dc.html): the labels down the left,
        /// mine and the ones the mods' manifests declare; on the right the chosen one, what can be
        /// done to it, the mods that carry it and a box to find a mod and add it.</summary>
        void RenderTags()
        {
            TagsBox.Children.Clear();
            if (st == null) return;
            var mine = AllTags();
            var auto = AutoTags();
            if (tagSel == null || !(tagAuto ? auto : mine).Any(t => string.Equals(t.Name, tagSel, StringComparison.CurrentCultureIgnoreCase)))
            {
                tagAuto = mine.Count == 0 && auto.Count > 0;
                tagSel = (tagAuto ? auto : mine).Select(t => t.Name).FirstOrDefault();
            }
            bool typing = tagHunt != null && tagHunt.IsKeyboardFocusWithin;
            bool finding = tagFind != null && tagFind.IsKeyboardFocusWithin;

            // the head: what the page holds
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            head.Children.Add(new TextBlock { Text = Texts.Get("tag_title"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var sum = new TextBlock
            {
                Text = Texts.F("tag_summary", st.Mods.Count, mine.Count, auto.Count), FontFamily = Mono, FontSize = 12,
                Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 6, 0, 0),
            };
            head.Children.Add(sum);
            TagsBox.Children.Add(head);

            var two = new Grid();
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            two.ColumnDefinitions.Add(new ColumnDefinition());
            TagsBox.Children.Add(two);

            // ---------------------------------------------------------- the labels
            var left = new StackPanel();
            Grid.SetColumn(left, 0);
            two.Children.Add(left);

            // the name goes in the box, the button makes it: a box that only searched was taken
            // for the name every time (the user, 2026-10-24). There are never enough labels to
            // need searching anyway
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var make = new Button { Content = Texts.Get("tag_new"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
            make.Click += (s, e) => NewTag();
            DockPanel.SetDock(make, Dock.Right);
            tools.Children.Add(make);
            if (tagHunt == null)
            {
                tagHunt = new TextBox
                {
                    Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"), BorderBrush = B("Grid"),
                    BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 13,
                };
                tagHunt.TextChanged += (s, e) => { if (tagHint != null) tagHint.Visibility = tagHunt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
                tagHunt.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; NewTag(); } };
            }
            var nameBox = new Grid();
            Reuse(tagHunt, nameBox);
            tagHint = new TextBlock
            {
                Text = Texts.Get("tag_new_hint"), Foreground = B("Muted"), FontSize = 13, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Visibility = tagHunt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            nameBox.Children.Add(tagHint);
            tools.Children.Add(nameBox);
            left.Children.Add(tools);

            var mineShown = mine;
            var autoShown = auto;

            void Row(string name, int n, bool isAuto)
            {
                bool cur = tagAuto == isAuto && string.Equals(name, tagSel, StringComparison.CurrentCultureIgnoreCase);
                bool off = isAuto && hiddenTags.Contains(name);
                var b = new Button { Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, Margin = new Thickness(0, 0, 0, 2) };
                var edge = new Border { BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = cur ? B("Yellow") : Brushes.Transparent, Background = cur ? B("Ground900") : Brushes.Transparent };
                var dp = new DockPanel { Margin = new Thickness(12, 9, 12, 9), Opacity = off ? 0.5 : 1 };
                var count = new TextBlock { Text = n.ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(count, Dock.Right);
                dp.Children.Add(count);
                if (off)
                {
                    var mark = new TextBlock { Text = Texts.Get("tag_hidden"), FontSize = 11, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                    DockPanel.SetDock(mark, Dock.Right);
                    dp.Children.Add(mark);
                }
                // the colour has to be said: a button with a plain template of its own does not
                // take the window's ink (the user, 2026-10-24)
                dp.Children.Add(new TextBlock
                {
                    Text = name, FontWeight = FontWeights.SemiBold, Foreground = B(off ? "Muted" : "Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });
                edge.Child = dp;
                b.Content = edge;
                var mineName = name;
                b.Click += (s, e) => { tagSel = mineName; tagAuto = isAuto; RenderTags(); };
                left.Children.Add(b);
            }

            if (mineShown.Count > 0)
            {
                left.Children.Add(Label("tag_mine", new Thickness(2, 6, 0, 6)));
                foreach (var (name, n) in mineShown) Row(name, n, false);
            }
            if (autoShown.Count > 0)
            {
                left.Children.Add(Label("tag_auto", new Thickness(2, 14, 0, 6)));
                foreach (var (name, n) in autoShown) Row(name, n, true);
            }
            if (mineShown.Count + autoShown.Count == 0)
                left.Children.Add(Small(Texts.Get("tag_none"), new Thickness(2, 6, 0, 0)));
            if (typing) tagHunt.Focus();
            if (tagSel == null) return;

            // ---------------------------------------------------------- the chosen label
            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            two.Children.Add(right);

            right.Children.Add(new TextBlock { Text = Texts.Get(tagAuto ? "tag_kind_auto" : "tag_kind_mine"), FontFamily = Mono, FontSize = 12, Foreground = B("Muted") });
            var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (tagAuto)
            {
                var hide = new Button { Content = Texts.Get(hiddenTags.Contains(tagSel) ? "tag_unhide" : "tag_hide"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                hide.Click += (s, e) =>
                {
                    if (!hiddenTags.Remove(tagSel)) hiddenTags.Add(tagSel);
                    SaveHiddenTags();
                    tagFilter.RemoveWhere(x => hiddenTags.Contains(x));
                    tagExclude.RemoveWhere(x => hiddenTags.Contains(x));
                    AfterFilter();
                    RenderTags();
                };
                acts.Children.Add(hide);
            }
            else
            {
                var rename = new Button { Content = Texts.Get("tag_rename"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                rename.Click += (s, e) =>
                {
                    var name = Dialog.AskText(this, Texts.Get("tag_rename_ask"), Texts.Get("tag_rename_save"), tagSel);
                    if (name == null || name.Trim().Length == 0 || name.Trim() == tagSel) return;
                    var now = name.Trim();
                    var was = tagSel;
                    tagSel = now;
                    if (newTag != null && string.Equals(newTag, was, StringComparison.CurrentCultureIgnoreCase)) { newTag = now; RenderTags(); return; }
                    if (tagFilter.Remove(was)) tagFilter.Add(now);
                    if (tagExclude.Remove(was)) tagExclude.Add(now);
                    SaveTagNote(now, Json.Str(TagNotes(), was));
                    SaveTagNote(was, null);
                    Run("tag-rename", was, now);
                };
                acts.Children.Add(rename);
                var drop = new Button { Content = Texts.Get("tag_delete"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                drop.Click += (s, e) =>
                {
                    if (!Dialog.Ask(this, Texts.F("tag_delete_ask", tagSel), Texts.Get("tag_delete"))) return;
                    var was = tagSel;
                    if (newTag != null && string.Equals(newTag, was, StringComparison.CurrentCultureIgnoreCase)) newTag = null;
                    tagSel = null;
                    tagFilter.Remove(was);
                    tagExclude.Remove(was);
                    SaveTagNote(was, null);
                    tagDone = Texts.F("tag_dropped", was);
                    Run("tag-delete", was);
                };
                acts.Children.Add(drop);
            }
            var show = new Button { Content = Texts.Get("tag_show"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
            show.Click += (s, e) =>
            {
                tagFilter.Clear();
                tagExclude.Clear();
                tagFilter.Add(tagSel);
                AfterFilter();
                ShowPage("deploy");
            };
            acts.Children.Add(show);
            DockPanel.SetDock(acts, Dock.Right);
            bar.Children.Add(acts);
            bar.Children.Add(new TextBlock { Text = tagSel, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            right.Children.Add(bar);
            if (tagAuto && Texts.Hints) right.Children.Add(Small(Texts.Get("tag_auto_note"), new Thickness(0, 8, 0, 0)));

            // a note of the player's own
            if (!tagAuto)
            {
                var noteLine = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
                var label = new TextBlock { Text = Texts.Get("tag_note"), FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                DockPanel.SetDock(label, Dock.Left);
                noteLine.Children.Add(label);
                var note = new TextBox
                {
                    Text = Json.Str(TagNotes(), tagSel) ?? "", Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"),
                    BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 13, MaxWidth = 460,
                    HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320,
                };
                var forTag = tagSel;
                note.LostFocus += (s, e) => SaveTagNote(forTag, note.Text);
                noteLine.Children.Add(note);
                right.Children.Add(noteLine);
            }

            // the mods that carry it
            var members = st.Mods.Where(m => Carries(m, tagSel, tagAuto)).ToList();
            var all = new DockPanel { Margin = new Thickness(0, 20, 0, 6) };
            var bulk = new StackPanel { Orientation = Orientation.Horizontal };
            var only = new Button { Content = Texts.Get("tag_only"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), ToolTip = Texts.F("tag_only_tip", members.Count) };
            only.Click += (s, e) =>
            {
                // the mods under the label turned on; the rest are left as they are (the user,
                // 2026-10-24: nobody turns a part of their mods on and everything else off)
                foreach (var m in members.Where(x => !x.Enabled)) Change(m.Id, c => c.Enabled = true, "enable", m.Id);
                tagDone = Texts.F("tag_only_done", tagSel, members.Count);
                RenderTags();
            };
            bulk.Children.Add(only);
            var offAll = new Button { Content = Texts.Get("tag_off_all"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0) };
            offAll.Click += (s, e) =>
            {
                foreach (var m in members.Where(x => x.Enabled)) Change(m.Id, c => c.Enabled = false, "disable", m.Id);
                tagDone = Texts.F("tag_off_done", tagSel, members.Count);
                RenderTags();
            };
            bulk.Children.Add(offAll);
            DockPanel.SetDock(bulk, Dock.Right);
            all.Children.Add(bulk);
            all.Children.Add(new TextBlock { Text = Texts.F("tag_on_n", members.Count), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(all);

            var carry = new StackPanel();
            if (members.Count == 0) carry.Children.Add(Small(Texts.Get("tag_empty"), new Thickness(0, 0, 0, 0)));
            int memberBand = 0;
            foreach (var m in members)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                if (!tagAuto)
                {
                    var offBtn = new Button { Content = "✕", Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, Foreground = B("Muted"), Width = 28, ToolTip = Texts.F("tag_off", m.Name) };
                    var mod = m;
                    var name = tagSel;
                    offBtn.Click += (s, e) =>
                    {
                        Change(mod.Id, c => c.Tags.RemoveAll(t => string.Equals(t, name, StringComparison.CurrentCultureIgnoreCase)), "untag", mod.Id, name);
                        RenderTags();
                    };
                    DockPanel.SetDock(offBtn, Dock.Right);
                    row.Children.Add(offBtn);
                }
                var state = new TextBlock
                {
                    Text = Texts.Get(m.Enabled ? "tag_on_state" : "tag_off_state"), FontFamily = Mono, FontSize = 12,
                    Foreground = B(m.Enabled ? "Blue" : "Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
                };
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);
                var ver = new TextBlock { Text = m.ShownVersion != null ? V(m.ShownVersion) : "", FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(ver, Dock.Right);
                row.Children.Add(ver);
                var pic = Picture(m.Icon, GlyphMod, 36, m.Enabled ? "Blue" : "Muted");
                DockPanel.SetDock(pic, Dock.Left);
                row.Children.Add(pic);
                row.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
                carry.Children.Add(Band(row, memberBand++));
            }
            right.Children.Add(Panel(carry, new Thickness(0, 0, 0, 16)));

            // a box to find a mod and put the label on it
            if (!tagAuto)
            {
                right.Children.Add(Label("tag_put", new Thickness(0, 0, 0, 6)));
                var findLine = new DockPanel();
                var bare = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = tagBare, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                    Content = new TextBlock { Text = Texts.Get("tag_bare"), FontSize = 12, Foreground = B("Muted") },
                };
                bare.Click += (s, e) => { tagBare = bare.IsChecked == true; FillTagHits(); };
                DockPanel.SetDock(bare, Dock.Right);
                findLine.Children.Add(bare);
                if (tagFind == null)
                {
                    tagFind = new TextBox
                    {
                        Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"), BorderBrush = B("Grid"),
                        BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 14,
                    };
                    tagFind.TextChanged += (s, e) => FillTagHits();
                }
                Reuse(tagFind, findLine);
                right.Children.Add(findLine);
                tagHits = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                right.Children.Add(tagHits);
                FillTagHits();
                if (finding) tagFind.Focus();
            }

            // what just happened, and how many mods have no label of mine
            int none = st.Mods.Count(m => m.Tags.Count == 0);
            var foot = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            var rest = new TextBlock
            {
                Text = none == 0 ? Texts.Get("tag_all_done") : Texts.F("tag_rest", none), FontSize = 12, Foreground = B("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(rest, Dock.Right);
            foot.Children.Add(rest);
            foot.Children.Add(new TextBlock
            {
                // what was just done stays; the line that explains labels is an explaining line (the user, 2026-10-25)
                Text = tagDone ?? (Texts.Hints ? Texts.Get("tag_foot") : ""), FontSize = 12, Foreground = B(tagDone != null ? "Blue" : "Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            right.Children.Add(foot);
        }

        /// <summary>The mods that do not carry the label yet: every one of them, each with a tick,
        /// so a label goes onto many in one call (the user's tester, 2026-10-25: a box that showed
        /// six of them made labelling a library too slow).</summary>
        void FillTagHits()
        {
            if (tagHits == null || st == null || tagSel == null) return;
            tagHits.Children.Clear();
            var words = (tagFind?.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = st.Mods
                .Where(m => !Carries(m, tagSel, false))
                .Where(m => !tagBare || m.Tags.Count == 0)
                .Where(m => words.All(w => m.Name.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0))
                .ToList();
            if (hits.Count == 0)
            {
                tagHits.Children.Add(Small(Texts.Get((tagFind?.Text ?? "").Trim().Length == 0 && !tagBare ? "tag_all_in" : "tag_no_hits"), new Thickness(0, 4, 0, 0)));
                return;
            }

            // the line underneath first: the rows count up on it without the list being drawn
            // again, so the scroll stays where the player left it
            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var count = new TextBlock
            {
                Text = Texts.F("tag_picked", tagPick.Count), FontSize = 12, Foreground = B("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = tagSel;
            var put = new Button
            {
                Content = Texts.F("tag_add_these", label), Style = (Style)FindResource("Ghost"), MinHeight = 34,
                FontSize = 12, Margin = new Thickness(8, 0, 0, 0), IsEnabled = tagPick.Count > 0,
            };
            put.Click += (s, e) => TagThese(st.Mods.Where(m => tagPick.Contains(m.Id) && !Carries(m, label, false)).ToList(), label);
            DockPanel.SetDock(put, Dock.Right);
            foot.Children.Add(put);
            // one tick for all of them, as in the mod list (the user, 2026-10-25)
            var every = new CheckBox
            {
                Style = (Style)FindResource("Tick"), IsChecked = hits.All(m => tagPick.Contains(m.Id)), VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = Texts.Get("pick_all"), FontSize = 12, Foreground = B("Ink") },
                Margin = new Thickness(8, 0, 16, 0),      // in line with the rows' ticks, inside their bands
            };
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var m in hits) tagPick.Add(m.Id);
                else foreach (var m in hits) tagPick.Remove(m.Id);
                FillTagHits();
            };
            DockPanel.SetDock(every, Dock.Left);
            foot.Children.Add(every);
            foot.Children.Add(count);

            var rows = new StackPanel();
            int hitBand = 0;
            foreach (var m in hits)
            {
                var row = new DockPanel();
                var has = new TextBlock
                {
                    Text = m.Tags.Count > 0 ? string.Join(" · ", m.Tags) : Texts.Get("tag_none_of_mine"), FontSize = 12,
                    Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                DockPanel.SetDock(has, Dock.Right);
                row.Children.Add(has);
                var pic = Picture(m.Icon, GlyphMod, 32, m.Enabled ? "Blue" : "Muted");
                pic.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(pic, Dock.Left);
                row.Children.Add(pic);
                row.Children.Add(new TextBlock { Text = m.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var id = m.Id;
                var box = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = tagPick.Contains(id), Content = row,
                    Margin = new Thickness(0, 3, 0, 3),
                };
                box.Click += (s, e) =>
                {
                    if (box.IsChecked == true) tagPick.Add(id); else tagPick.Remove(id);
                    count.Text = Texts.F("tag_picked", tagPick.Count);
                    put.IsEnabled = tagPick.Count > 0;
                    every.IsChecked = hits.All(x => tagPick.Contains(x.Id));
                };
                rows.Children.Add(Band(box, hitBand++, false));
            }
            tagHits.Children.Add(new ScrollViewer
            {
                Content = rows, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            tagHits.Children.Add(foot);
        }

        /// <summary>One label onto the mods that were ticked: one call and one save, not one of
        /// each per mod.</summary>
        async void TagThese(List<ModRow> mods, string name)
        {
            if (mods.Count == 0 || busy) return;
            foreach (var m in mods) m.Tags.Add(name);
            newTag = null;
            tagPick.Clear();
            tagDone = mods.Count == 1 ? Texts.F("tag_added", mods[0].Name, name) : Texts.F("tag_added_n", mods.Count, name);
            RenderTags();
            var args = new List<string> { "tag-many", name };
            args.AddRange(mods.Select(m => m.Id));
            waiting++;
            var r = await Queued(() => Cli.Run(args.ToArray()));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

        /// <summary>The settings, in a window of its own (the user, 2026-10-24): the language, whether
        /// the explaining lines are shown, who made this and where it comes from.</summary>
        void Settings_Click(object sender, RoutedEventArgs e)
        {
            var w = Dialog.Blank(this, out var body);
            body.Children.Add(new TextBlock { Text = Texts.Get("settings"), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = B("Ink") });

            // the language
            body.Children.Add(Label("set_lang", new Thickness(0, 20, 0, 6)));
            var langs = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            var zh = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setlang", Content = "中文", MinWidth = 96, IsChecked = Texts.Zh };
            var en = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setlang", Content = "English", MinWidth = 96, IsChecked = !Texts.Zh };
            zh.Click += (s, e2) => { Language(true); w.Close(); Settings_Click(sender, e); };
            en.Click += (s, e2) => { Language(false); w.Close(); Settings_Click(sender, e); };
            pair.Children.Add(zh);
            pair.Children.Add(en);
            langs.Child = pair;
            body.Children.Add(langs);

            // profiles: a switch of their own (the user, 2026-10-25)
            body.Children.Add(Label("set_profiles", new Thickness(0, 20, 0, 6)));
            var pfSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = st?.ProfilesOn == true, VerticalAlignment = VerticalAlignment.Center };
            var pfLine = new StackPanel { Orientation = Orientation.Horizontal };
            pfLine.Children.Add(pfSw);
            pfLine.Children.Add(new TextBlock { Text = Texts.Get("set_profiles_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            pfSw.Click += async (s, e2) =>
            {
                viewLibrary = false;
                await Exec(pfSw.IsChecked == true ? "profiles-on" : "profiles-off");
                Refresh();
            };
            body.Children.Add(pfLine);
            body.Children.Add(Small(Texts.Get("set_profiles_note"), new Thickness(0, 8, 0, 0)));

            // which way the list is shown: the winner at the bottom (the load order as it is) or at the top
            body.Children.Add(Label("set_order", new Thickness(0, 20, 0, 6)));
            var orders = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            var two = new StackPanel { Orientation = Orientation.Horizontal };
            var bottomWins = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setorder", Content = Texts.Get("set_order_bottom"), MinWidth = 96, IsChecked = !Texts.TopWins };
            var topWinsBtn = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setorder", Content = Texts.Get("set_order_top"), MinWidth = 96, IsChecked = Texts.TopWins };
            void Direction(bool top)
            {
                if (Texts.TopWins == top) return;
                Texts.ShowTopWins(top);
                rows.Clear();
                List.Children.Clear();
                Words();
                if (st != null) Render();
            }
            bottomWins.Click += (s, e2) => Direction(false);
            topWinsBtn.Click += (s, e2) => Direction(true);
            two.Children.Add(bottomWins);
            two.Children.Add(topWinsBtn);
            orders.Child = two;
            body.Children.Add(orders);
            body.Children.Add(Small(Texts.Get("set_order_note"), new Thickness(0, 8, 0, 0)));

            // the explaining lines
            body.Children.Add(Label("set_hints", new Thickness(0, 20, 0, 6)));
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = Texts.Hints, VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(sw);
            line.Children.Add(new TextBlock { Text = Texts.Get("set_hints_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            sw.Click += (s, e2) =>
            {
                Texts.ShowHints(sw.IsChecked == true);
                rows.Clear();
                List.Children.Clear();
                Words();
                if (st != null) Render();
                if (TagsPage.Visibility == Visibility.Visible) RenderTags();
            };
            body.Children.Add(line);
            body.Children.Add(Small(Texts.Get("set_hints_note"), new Thickness(0, 8, 0, 0)));

            // how the list is worked
            body.Children.Add(Label("set_keys", new Thickness(0, 20, 0, 6)));
            foreach (var key in new[] { "set_key_rename", "set_key_many" })
                body.Children.Add(new TextBlock { Text = "· " + Texts.Get(key), Foreground = B("Ink"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });

            // this program's own updates (the user, 2026-10-25)
            body.Children.Add(Label("set_update", new Thickness(0, 20, 0, 6)));
            body.Children.Add(new TextBlock
            {
                Text = Texts.Get("title") + "  " + StateDoc.Version + "  ·  " + Texts.Get(SelfUpdate.Official ? "build_official" : "build_dev") +
                       (HasNexus ? "" : "  " + Texts.Get("set_no_nexus")),
                FontFamily = Mono, FontSize = 13, Foreground = B("Ink"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
            });
            var upBox = new StackPanel();
            body.Children.Add(upBox);
            string failed = null;
            bool checking = false;
            void FillUpdate()
            {
                upBox.Children.Clear();
                var u = SelfUpdate.Last();
                string when = u.CheckedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(u.CheckedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : null;
                string said = checking ? Texts.Get("self_checking")
                    : failed != null ? Texts.F("self_failed", failed)
                    : u.Newer ? Texts.F("self_new", u.Latest + (u.Prerelease ? Texts.Get("self_pre") : ""))
                    : when == null ? Texts.Get("self_none")
                    : u.Latest == null ? Texts.F("self_no_release", when)
                    : Texts.F("self_current", when);
                upBox.Children.Add(new TextBlock
                {
                    Text = said, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                    Foreground = B(u.Newer && !checking && failed == null ? "Yellow" : "Ink"),
                    FontWeight = u.Newer && !checking && failed == null ? FontWeights.SemiBold : FontWeights.Normal,
                });
                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                if (u.Newer && !string.IsNullOrEmpty(u.Page))
                {
                    var open = new Button { Content = Texts.Get("self_open"), Style = (Style)FindResource("Primary"), MinHeight = 36, Margin = new Thickness(0, 0, 8, 0), ToolTip = u.Page };
                    var page = u.Page;
                    open.Click += (s, e2) => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                    btns.Children.Add(open);
                }
                var check = new Button { Content = Texts.Get("self_check"), Style = (Style)FindResource("Ghost"), MinHeight = 36, IsEnabled = !checking };
                check.Click += async (s, e2) =>
                {
                    checking = true;
                    failed = null;
                    FillUpdate();
                    try { await Task.Run(() => SelfUpdate.Check()); }
                    catch (ModError err) { failed = CheckWhy(err.Kind); }
                    catch (Exception err) when (err is IOException || err is UnauthorizedAccessException || err is TaskCanceledException) { failed = Texts.Get("up_why_offline"); }
                    checking = false;
                    FillUpdate();
                    ShowUpdateDot();
                };
                btns.Children.Add(check);
                upBox.Children.Add(btns);
            }
            FillUpdate();
            var autoSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = SelfUpdate.Auto, VerticalAlignment = VerticalAlignment.Center };
            var autoLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            autoLine.Children.Add(autoSw);
            autoLine.Children.Add(new TextBlock { Text = Texts.Get("self_auto"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            autoSw.Click += (s, e2) =>
            {
                try { SelfUpdate.Auto = autoSw.IsChecked == true; }
                catch (Exception err) when (err is IOException || err is UnauthorizedAccessException) { autoSw.IsChecked = SelfUpdate.Auto; }
            };
            body.Children.Add(autoLine);
            body.Children.Add(Small(Texts.Get(SelfUpdate.Official ? "self_auto_note_official" : "self_auto_note_dev"), new Thickness(0, 8, 0, 0)));

            // who made it, and where it comes from (last)
            body.Children.Add(Label("set_about", new Thickness(0, 20, 0, 6)));
            // which library this window is reading. Two windows on two libraries look exactly
            // alike, and one of them is then showing a library nobody else has (the user,
            // 2026-10-25)
            body.Children.Add(new TextBlock
            {
                Text = Cli.LibraryFile, FontFamily = Mono, FontSize = 11, Foreground = B("Muted"),
                Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(Link(Texts.Get("author_name"), Texts.Get("author_url")));
            body.Children.Add(Link(Texts.Get("project_name"), Texts.Get("project_url")));

            var close = new Button { Content = Texts.Get("close"), Style = (Style)FindResource("Primary"), MinWidth = 96, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            close.Click += (s, e2) => w.Close();
            body.Children.Add(close);
            w.ShowDialog();
        }

        /// <summary>A resource type as the conflicts name it: in words for the common ones, else the engine's name.</summary>
        static string TypeLabel(string type) => Texts.Has("rt_" + type) ? Texts.Get("rt_" + type) : type;

        /// <summary>A line that opens an address in the browser.</summary>
        UIElement Link(string text, string url)
        {
            var b = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2), ToolTip = url };
            b.Content = new TextBlock { Text = text, Foreground = B("Blue"), FontWeight = FontWeights.SemiBold };
            b.Click += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return b;
        }

        /// <summary>To the top of the list and to the end of it. A library grows at the end, so
        /// the one that matters is the end (the user, 2026-10-25).</summary>
        void JumpButtons()
        {
            Mark(TopBtn, GlyphTop, "jump_top");
            Mark(BottomBtn, GlyphBottom, "jump_bottom");
        }

        void Mark(Button b, string glyph, string tip)
        {
            b.Template = Plain();
            b.Cursor = Cursors.Hand;
            b.FocusVisualStyle = null;
            b.ToolTip = Texts.Get(tip);
            b.Content = new Path
            {
                Data = Geometry.Parse(glyph), Width = 17, Height = 17, Stretch = Stretch.Uniform, Margin = new Thickness(8),
                Stroke = B("Muted"), StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
            };
        }

        void Top_Click(object sender, RoutedEventArgs e) => ListScroll.ScrollToTop();

        void Bottom_Click(object sender, RoutedEventArgs e) => ListScroll.ScrollToBottom();

        /// <summary>Where the list was left, so a library that grows at the end does not have to
        /// be scrolled down again on every start (the user, 2026-10-25). Kept in gui.json, taken
        /// up once the first list has been drawn and only while nothing is filtered or
        /// searched.</summary>
        void SaveWhere()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["list_offset"] = (long)Math.Round(ListScroll.VerticalOffset);
                Store.Save("gui.json", d);
            }
            catch (Exception x) when (x is IOException || x is UnauthorizedAccessException || x is FormatException) { }
        }

        bool wentBack;

        void WhereItWas()
        {
            if (wentBack || filter != "all" || searchWords.Length > 0) return;
            wentBack = true;
            long at;
            try { at = Json.Long(Store.Load("gui.json"), "list_offset"); }
            catch (Exception x) when (x is IOException || x is FormatException) { return; }
            if (at <= 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ListScroll.ScrollToVerticalOffset(Math.Min(at, ListScroll.ScrollableHeight));
            }), DispatcherPriority.Loaded);
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        void Deploy_Click(object sender, RoutedEventArgs e)
        {
            if (st != null && st.GameDir == null) ChooseGame();
            else _ = DeployNow();
        }

        Task<bool> DeployNow() => Act(() => Cli.Call(() =>
        {
            var r = Hd2Mod.Deploy.Run(Library.Load(), Store.GameDir());
            return Texts.F("deployed", Texts.N(r.Kept), Texts.N(r.Moved), Texts.N(r.Written), Texts.N(r.Linked), Texts.N(r.Copied),
                           Texts.N(r.Deleted), Texts.N(r.Files));
        }), true);

        /// <summary>The game through Steam; changes not deployed yet are deployed first,
        /// when the player says so.</summary>
        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (Hd2Mod.Deploy.GameRunning()) { Dialog.Tell(this, Texts.Get("game_already")); return; }
            if (st != null && st.GameDir != null && st.Pending > 0)
            {
                if (!Dialog.Ask(this, Texts.F("launch_ask", st.Pending), Texts.Get("deploy_launch"))) return;
                if (!await DeployNow()) return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true });
                Foot(Texts.Get("launching"), "Blue");
            }
            catch (Exception x) when (x is System.ComponentModel.Win32Exception || x is InvalidOperationException)
            {
                Dialog.Tell(this, Texts.Get("no_steam"));
            }
        }

        static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", Cli.Quote(path));
        }

        void Purge_Click(object sender, RoutedEventArgs e)
        {
            if (st != null && st.GameDir == null) ChooseGame();
            else if (Dialog.Ask(this, Texts.Get("purge_ask"), Texts.Get("purge")))
                _ = Act(() => Cli.Call(() => Texts.F("purged", Texts.N(Hd2Mod.Deploy.Purge(Store.GameDir())))), true);
        }

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = AddBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var zip = new MenuItem { Header = Texts.Get("add_zip") };
            zip.Click += (s, a) => { var p = PickZip(); if (p != null) Run("add", p); };
            var folder = new MenuItem { Header = Texts.Get("add_folder") };
            folder.Click += async (s, a) =>
            {
                var p = PickFolder();
                if (p == null) return;
                var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
                if (await Act(() => Cli.Run("add", p), false))
                    AdviseDrive(Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList());
            };
            menu.Items.Add(zip);
            menu.Items.Add(folder);
            var managers = new MenuItem { Header = Texts.Get("st_import") };
            managers.Click += (s, a) => ImportFromManagers(false);
            menu.Items.Add(managers);
            menu.IsOpen = true;
        }

        // ---------------------------------------------------------------- mods dropped on the list

        static readonly string[] Archives = { ".zip", ".7z", ".rar", ".tar", ".gz", ".xz" };

        /// <summary>What a drop from Explorer holds that can be installed: mod folders and
        /// archives, in the order the system gives them.</summary>
        static List<string> Dropped(IDataObject data)
        {
            var paths = data != null && data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            if (paths == null) return new List<string>();
            return paths.Where(p => Directory.Exists(p)
                                    || (System.IO.File.Exists(p) && Archives.Contains(IOPath.GetExtension(p).ToLowerInvariant()))).ToList();
        }

        /// <summary>While mods are dragged over the list: what dropping them would do, over
        /// the whole list, so there is no doubt that it takes them (the user, 2026-10-24).</summary>
        void List_DragOver(object sender, DragEventArgs e)
        {
            var many = Dropped(e.Data);
            bool ok = many.Count > 0 && !busy;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            DropHintText.Text = Texts.Get(ok ? "drop_here" : "drop_no");
            DropHintWhat.Text = ok ? Texts.F("drop_what", Texts.N(many.Count)) : Texts.Get(busy ? "working" : "drop_none");
            DropHint.Visibility = Visibility.Visible;
        }

        void List_DragLeave(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        /// <summary>Mods dropped on the list: each archive or folder added to the library, in
        /// the order dropped, then the first of them chosen and the drive advice given once.</summary>
        async void List_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropHint.Visibility = Visibility.Collapsed;
            var many = Dropped(e.Data);
            if (many.Count == 0) { Dialog.Tell(this, Texts.Get("drop_none")); return; }
            if (busy) return;
            var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
            int added = 0;
            foreach (var p in many) if (await Act(() => Cli.Run("add", p), false)) added++;
            var fresh = Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList();
            if (fresh.Count == 0) return;
            Current(fresh[0].Id);
            Foot(Texts.F("drop_done", Texts.N(added)), "Blue");
            AdviseDrive(fresh);
        }

        string PickZip() =>
            Pick.File(this, Texts.Get("pick_zip_title"), Texts.Get("pick_zip_kind"), "*.zip;*.7z;*.rar;*.tar;*.gz;*.xz");

        string PickFolder(string titleKey = "pick_mod_title") => Pick.Folder(this, Texts.Get(titleKey));

        // ---------------------------------------------------------------- the window's own frame (as HD2 Mod Setting GUI)

        void Min_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();
        void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        void ToggleMax()
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        void FitMaximized()
        {
            bool max = WindowState == WindowState.Maximized;
            MaxGlyph.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
            if (max)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
                int px = GetSystemMetrics(32) + GetSystemMetrics(92);   // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                Frame.Margin = new Thickness(px * sx);
                Frame.BorderThickness = new Thickness(0);
            }
            else
            {
                Frame.Margin = new Thickness(0);
                Frame.BorderThickness = new Thickness(1);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(Hook);
            acrylic = Backdrop.Enable(this);
            if (acrylic) Frame.Background = B("Ground900");
        }

        // Windows 11's snap layouts show only over a real maximize button: over ours the
        // system is told HTMAXBUTTON
        const int WM_NCHITTEST = 0x84, WM_NCMOUSELEAVE = 0x2A2, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSEMOVE = 0xA0, HTMAXBUTTON = 9;

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    if (OverMax(lParam)) { SetMaxHover(true); handled = true; return new IntPtr(HTMAXBUTTON); }
                    SetMaxHover(false);
                    break;
                case WM_NCMOUSELEAVE:
                    SetMaxHover(false);
                    break;
                case WM_NCMOUSEMOVE:
                    if (wParam.ToInt32() != HTMAXBUTTON) SetMaxHover(false);
                    break;
                case WM_NCLBUTTONDOWN:
                    if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
                    break;
                case WM_NCLBUTTONUP:
                    if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; ToggleMax(); return IntPtr.Zero; }
                    break;
            }
            return IntPtr.Zero;
        }

        bool OverMax(IntPtr lParam)
        {
            if (!MaxBtn.IsVisible) return false;
            int v = unchecked((int)lParam.ToInt64());
            var p = new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
            try
            {
                var local = MaxBtn.PointFromScreen(p);
                return local.X >= 0 && local.Y >= 0 && local.X < MaxBtn.ActualWidth && local.Y < MaxBtn.ActualHeight;
            }
            catch (InvalidOperationException) { return false; }
        }

        void SetMaxHover(bool on)
        {
            if (on == overMax) return;
            overMax = on;
            MaxBtn.Background = on ? B("Grid") : Brushes.Transparent;
        }
    }
}
