using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using Ryzi.Editor;
using Ryzi.Integrations.ExistingSimulator;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Ryzi.Integrations.Tests.PlayMode
{
    public sealed class ExistingSimulatorProviderTests
    {
        [UnityTest]
        public IEnumerator ScanAndCalibration_FindExplicitSimulatorAndRestoreScene()
        {
            if (SceneManager.GetActiveScene().path != "Assets/Scenes/SampleScene.unity")
            {
                var load = SceneManager.LoadSceneAsync("Assets/Scenes/SampleScene.unity", LoadSceneMode.Single);
                while (!load.isDone)
                    yield return null;
            }

            Scene scene = SceneManager.GetActiveScene();
            bool dirtyBefore = scene.isDirty;
            SceneDiscoveryResult discovery = new ProjectScanner().ScanCurrentScene();
            ExistingSimulatorProvider provider = new ExistingSimulatorProvider();

            Assert.That(provider.CanHandle(discovery, out string reason), Is.True, reason);
            CalibrationReport report = null;
            LogAssert.Expect(
                LogType.Error,
                new Regex("More than one global light on layer Default"));
            yield return provider.Calibrate(discovery, CancellationToken.None, value => report = value);

            Assert.That(report, Is.Not.Null);
            Assert.That(report.completed, Is.True);
            Assert.That(report.probes.Length, Is.GreaterThanOrEqualTo(6));
            Assert.That(report.stateRestored, Is.True);
            Assert.That(report.deterministicRepeatability, Is.True);
            Assert.That(scene.isDirty, Is.EqualTo(dirtyBefore));
        }

        [UnityTest]
        public IEnumerator Calibration_PreCancelledToken_ReportsCancellationAndUnloads()
        {
            if (SceneManager.GetActiveScene().path != "Assets/Scenes/SampleScene.unity")
            {
                var load = SceneManager.LoadSceneAsync("Assets/Scenes/SampleScene.unity", LoadSceneMode.Single);
                while (!load.isDone)
                    yield return null;
            }

            SceneDiscoveryResult discovery = new ProjectScanner().ScanCurrentScene();
            ExistingSimulatorProvider provider = new ExistingSimulatorProvider();
            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            CalibrationReport report = null;

            LogAssert.Expect(
                LogType.Error,
                new Regex("More than one global light on layer Default"));
            yield return provider.Calibrate(discovery, cancellation.Token, value => report = value);
            Assert.That(report, Is.Not.Null);
            Assert.That(report.cancelled, Is.True);
        }
    }
}
