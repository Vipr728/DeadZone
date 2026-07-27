using Ryz1.Contracts;

namespace Ryz1.SimCore;

public sealed record SimSolveResult
{
    public bool Solved { get; init; }
    public IReadOnlyList<int> MacroIds { get; init; } = Array.Empty<int>();
    public int NodesExpanded { get; init; }
    public int DeathsPruned { get; init; }
    public int TicksSimulated { get; init; }
    public string Diagnostic { get; init; } = "";
    public float FurthestProgress { get; init; }
    public ReplayRecordDto Replay { get; init; } = new();
    public DatasetFileDto Dataset { get; init; } = new();
}

public sealed class SimBeamSearch
{
    sealed record Node(
        int Id,
        int ParentId,
        int Depth,
        List<int> MacroIds,
        RyzObservationDto Observation,
        List<NeuralGuideStep> GuideSequence,
        float BaselineScore,
        float Score);

    public SimSolveResult Solve(RyzTaskBundleDto bundle, SimSearchConfig config, INeuralGuide? guide = null, int trialId = 0)
    {
        guide ??= NullNeuralGuide.Instance;
        var macros = bundle.Task.ActionSchema.Macros.Where(m => m.IsValid).OrderBy(m => m.Id).ToArray();
        var frontier = new List<Node>();
        var rootSim = new PlatformerSim(bundle.Task);
        var rootObs = rootSim.Observe();
        frontier.Add(new Node(
            0,
            -1,
            0,
            new List<int>(),
            rootObs,
            new List<NeuralGuideStep> { new(rootObs, -1, 0f, false) },
            Score(rootObs, null, bundle.Task),
            Score(rootObs, null, bundle.Task)));

        int nextId = 1;
        int expanded = 0;
        int deaths = 0;
        int ticks = 0;
        float furthest = rootObs.Progress;
        var transitions = new List<DatasetTransitionDto>();

        for (int depth = 0; depth < config.MaxDepth && ticks < config.MaxTicksSimulated; depth++)
        {
            int depthTransitionStart = transitions.Count;
            var candidates = new List<Node>();
            foreach (Node parent in frontier)
            {
                NeuralGuideOutput neural = guide.Evaluate(parent.GuideSequence, trialId);
                float[] policyPriors = Softmax(neural.PolicyLogits);
                foreach (MacroActionDto macro in macros)
                {
                    if (ticks >= config.MaxTicksSimulated)
                        break;

                    var sim = new PlatformerSim(bundle.Task);
                    foreach (int id in parent.MacroIds)
                    {
                        MacroActionDto existing = macros.First(m => m.Id == id);
                        foreach (RyzAction action in MacroExpander.Expand(existing))
                            sim.Tick(action);
                    }

                    RyzObservationDto before = sim.Observe();
                    foreach (RyzAction action in MacroExpander.Expand(macro))
                    {
                        sim.Tick(action);
                        ticks++;
                        if (sim.State.IsDead || sim.State.IsComplete)
                            break;
                    }
                    RyzObservationDto after = sim.Observe();
                    expanded++;
                    furthest = MathF.Max(furthest, after.Progress);
                    float prior = policyPriors.Length > macro.Id ? policyPriors[macro.Id] : 0f;
                    float baselineScore = Score(after, macro, bundle.Task);
                    float candidateScore = baselineScore
                        + config.NeuralPolicyWeight * prior
                        + config.NeuralValueWeight * neural.Value;
                    bool death = after.IsDead;
                    float reward = Reward(before, after, bundle.Task);
                    var ids = new List<int>(parent.MacroIds) { macro.Id };
                    var guideSequence = new List<NeuralGuideStep>(parent.GuideSequence)
                    {
                        new(after, macro.Id, reward, death || after.IsComplete)
                    };
                    var child = new Node(
                        nextId++,
                        parent.Id,
                        depth + 1,
                        ids,
                        after,
                        guideSequence,
                        baselineScore,
                        candidateScore);

                    if (death)
                        deaths++;
                    else
                        candidates.Add(child);

                    transitions.Add(new DatasetTransitionDto
                    {
                        TaskId = bundle.Task.TaskId,
                        TrialId = trialId,
                        NodeId = child.Id,
                        ParentId = parent.Id,
                        SearchDepth = child.Depth,
                        MacroId = macro.Id,
                        Reward = reward,
                        Progress = after.Progress,
                        Death = death,
                        Completion = after.IsComplete,
                        TeacherSelected = after.IsComplete,
                        SurvivedPruning = false,
                        EventuallyCompleted = after.IsComplete,
                        CandidateScore = candidateScore,
                        MechanicsVector = bundle.Task.MechanicsVector.Values.ToArray(),
                        Observation = before,
                        NextObservation = after,
                    });

                    if (after.IsComplete)
                    {
                        MarkTeacherSelections(transitions, depthTransitionStart);
                        MarkWinningPath(transitions, child.Id);
                        return Finish(bundle, child.MacroIds, expanded, deaths, ticks, "completed", furthest, transitions);
                    }
                }
            }

            MarkTeacherSelections(transitions, depthTransitionStart);
            if (candidates.Count == 0)
                break;
            int width = Math.Max(1, config.BeamWidth);
            List<Node> ranked = candidates
                .OrderByDescending(n => n.Score)
                .ThenBy(n => n.Id)
                .Take(width)
                .ToList();
            // The neural guide may add candidates, but it may never remove the
            // deterministic baseline's best candidate. This keeps a weak or
            // out-of-distribution policy from regressing verified search.
            Node baselineBest = candidates
                .OrderByDescending(n => n.BaselineScore)
                .ThenBy(n => n.Id)
                .First();
            if (ranked.All(node => node.Id != baselineBest.Id))
                ranked[ranked.Count - 1] = baselineBest;
            frontier = ranked;
            HashSet<int> survivingNodeIds = frontier.Select(node => node.Id).ToHashSet();
            for (int index = depthTransitionStart; index < transitions.Count; index++)
            {
                DatasetTransitionDto transition = transitions[index];
                if (survivingNodeIds.Contains(transition.NodeId))
                    transitions[index] = transition with { SurvivedPruning = true };
            }
        }

        return Finish(bundle, Array.Empty<int>(), expanded, deaths, ticks, "search exhausted", furthest, transitions);
    }

