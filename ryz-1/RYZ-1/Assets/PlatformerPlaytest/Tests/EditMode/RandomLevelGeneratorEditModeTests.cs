using System.Collections.Generic;
using CelesteBenchmark;
using NUnit.Framework;
using UnityEngine;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class RandomLevelGeneratorEditModeTests
    {
        GameObject host;
        RandomLevelGenerator generator;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Generator Host");
            generator = host.AddComponent<RandomLevelGenerator>();
            generator.GenerateOnStart = false;
            generator.PlatformCount = 14;
            generator.Origin = new Vector2(10f, -2f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
        }

        [Test]
        public void SameSeed_RebuildsIdenticalJumpableLayout()
        {
            generator.GenerateFromSeed(8675309);
            List<Vector3> firstPositions = PlatformPositions();
            List<Vector3> firstScales = PlatformScales();

            generator.GenerateFromSeed(8675309);
            List<Vector3> secondPositions = PlatformPositions();
            List<Vector3> secondScales = PlatformScales();

            Assert.AreEqual(14, generator.GeneratedPlatformCount);
            CollectionAssert.AreEqual(firstPositions, secondPositions);
            CollectionAssert.AreEqual(firstScales, secondScales);

            for (int i = 1; i < secondPositions.Count; i++)
            {
                float previousRight = secondPositions[i - 1].x + secondScales[i - 1].x * 0.5f;
                float nextLeft = secondPositions[i].x - secondScales[i].x * 0.5f;
                Assert.LessOrEqual(nextLeft - previousRight, 2.75f);
                Assert.LessOrEqual(Mathf.Abs(secondPositions[i].y - secondPositions[i - 1].y), 1.5f);
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentLayouts()
        {
            generator.GenerateFromSeed(1);
            List<Vector3> first = PlatformPositions();

            generator.GenerateFromSeed(2);
            List<Vector3> second = PlatformPositions();

            Assert.IsFalse(LayoutsEqual(first, second));
        }

        [Test]
        public void GeneratedFinish_PublishesRealGoalMetadata()
        {
            generator.GenerateFromSeed(123);

            Assert.IsNotNull(generator.GeneratedGoal);
            Assert.IsNotNull(generator.GeneratedGoal.Trigger);
            Assert.IsTrue(generator.GeneratedGoal.Trigger.isTrigger);
            Assert.Greater(generator.GeneratedGoal.WorldRect.width, 0f);
            Assert.Greater(generator.GeneratedGoal.WorldRect.height, 0f);
            Assert.IsTrue(generator.GeneratedGoal.WorldRect.Contains(
                generator.GeneratedGoal.WorldRect.center));
        }

        [Test]
        public void Clear_RemovesOnlyGeneratedRoot()
        {
            GameObject authored = new GameObject("Authored Geometry");
            authored.transform.SetParent(host.transform);
            generator.GenerateFromSeed(42);

            generator.ClearGeneratedLevel();

            Assert.IsNull(host.transform.Find(RandomLevelGenerator.GeneratedRootName));
            Assert.IsNotNull(host.transform.Find("Authored Geometry"));
            Assert.AreEqual(0, generator.GeneratedPlatformCount);
            Assert.IsNull(generator.GeneratedGoal);
        }

        List<Vector3> PlatformPositions()
        {
            List<Vector3> values = new List<Vector3>();
            Transform root = generator.GeneratedRoot;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith("Platform "))
                    values.Add(child.localPosition);
            }
            return values;
        }

        List<Vector3> PlatformScales()
        {
            List<Vector3> values = new List<Vector3>();
            Transform root = generator.GeneratedRoot;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith("Platform "))
                    values.Add(child.localScale);
            }
            return values;
        }

        static bool LayoutsEqual(List<Vector3> first, List<Vector3> second)
        {
            if (first.Count != second.Count)
                return false;
            for (int i = 0; i < first.Count; i++)
            {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }
    }
}
