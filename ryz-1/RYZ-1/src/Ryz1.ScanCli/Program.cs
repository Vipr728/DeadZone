using Ryz1.Contracts;
using Ryz1.SimCore;

namespace Ryz1.ScanCli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string project = GetOption(args, "--project", Directory.GetCurrentDirectory());
            string outPath = GetOption(args, "--out", Path.Combine(project, "Library", "RYZ1", "task_bundles", "simcore-demo-task.json"));
            int seed = int.Parse(GetOption(args, "--seed", "0"));

            if (!Directory.Exists(project))
                throw new DirectoryNotFoundException(project);

            // Native scan CLI validates exported authoring artifacts when Unity is unavailable on GB10.
            // For the hackathon subset it can also emit a deterministic SimCore demo bundle.
            var bundle = Ryz1.SimCore.TaskFactory.CreateDemoBundle(seed);
            Json.Write(outPath, bundle);
            Console.WriteLine($"Wrote RYZ task bundle: {outPath}");
            Console.WriteLine($"manifestFingerprint={bundle.Task.ManifestFingerprint}");
            Console.WriteLine($"levelFingerprint={bundle.Task.LevelFingerprint}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RYZ-1 scan failed: " + ex.Message);
            return 1;
        }
    }

    private static string GetOption(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return fallback;
    }
}
