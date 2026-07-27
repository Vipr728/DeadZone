using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Ryzi.Editor
{
    public sealed class ProjectScanner
    {
        static readonly string[] MovementTerms =
            { "move", "velocity", "speed", "jump", "ground", "wall", "dash", "climb", "motor" };
        static readonly string[] InputTerms =
            { "input", "keyboard", "gamepad", "button", "axis", "virtual" };
        static readonly string[] ResetTerms =
            { "reset", "respawn", "restart", "checkpoint", "spawn" };
        static readonly string[] DeathTerms =
            { "death", "dead", "died", "die", "kill", "respawn" };
        static readonly string[] CompletionTerms =
            { "goal", "complete", "finish", "exit", "win" };
        static readonly BindingFlags Members =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public SceneDiscoveryResult ScanCurrentScene()
        {
            Stopwatch watch = Stopwatch.StartNew();
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("No loaded active scene is available to scan.");

            bool dirtyBefore = scene.isDirty;
            List<GameObject> sceneObjects = CollectSceneObjects(scene);
            List<MonoBehaviour> behaviours = CollectBehaviours(sceneObjects);
            List<DiscoveryCandidate<GameObject>> players = RankPlayers(sceneObjects, behaviours);
            GameObject selected = players.Count > 0 ? players[0].Value : null;

            List<DiscoveryCandidate<MonoBehaviour>> movement =
                RankBehaviours(selected, behaviours, MovementTerms, "movement");
            List<DiscoveryCandidate<MonoBehaviour>> reset =
                RankBehaviours(selected, behaviours, ResetTerms, "reset");
            List<DiscoveryCandidate<MonoBehaviour>> death =
                RankBehaviours(selected, behaviours, DeathTerms, "death");
            List<DiscoveryCandidate<MonoBehaviour>> completion =
                RankBehaviours(null, behaviours, CompletionTerms, "completion");

            string fingerprint = BuildFingerprint(scene, behaviours);
            MechanicsManifest manifest = BuildManifest(
                scene, fingerprint, selected, behaviours, movement, reset, death, completion);

            watch.Stop();
            if (scene.isDirty != dirtyBefore)
                throw new InvalidOperationException("Scanning unexpectedly changed the active scene dirty state.");

            SceneDiscoveryResult result = new SceneDiscoveryResult
            {
                ScenePath = scene.path,
                SceneName = scene.name,
                Fingerprint = fingerprint,
                DurationMilliseconds = watch.ElapsedMilliseconds,
                SceneWasDirty = dirtyBefore,
                PlayerCandidates = players.ToArray(),
                MovementCandidates = movement.ToArray(),
                ResetCandidates = reset.ToArray(),
                DeathCandidates = death.ToArray(),
                CompletionCandidates = completion.ToArray(),
                Manifest = manifest
            };
            WriteCache(result);
            return result;
        }

        static List<GameObject> CollectSceneObjects(Scene scene)
        {
            List<GameObject> result = new List<GameObject>(256);
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform[] transforms = roots[i].GetComponentsInChildren<Transform>(true);
                for (int j = 0; j < transforms.Length; j++)
                    result.Add(transforms[j].gameObject);
            }
            return result;
        }

        static List<MonoBehaviour> CollectBehaviours(List<GameObject> objects)
        {
            List<MonoBehaviour> result = new List<MonoBehaviour>(128);
            for (int i = 0; i < objects.Count; i++)
            {
                MonoBehaviour[] found = objects[i].GetComponents<MonoBehaviour>();
                for (int j = 0; j < found.Length; j++)
                {
                    if (found[j] != null)
                        result.Add(found[j]);
                }
            }
            return result;
        }

        static List<DiscoveryCandidate<GameObject>> RankPlayers(
            List<GameObject> objects,
            List<MonoBehaviour> behaviours)
        {
            List<DiscoveryCandidate<GameObject>> result = new List<DiscoveryCandidate<GameObject>>();
            for (int i = 0; i < objects.Count; i++)
            {
                GameObject candidate = objects[i];
                List<DiscoveryEvidence> evidence = new List<DiscoveryEvidence>();
                float score = 0f;

                if (string.Equals(candidate.tag, "Player", StringComparison.Ordinal))
                    Add(evidence, ref score, "player-tag", "Tagged Player.", "Scene tag", 0.38f);
                if (candidate.GetComponent<Rigidbody2D>() != null)
                    Add(evidence, ref score, "rigidbody2d", "Owns a Rigidbody2D.", "Component", 0.16f);
                if (candidate.GetComponent<Collider2D>() != null)
                    Add(evidence, ref score, "collider2d", "Owns a primary Collider2D.", "Component", 0.10f);
                if (candidate.activeInHierarchy)
                    Add(evidence, ref score, "active-scene", "Active in the loaded scene.", "Scene", 0.04f);

                MonoBehaviour[] owned = candidate.GetComponents<MonoBehaviour>();
                for (int j = 0; j < owned.Length; j++)
                {
                    if (owned[j] == null)
                        continue;
                    Type type = owned[j].GetType();
                    if (CountMemberMatches(type, MovementTerms) >= 3)
                        Add(evidence, ref score, "movement-members",
                            $"{type.FullName} exposes multiple movement-related members.", type.Assembly.GetName().Name, 0.17f);
                    string inputSource = type.Assembly.GetName().Name;
                    if (CountMemberMatches(type, InputTerms) >= 2 || SourceContainsInput(owned[j], out inputSource))
                        Add(evidence, ref score, "input-consumer",
                            $"{type.FullName} contains an input path.", inputSource, 0.16f);
                    if (CountMemberMatches(type, ResetTerms) > 0)
                        Add(evidence, ref score, "episode-lifecycle",
                            $"{type.FullName} exposes reset/respawn members.", type.Assembly.GetName().Name, 0.08f);
                }

                if (score >= 0.12f)
                    result.Add(new DiscoveryCandidate<GameObject>(candidate, score, evidence));
            }

            result.Sort((a, b) =>
            {
                int confidence = b.Confidence.CompareTo(a.Confidence);
                return confidence != 0
                    ? confidence
                    : string.CompareOrdinal(HierarchyPath(a.Value), HierarchyPath(b.Value));
            });
            return result;
        }

        static List<DiscoveryCandidate<MonoBehaviour>> RankBehaviours(
            GameObject preferredRoot,
            List<MonoBehaviour> all,
            string[] terms,
            string category)
        {
            List<DiscoveryCandidate<MonoBehaviour>> result = new List<DiscoveryCandidate<MonoBehaviour>>();
            for (int i = 0; i < all.Count; i++)
            {
                MonoBehaviour behaviour = all[i];
                Type type = behaviour.GetType();
                int matches = CountMemberMatches(type, terms);
                if (matches == 0 && !ContainsAny(type.Name, terms))
                    continue;

                List<DiscoveryEvidence> evidence = new List<DiscoveryEvidence>();
                float score = Mathf.Min(0.72f, 0.16f + matches * 0.07f);
                evidence.Add(new DiscoveryEvidence
                {
                    id = category + "-members",
                    summary = $"{type.FullName} has {matches} {category}-related member match(es).",
                    source = type.Assembly.GetName().Name,
                    level = EvidenceLevel.SourceCandidate,
                    weight = score
                });
                if (preferredRoot != null && behaviour.transform.IsChildOf(preferredRoot.transform))
                {
                    score += 0.20f;
                    evidence.Add(new DiscoveryEvidence
                    {
                        id = "selected-player-owner",
                        summary = "Component belongs to the selected player root.",
                        source = HierarchyPath(preferredRoot),
                        level = EvidenceLevel.SourceVerified,
                        weight = 0.20f
                    });
                }
                result.Add(new DiscoveryCandidate<MonoBehaviour>(behaviour, score, evidence));
            }

            result.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            return result;
        }

        static MechanicsManifest BuildManifest(
            Scene scene,
            string fingerprint,
            GameObject player,
            List<MonoBehaviour> all,
            List<DiscoveryCandidate<MonoBehaviour>> movement,
            List<DiscoveryCandidate<MonoBehaviour>> reset,
            List<DiscoveryCandidate<MonoBehaviour>> death,
            List<DiscoveryCandidate<MonoBehaviour>> completion)
        {
            List<ActionChannelDefinition> actions = DiscoverActionChannels(player);
            List<MechanicDefinition> mechanics = new List<MechanicDefinition>();
            for (int i = 0; i < actions.Count; i++)
            {
                ActionChannelDefinition action = actions[i];
                mechanics.Add(new MechanicDefinition
                {
                    id = "mechanic." + action.id,
                    suggestedName = action.suggestedName,
                    trigger = new ActionPattern
                    {
                        channelIds = new[] { action.id },
                        description = "Candidate effect of action channel " + action.id
                    },
                    staticConfidence = action.confidence,
                    runtimeConfidence = 0f
                });
            }

            List<EntityAffordanceDefinition> affordances = new List<EntityAffordanceDefinition>();
            for (int i = 0; i < all.Count; i++)
            {
                string typeName = all[i].GetType().Name;
                string[] flags = AffordanceFlags(typeName);
                if (flags.Length == 0)
                    continue;
                affordances.Add(new EntityAffordanceDefinition
                {
                    runtimeTypeId = all[i].GetType().FullName,
                    suggestedName = typeName,
                    flags = flags,
                    confidence = 0.72f,
                    evidenceLevel = EvidenceLevel.SourceCandidate
                });
            }

            List<TunableDefinition> tunables = DiscoverTunables(player);
            List<DiscoveryIssue> issues = new List<DiscoveryIssue>();
            if (player == null)
                issues.Add(Issue("no-player", "Error", "No probable player was found.", "Select a player root or implement an adapter."));
            if (actions.Count == 0)
                issues.Add(Issue("no-input", "Error", "No input path was traceable.", "Confirm input channels or implement an adapter."));
            if (reset.Count == 0)
                issues.Add(Issue("no-reset", "Error", "No reset or respawn candidate was found.", "Confirm a reset method."));
            if (death.Count == 0)
                issues.Add(Issue("no-death", "Warning", "No death behavior was found.", "Confirm whether the level has failure states."));
            if (completion.Count == 0)
                issues.Add(Issue("no-completion", "Error", "No completion behavior was found.", "Confirm a goal or completion trigger."));

            return new MechanicsManifest
            {
                scenarioId = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path,
                sourceFingerprint = fingerprint,
                actions = actions.ToArray(),
                observations = new[]
                {
                    Observation("player.position", "Vector2", movement.Count > 0 ? 0.9f : 0.5f),
                    Observation("player.velocity", "Vector2", movement.Count > 0 ? 0.9f : 0.5f),
                    Observation("player.grounded", "Boolean", movement.Count > 0 ? 0.75f : 0.35f),
                    Observation("episode.progress", "Float", completion.Count > 0 ? 0.75f : 0.25f)
                },
                mechanics = mechanics.ToArray(),
                affordances = affordances.ToArray(),
                tunables = tunables.ToArray(),
                issues = issues.ToArray()
            };
        }

        static List<ActionChannelDefinition> DiscoverActionChannels(GameObject player)
        {
            List<ActionChannelDefinition> result = new List<ActionChannelDefinition>();
            if (player == null)
                return result;

            MonoBehaviour[] behaviours = player.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                Type[] interfaces = behaviour.GetType().GetInterfaces();
                for (int t = 0; t < interfaces.Length; t++)
                {
                    Type contract = interfaces[t];
                    if (!contract.IsInterface || contract.Name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    PropertyInfo[] properties = contract.GetProperties();
                    AddChannelsForProperties(result, properties, contract.FullName);
                }

                Type behaviourType = behaviour.GetType();
                MethodInfo virtualInput = behaviourType.GetMethod("SetVirtualInput", Members);
                if (virtualInput != null && virtualInput.GetParameters().Length == 1)
                    AddChannelsForProperties(result, virtualInput.GetParameters()[0].ParameterType.GetProperties(), behaviourType.FullName);
            }
            return result;
        }

        static void AddChannelsForProperties(
            List<ActionChannelDefinition> result,
            PropertyInfo[] properties,
            string source)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                bool axis = property.PropertyType == typeof(float) || property.PropertyType == typeof(Vector2);
                bool button = property.PropertyType == typeof(bool);
                if (!axis && !button)
                    continue;

                string canonical = CanonicalChannelId(property.Name, axis, result);
                if (FindAction(result, canonical) != null)
                    continue;
                result.Add(new ActionChannelDefinition
                {
                    id = canonical,
                    suggestedName = property.Name,
                    valueType = axis ? property.PropertyType.Name : "Button",
                    supportsPressed = button,
                    supportsHeld = button,
                    supportsReleased = button,
                    confidence = 0.88f,
                    evidenceLevel = EvidenceLevel.SourceVerified,
                    evidence = new[]
                    {
                        new DiscoveryEvidence
                        {
                            id = "explicit-input-contract",
                            summary = $"Explicit input contract exposes {property.Name}.",
                            source = source,
                            level = EvidenceLevel.SourceVerified,
                            weight = 0.88f
                        }
                    }
                });
            }
        }

        static string CanonicalChannelId(string name, bool axis, List<ActionChannelDefinition> existing)
        {
            string lower = name.ToLowerInvariant();
            if (axis && (lower == "movex" || lower == "movey" || lower == "move"))
                return "axis.move";

            string stem = lower
                .Replace("pressededge", "")
                .Replace("releasededge", "")
                .Replace("pressed", "")
                .Replace("released", "")
                .Replace("held", "");
            for (int i = 0; i < existing.Count; i++)
            {
                string suggested = existing[i].suggestedName.ToLowerInvariant();
                if (suggested.StartsWith(stem, StringComparison.Ordinal) && existing[i].valueType == "Button")
                    return existing[i].id;
            }
            return axis ? "axis." + stem : "button." + CountButtons(existing);
        }

        static int CountButtons(List<ActionChannelDefinition> actions)
        {
            int count = 0;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].id.StartsWith("button.", StringComparison.Ordinal))
                    count++;
            return count;
        }

        static ActionChannelDefinition FindAction(List<ActionChannelDefinition> actions, string id)
        {
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].id == id)
                    return actions[i];
            return null;
        }

        static List<TunableDefinition> DiscoverTunables(GameObject player)
        {
            List<TunableDefinition> result = new List<TunableDefinition>();
            if (player == null)
                return result;
            MonoBehaviour[] behaviours = player.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null)
                    continue;
                FieldInfo[] fields = behaviours[i].GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                for (int f = 0; f < fields.Length; f++)
                {
                    if (fields[f].FieldType != typeof(float))
                        continue;
                    string name = fields[f].Name;
                    if (!ContainsAny(name, MovementTerms))
                        continue;
                    result.Add(new TunableDefinition
                    {
                        id = behaviours[i].GetType().FullName + "." + name,
                        displayName = ObjectNames.NicifyVariableName(name),
                        valueType = "Float",
                        currentNumericValue = (float)fields[f].GetValue(behaviours[i]),
                        confidence = 0.78f,
                        evidenceLevel = EvidenceLevel.SourceVerified
                    });
                }
            }
            return result;
        }

        static int CountMemberMatches(Type type, string[] terms)
        {
            int count = ContainsAny(type.Name, terms) ? 1 : 0;
            MemberInfo[] members = type.GetMembers(Members);
            for (int i = 0; i < members.Length; i++)
                if (ContainsAny(members[i].Name, terms))
                    count++;
            return count;
        }

        static bool SourceContainsInput(MonoBehaviour behaviour, out string source)
        {
            source = behaviour.GetType().Assembly.GetName().Name;
            MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
            if (script == null)
                return false;
            string path = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            string text = File.ReadAllText(path);
            source = path;
            return text.IndexOf("Keyboard.current", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Gamepad.current", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Input.GetAxis", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Input.GetButton", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("ReadValue<", StringComparison.Ordinal) >= 0;
        }

        static string[] AffordanceFlags(string typeName)
        {
            string lower = typeName.ToLowerInvariant();
            if (lower.Contains("spike") || lower.Contains("hazard") || lower.Contains("kill"))
                return new[] { "hazard" };
            if (lower.Contains("movingplatform"))
                return new[] { "platform", "dynamic" };
            if (lower.Contains("checkpoint"))
                return new[] { "checkpoint" };
            if (lower.Contains("goal") || lower.Contains("finish") || lower.Contains("exit"))
                return new[] { "completion" };
            if (lower.Contains("spring") || lower.Contains("bounce"))
                return new[] { "impulse" };
            if (lower.Contains("refill") || lower.Contains("restore"))
                return new[] { "resource-restoration" };
            return Array.Empty<string>();
        }

        static void Add(
            List<DiscoveryEvidence> evidence,
            ref float score,
            string id,
            string summary,
            string source,
            float weight)
        {
            score += weight;
            evidence.Add(new DiscoveryEvidence
            {
                id = id,
                summary = summary,
                source = source,
                level = EvidenceLevel.SourceVerified,
                weight = weight
            });
        }

        static bool ContainsAny(string value, string[] terms)
        {
            for (int i = 0; i < terms.Length; i++)
                if (value.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static string BuildFingerprint(Scene scene, List<MonoBehaviour> behaviours)
        {
            string value = scene.path + "|" + scene.name;
            if (!string.IsNullOrEmpty(scene.path))
                value += "|" + AssetDatabase.GetAssetDependencyHash(scene.path);
            for (int i = 0; i < behaviours.Count; i++)
            {
                MonoScript script = MonoScript.FromMonoBehaviour(behaviours[i]);
                string path = script == null ? null : AssetDatabase.GetAssetPath(script);
                if (!string.IsNullOrEmpty(path))
                    value += "|" + path + ":" + AssetDatabase.GetAssetDependencyHash(path);
            }
            return Hash128.Compute(value).ToString();
        }

        static void WriteCache(SceneDiscoveryResult result)
        {
            try
            {
                string directory = LocalDataPathService.EnsureDirectory(LocalDataPathService.CacheRoot);
                string path = LocalDataPathService.Guard(Path.Combine(directory, result.Fingerprint + ".manifest.json"));
                File.WriteAllText(path, JsonUtility.ToJson(result.Manifest, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Ryzi could not write the discovery cache: " + ex.Message);
            }
        }

        static ObservationChannelDefinition Observation(string id, string type, float confidence)
        {
            return new ObservationChannelDefinition
            {
                id = id,
                valueType = type,
                confidence = confidence,
                evidenceLevel = EvidenceLevel.SourceCandidate
            };
        }

        static DiscoveryIssue Issue(string id, string severity, string summary, string resolution)
        {
            return new DiscoveryIssue { id = id, severity = severity, summary = summary, resolution = resolution };
        }

        public static string HierarchyPath(GameObject value)
        {
            if (value == null)
                return "<null>";
            string path = value.name;
            Transform current = value.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
