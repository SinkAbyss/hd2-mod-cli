using System;
using System.IO;
using System.Text;

namespace Hd2Mod
{
    static class Program
    {
        static int Main(string[] args)
        {
            // mod names are often Chinese: UTF-8 to a console and to a pipe alike
            var utf8 = new UTF8Encoding(false);
            try { Console.OutputEncoding = utf8; }
            catch (IOException) { }
            var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            var err = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
            return Commands.Run(args, output, err);
        }
    }
}