    static void MarkTeacherSelections(List<DatasetTransitionDto> transitions, int startIndex)
    {
        IEnumerable<IGrouping<int, (DatasetTransitionDto Transition, int Index)>> groups = transitions
            .Select((transition, index) => (Transition: transition, Index: index))
            .Where(item => item.Index >= startIndex)
            .GroupBy(item => item.Transition.ParentId);
        foreach (IGrouping<int, (DatasetTransitionDto Transition, int Index)> group in groups)
        {
            (DatasetTransitionDto Transition, int Index) best = group
                .Where(item => !item.Transition.Death)
                .DefaultIfEmpty(group.First())
                .OrderByDescending(item => item.Transition.Completion)
                .ThenByDescending(item => item.Transition.CandidateScore)
                .ThenBy(item => item.Transition.NodeId)
                .First();
            transitions[best.Index] = best.Transition with { TeacherSelected = true };
        }
    }

    static void MarkWinningPath(List<DatasetTransitionDto> transitions, int winningNodeId)
    {
        Dictionary<int, int> indicesByNode = transitions
            .Select((transition, index) => (transition.NodeId, Index: index))
            .ToDictionary(item => item.NodeId, item => item.Index);
        int nodeId = winningNodeId;
        while (indicesByNode.TryGetValue(nodeId, out int index))
        {
            DatasetTransitionDto transition = transitions[index];
            transitions[index] = transition with
            {
                TeacherSelected = true,
                SurvivedPruning = true,
                EventuallyCompleted = true,
            };
            nodeId = transition.ParentId;
        }
    }

