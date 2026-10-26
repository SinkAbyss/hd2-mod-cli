using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickDeploy
{
    /// <summary>Text a player may want to copy: a name, a version, a path, a script's name, an
    /// error. It is drawn like the text around it, can be selected with the mouse and copied
    /// with Ctrl+C, and right-click → Copy copies the selection, or all of it when nothing is
    /// selected (the user, 2026-10-26). Labels, counts and buttons stay plain text.</summary>
    static class Selectable
    {
        static Brush B(string key) => (Brush)Application.Current.Resources[key];

        public static TextBox Text(string text, double size, string colour = "Ink", FontFamily font = null, double lineHeight = 0)
        {
            var t = new TextBox
            {
                Style = (Style)Application.Current.Resources["Copyable"], Text = text ?? "", FontSize = size, Foreground = B(colour),
            };
            if (font != null) t.FontFamily = font;
            if (lineHeight > 0) t.SetValue(TextBlock.LineHeightProperty, lineHeight);
            Menu(t, () => t.SelectionLength > 0 ? t.SelectedText : t.Text);
            return t;
        }

        /// <summary>Right-click → Copy on text that is cut short on screen (a line with "…"):
        /// the whole of it is copied.</summary>
        public static void Menu(FrameworkElement e, Func<string> text)
        {
            var item = new MenuItem { Header = Texts.Get("copy") };
            item.Click += (s, a) =>
            {
                var x = text();
                if (string.IsNullOrEmpty(x)) return;
                try { Clipboard.SetText(x); } catch (Exception) { }      // another program holding the clipboard: nothing copied
            };
            var menu = new ContextMenu();
            menu.Items.Add(item);
            e.ContextMenu = menu;
        }
    }
}
