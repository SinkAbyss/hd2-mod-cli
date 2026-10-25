using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickDeploy
{
    /// <summary>The system's own windows for choosing a file or a folder, in their normal
    /// size (the user, 2026-10-24: "太小了"): the shell's IFileDialog — resizable, with the
    /// navigation pane, the places bar and search — in place of the small tree the old
    /// folder browser draws. The file window is WinForms's, which uses IFileDialog on its
    /// own; the folder window asks for IFileDialog here, because WinForms's does not.</summary>
    static class Pick
    {
        /// <summary>One existing file. kind: what the files are called, patterns: "*.zip;*.7z".</summary>
        public static string File(Window owner, string title, string kind, string patterns)
        {
            using (var d = new System.Windows.Forms.OpenFileDialog
            {
                Title = title,
                Filter = kind + "|" + patterns + "|" + Texts.Get("pick_all") + "|*.*",
                AutoUpgradeEnabled = true,
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = false,
            })
                return d.ShowDialog(new Owner(owner)) == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
        }

        /// <summary>One existing folder; start: the folder it opens in, when it is still there.</summary>
        public static string Folder(Window owner, string title, string start = null)
        {
            try { return Shell(owner, title, start); }
            catch (Exception e) when (e is COMException || e is InvalidCastException || e is NotImplementedException)
            {
                // no shell dialog on this system: the old tree, small but working
                using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = title, ShowNewFolderButton = true })
                {
                    if (start != null && Directory.Exists(start)) d.SelectedPath = start;
                    return d.ShowDialog(new Owner(owner)) == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
                }
            }
        }

        static string Shell(Window owner, string title, string start)
        {
            var dialog = (IFileDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out uint opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                if (start != null && Directory.Exists(start))
                {
                    var item = Item(start);
                    if (item != null) dialog.SetFolder(item);
                }
                if (dialog.Show(new WindowInteropHelper(owner).Handle) != 0) return null;   // cancelled
                dialog.GetResult(out var chosen);
                chosen.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                Marshal.ReleaseComObject(chosen);
                return path;
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        static IShellItem Item(string path)
        {
            try
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
                return item as IShellItem;
            }
            catch (Exception e) when (e is COMException || e is FileNotFoundException) { return null; }
        }

        /// <summary>A WPF window as the owner a WinForms dialog wants.</summary>
        class Owner : System.Windows.Forms.IWin32Window
        {
            public Owner(Window w) { Handle = new WindowInteropHelper(w).Handle; }
            public IntPtr Handle { get; }
        }

        // ---------------------------------------------------------------- the shell's dialog

        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bind,
                                                       ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

        [ComImport, ClassInterface(ClassInterfaceType.None), Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        class FileOpenDialog { }

        /// <summary>IFileDialog, in its own order: every method holds its place in the table,
        /// and the ones this window never calls take no arguments.</summary>
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder();
            void GetCurrentSelection();
            void SetFileName();
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel();
            void GetResult(out IShellItem item);
            void AddPlace();
            void SetDefaultExtension();
            void Close();
            void SetClientGuid();
            void ClearClientData();
            void SetFilter();
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes();
            void Compare();
        }
    }
}
