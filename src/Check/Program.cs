using System.Text;
using AllStarsModManager.Compat;

namespace AllStarsModManager.Check;

/// <summary>Entry point of the manager-free checker. All behaviour is in AllStarsModManager.Core (CheckCli) so the manager and tests share it.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        try
        {
            return CheckCli.Run(args, Console.Out, Console.Error);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("AllStarsModCheck: unexpected error: " + e.GetType().Name + ": " + CheckOutput.ScrubPaths(e.Message, Array.Empty<string>()));
            Console.Error.WriteLine("No file was changed. Please report this with the folder layout that triggered it.");
            return 70;
        }
    }
}
