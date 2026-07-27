using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Ryz1.Contracts;
using Ryz1.SimCore;

namespace Ryz1.Runner;

/// <summary>
/// Runs the mechanics-conditioned RYZ-1 policy/value model locally.
/// SimCore remains authoritative: this guide only ranks search candidates,
/// and every returned plan is still replay-verified by SimCore.
/// </summary>
internal sealed class OnnxNeuralGuide : INeuralGuide, IDisposable
{
    private const int HistorySize = 4;

    private readonly InferenceSession _session;
    private readonly float[] _mechanics;
    private readonly int _playerSize;
    private readonly int _macroCount;
    private readonly int _sequenceLength;

    public OnnxNeuralGuide(
        string modelPath,
        IReadOnlyList<float> mechanics,
        int playerSize,
        int macroCount,
        int sequenceLength)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Neural guide ONNX model was not found.", modelPath);

        _session = new InferenceSession(modelPath);
        _playerSize = playerSize;
        _macroCount = macroCount;
        _sequenceLength = Math.Max(1, sequenceLength);
        _mechanics = mechanics.ToArray();

        ValidateInput("player", _playerSize);
        ValidateInput("mechanics", _mechanics.Length);
        ValidateInput("history", HistorySize);
    }

    public NeuralGuideOutput Evaluate(IReadOnlyList<NeuralGuideStep> sequence, int trialId)
    {
        if (sequence.Count == 0)
            throw new InvalidOperationException("Neural guide sequence is empty.");

        int offset = Math.Max(0, sequence.Count - _sequenceLength);
        int length = sequence.Count - offset;
        float[] playerValues = new float[length * _playerSize];
        float[] mechanicsValues = new float[length * _mechanics.Length];
        float[] historyValues = new float[length * HistorySize];
        for (int index = 0; index < length; index++)
        {
            NeuralGuideStep step = sequence[offset + index];
            if (step.Observation.PlayerVector.Length != _playerSize)
            {
                throw new InvalidOperationException(
                    $"Observation has {step.Observation.PlayerVector.Length} player values; " +
                    $"the model expects {_playerSize}.");
            }
            Array.Copy(step.Observation.PlayerVector, 0, playerValues, index * _playerSize, _playerSize);
            Array.Copy(_mechanics, 0, mechanicsValues, index * _mechanics.Length, _mechanics.Length);
            historyValues[index * HistorySize] =
                step.PreviousMacroId < 0 ? 0f : (float)step.PreviousMacroId / Math.Max(1, _macroCount - 1);
            historyValues[index * HistorySize + 1] = step.PreviousReward;
            historyValues[index * HistorySize + 2] = step.PreviousTerminal ? 1f : 0f;
            historyValues[index * HistorySize + 3] = 1f;
        }

        var player = new DenseTensor<float>(
            playerValues,
            new[] { 1, length, _playerSize });
        var mechanics = new DenseTensor<float>(
            mechanicsValues,
            new[] { 1, length, _mechanics.Length });
        var history = new DenseTensor<float>(
            historyValues,
            new[] { 1, length, HistorySize });

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("player", player),
            NamedOnnxValue.CreateFromTensor("mechanics", mechanics),
            NamedOnnxValue.CreateFromTensor("history", history),
        };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(inputs);
        DisposableNamedOnnxValue[] outputs = results.ToArray();
        if (outputs.Length < 2)
            throw new InvalidOperationException("RYZ-1 ONNX model did not return policy and value outputs.");

        float[] logits = outputs[0].AsTensor<float>().ToArray();
        if (logits.Length < _macroCount)
        {
            throw new InvalidOperationException(
                $"Model returned {logits.Length} policy logits; {_macroCount} are required.");
        }
        float[] valueOutput = outputs[1].AsTensor<float>().ToArray();
        float value = valueOutput.Length == 0 ? 0f : valueOutput[^1];
        return new NeuralGuideOutput(logits.Skip(logits.Length - _macroCount).ToArray(), value);
    }

    public void Dispose() => _session.Dispose();

    private void ValidateInput(string name, int expectedWidth)
    {
        if (!_session.InputMetadata.TryGetValue(name, out NodeMetadata? metadata))
            throw new InvalidOperationException($"RYZ-1 ONNX model is missing input '{name}'.");
        int actualWidth = metadata.Dimensions.LastOrDefault();
        if (actualWidth > 0 && actualWidth != expectedWidth)
        {
            throw new InvalidOperationException(
                $"Model input '{name}' has width {actualWidth}; expected {expectedWidth}.");
        }
    }
}
