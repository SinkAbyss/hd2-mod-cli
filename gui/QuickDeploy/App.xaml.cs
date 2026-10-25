using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Windows;

namespace QuickDeploy
{
    public partial class App : Application
    {
        /// <summary>The pipe a running window listens on: one per Windows user and settings
        /// folder (a window on a test library never takes the player's links).</summary>
        public static readonly string Pipe = "HD2ModQuickDeploy-" + Environment.UserName + "-" + HomeTag();

        static string HomeTag()
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Hd2Mod.Store.Home.ToLowerInvariant().TrimEnd('\\')))
                                        .Take(6).Select(b => b.ToString("x2")));
        }


        static System.Threading.Mutex single;

        /// <summary>One window: a start while it runs brings it forward and ends.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            // the first window holds this; a second start knows at once (no wait on the pipe)
            single = new System.Threading.Mutex(true, Pipe, out bool first);
            if (!first)
            {
                try
                {
                    using (var c = new NamedPipeClientStream(".", Pipe, PipeDirection.Out))
                    {
                        c.Connect(5000);
                        var bytes = Encoding.UTF8.GetBytes("\n");
                        c.Write(bytes, 0, bytes.Length);
                        c.Flush();
                    }
                    Shutdown();
                    return;
                }
                catch (Exception x) when (x is TimeoutException || x is IOException) { }   // it does not answer: open anyway
            }
            base.OnStartup(e);
            new MainWindow().Show();
        }
    }
}
