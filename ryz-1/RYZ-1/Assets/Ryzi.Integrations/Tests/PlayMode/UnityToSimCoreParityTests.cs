using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Ryzi.Integrations.Tests.PlayMode
{
    public sealed class UnityToSimCoreParityTests
    {
        [Test]
        public void SimCoreNativeRunner_PublishesComparableReplayArtifacts()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string dotnet = FindExecutable("dotnet");
            if (string.IsNullOrEmpty(dotnet))
                Assert.Ignore("dotnet SDK not found; Unity-to-SimCore parity requires the native .NET toolchain.");

            string outDir = Path.Combine(root, "Library", "RYZ1", "parity", "unity-playmode");
            Directory.CreateDirectory(outDir);

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = "run --project src/Ryz1.Runner/Ryz1.Runner.csproj -- --seed 0 --beam 4 --depth 4 --out \"" + outDir + "\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process process = Process.Start(start);
            Assert.That(process, Is.Not.Null);
            Assert.That(process.WaitForExit(120000), Is.True, "SimCore runner timed out.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.That(process.ExitCode == 0 || process.ExitCode == 2, Is.True, stdout + "\n" + stderr);
            Assert.That(File.Exists(Path.Combine(outDir, "task_bundle.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "replay.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "dataset.json")), Is.True);
        }

        static string FindExecutable(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] parts = path.Split(Path.PathSeparator);
            for (int i = 0; i < parts.Length; i++)
            {
                string candidate = Path.Combine(parts[i], name);
                if (File.Exists(candidate))
                    return candidate;
            }
            return string.Empty;
        }
    }
}
