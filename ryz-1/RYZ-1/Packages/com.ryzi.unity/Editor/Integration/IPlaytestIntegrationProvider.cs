using System;
using System.Collections;
using System.Threading;

namespace Ryzi.Editor
{
    public interface IPlaytestIntegrationProvider
    {
        string Id { get; }
        string DisplayName { get; }
        bool RequiresPlayMode { get; }
        bool CanHandle(SceneDiscoveryResult discovery, out string reason);

        IEnumerator Calibrate(
            SceneDiscoveryResult discovery,
            CancellationToken cancellationToken,
            Action<CalibrationReport> completed);

        IEnumerator RunTest(
            SceneDiscoveryResult discovery,
            MechanicsManifest manifest,
            PlayerProfile profile,
            CancellationToken cancellationToken,
            Action<SimulationRunReport> completed);

        IEnumerator RunCounterfactual(
            SceneDiscoveryResult discovery,
            MechanicsManifest manifest,
            SimulationRunReport baseline,
            CancellationToken cancellationToken,
            Action<CounterfactualReport> completed);
    }

    /// <summary>
    /// Optional provider capability for replaying a recorded action stream through a visible, real Unity scene.
    /// The Editor package owns the controls; the integration owns scene binding and simulation-specific playback.
    /// </summary>
    public interface IGameViewReplayProvider
    {
        bool IsGameViewReplayActive { get; }
        bool IsGameViewReplayComplete { get; }
        int GameViewReplayTick { get; }

        IEnumerator StartGameViewReplay(
            SceneDiscoveryResult discovery,
            ReplayRecord replay,
            CancellationToken cancellationToken,
            Action<string> completed);

        void PlayGameViewReplay();
        void PauseGameViewReplay();
        void StepGameViewReplay();
        void RestartGameViewReplay();
        void JumpGameViewReplayToTick(int tick);
        void StopGameViewReplay();
    }
}
