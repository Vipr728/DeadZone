using Ryz1.Contracts;

namespace Ryz1.SimCore;

public static class MacroExpander
{
    public static IReadOnlyList<RyzAction> Expand(MacroActionDto macro)
    {
        int ticks = Math.Max(0, macro.Ticks);
        RyzAction[] actions = new RyzAction[ticks];
        for (int i = 0; i < ticks; i++)
        {
            bool first = i == 0;
            actions[i] = new RyzAction(
                macro.MoveX,
                macro.MoveY,
                macro.Button0Pressed && first,
                macro.Button0Held,
                macro.Button1Pressed && first,
                macro.Button2Held);
        }
        return actions;
    }
}

public sealed record SimSearchConfig
{
    public int BeamWidth { get; init; } = 12;
    public int MaxDepth { get; init; } = 24;
    public int MaxTicksSimulated { get; init; } = 300_000;
    public float NeuralPolicyWeight { get; init; } = 0f;
    public float NeuralValueWeight { get; init; } = 0f;
}

public interface INeuralGuide
{
    NeuralGuideOutput Evaluate(IReadOnlyList<NeuralGuideStep> sequence, int trialId);
}

public sealed record NeuralGuideStep(
    RyzObservationDto Observation,
    int PreviousMacroId,
    float PreviousReward,
    bool PreviousTerminal);

public sealed record NeuralGuideOutput(float[] PolicyLogits, float Value);

public sealed class NullNeuralGuide : INeuralGuide
{
    public static readonly NullNeuralGuide Instance = new();
    public NeuralGuideOutput Evaluate(IReadOnlyList<NeuralGuideStep> sequence, int trialId) =>
        new(Array.Empty<float>(), 0f);
}