    static float Score(RyzObservationDto obs, MacroActionDto? macro, RyzTaskSpecDto task)
    {
        float stable = obs.IsGrounded ? 0.02f : 0f;
        float death = obs.IsDead ? -10f : 0f;
        float completion = obs.IsComplete ? 10f : 0f;
        float macroPenalty = macro == null ? 0f : -macro.Ticks * 0.0005f;
        return obs.Progress + stable + death + completion + macroPenalty;
    }

    static float[] Softmax(float[] logits)
    {
        if (logits.Length == 0)
            return Array.Empty<float>();
        float max = logits.Max();
        float[] probabilities = new float[logits.Length];
        float sum = 0f;
        for (int index = 0; index < logits.Length; index++)
        {
            float value = MathF.Exp(Math.Clamp(logits[index] - max, -30f, 30f));
            probabilities[index] = value;
            sum += value;
        }
        if (sum <= 0f || !float.IsFinite(sum))
            return new float[logits.Length];
        for (int index = 0; index < probabilities.Length; index++)
            probabilities[index] /= sum;
        return probabilities;
    }

    static float Reward(RyzObservationDto before, RyzObservationDto after, RyzTaskSpecDto task)
    {
        float reward = (after.Progress - before.Progress) * task.Reward.ProgressScale + task.Reward.TickPenalty;
        if (after.IsComplete)
            reward += task.Reward.CompletionReward;
        if (after.IsDead)
            reward += task.Reward.DeathPenalty;
        return reward;
    }

    static SimSolveResult Finish(
        RyzTaskBundleDto bundle,
        IReadOnlyList<int> macroIds,
        int expanded,
        int deaths,
        int ticks,
        string diagnostic,
        float furthest,
        IReadOnlyList<DatasetTransitionDto> transitions)
    {
        ReplayRecordDto replay = Verify(bundle, macroIds);
        var dataset = new DatasetFileDto
        {
            DatasetId = $"{bundle.Task.TaskId}-{bundle.Task.RandomizationSeed}",
            Split = "unsplit",
            TaskIds = new[] { bundle.Task.TaskId },
            Transitions = transitions,
        };
        return new SimSolveResult
        {
            Solved = replay.Completed && replay.Verified,
            MacroIds = macroIds,
            NodesExpanded = expanded,
            DeathsPruned = deaths,
            TicksSimulated = ticks,
            Diagnostic = diagnostic,
            FurthestProgress = furthest,
            Replay = replay,
            Dataset = dataset,
        };
    }

    public static ReplayRecordDto Verify(RyzTaskBundleDto bundle, IReadOnlyList<int> macroIds)
    {
        var sim = new PlatformerSim(bundle.Task);
        var keyframes = new List<RyzObservationDto> { sim.Observe() };
        var macros = bundle.Task.ActionSchema.Macros.ToDictionary(m => m.Id);
        foreach (int id in macroIds)
        {
            if (!macros.TryGetValue(id, out MacroActionDto? macro))
                return new ReplayRecordDto { TaskId = bundle.Task.TaskId, ManifestFingerprint = bundle.Task.ManifestFingerprint, MacroIds = macroIds, Verified = false, Diagnostic = $"unknown macro {id}" };
            foreach (RyzAction action in MacroExpander.Expand(macro))
            {
                sim.Tick(action);
                if (sim.State.Tick % 10 == 0 || sim.State.IsComplete || sim.State.IsDead)
                    keyframes.Add(sim.Observe());
                if (sim.State.IsComplete || sim.State.IsDead)
                    break;
            }
            if (sim.State.IsComplete || sim.State.IsDead)
                break;
        }

        return new ReplayRecordDto
        {
            TaskId = bundle.Task.TaskId,
            ManifestFingerprint = bundle.Task.ManifestFingerprint,
            MacroIds = macroIds,
            Keyframes = keyframes,
            Completed = sim.State.IsComplete,
            Verified = !sim.State.IsDead && sim.State.IsComplete,
            Diagnostic = sim.State.IsComplete ? "verified in SimCore" : sim.State.IsDead ? "died during replay" : "did not complete",
        };
    }
}
