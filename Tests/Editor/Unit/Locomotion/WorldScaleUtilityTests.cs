using NUnit.Framework;
using UnityEngine;

namespace Unity.EditorXR.Tools.Tests
{
    class WorldScaleUtilityTests
    {
        [Test]
        public void RejectsUnsafeHandSeparation()
        {
            float scale;
            Assert.IsFalse(WorldScaleUtility.TryGetScale(1f, 0.049f, 0.1f, 0.1f, 1000f, out scale));
        }

        [TestCase(1f, 1f, 2f, 0.5f)]
        [TestCase(1f, 1f, 0.5f, 2f)]
        [TestCase(1f, 1f, 100f, 0.1f)]
        [TestCase(1f, 1f, 0.0001f, 1000f)]
        public void CalculatesGuardedClampedRatios(float startScale, float startDistance, float distance,
            float expected)
        {
            float scale;
            Assert.IsTrue(WorldScaleUtility.TryGetScale(startScale, startDistance, distance, 0.1f, 1000f, out scale));
            Assert.AreEqual(expected, scale, 0.0001f);
        }

        [Test]
        public void OnePercentScaleChangesStayInDeadZone()
        {
            float scale;
            Assert.IsTrue(WorldScaleUtility.TryGetScale(2f, 1f, 1.005f, 0.1f, 1000f, out scale));
            Assert.AreEqual(2f, scale);
        }

        [Test]
        public void MidpointAnchorRemainsFixed()
        {
            var rotation = Quaternion.Euler(0f, 30f, 0f);
            var midpoint = new Vector3(1f, 0f, 2f);
            var startPosition = new Vector3(4f, 0f, 5f);
            var anchor = rotation * midpoint * 2f;
            Assert.AreEqual(startPosition, WorldScaleUtility.GetAnchoredPosition(startPosition, anchor, midpoint,
                rotation, 2f));
        }

        [Test]
        public void FilteringIsStableAcrossFrameRates()
        {
            var at30 = FilterForDuration(30, 0.3f);
            var at90 = FilterForDuration(90, 0.3f);
            Assert.AreEqual(at30, at90, 0.0001f);
        }

        [Test]
        public void YawUsesSignedAngleAndDeadZone()
        {
            Assert.AreEqual(0f, WorldScaleUtility.GetYaw(Vector3.forward,
                Quaternion.Euler(0f, 1f, 0f) * Vector3.forward));
            Assert.AreEqual(20f, WorldScaleUtility.GetYaw(Vector3.forward,
                Quaternion.Euler(0f, 20f, 0f) * Vector3.forward), 0.001f);
            Assert.AreEqual(-20f, WorldScaleUtility.GetYaw(Vector3.forward,
                Quaternion.Euler(0f, -20f, 0f) * Vector3.forward), 0.001f);
        }

        static float FilterForDuration(int frameRate, float duration)
        {
            var value = 0f;
            var deltaTime = 1f / frameRate;
            var frames = Mathf.RoundToInt(duration * frameRate);
            for (var i = 0; i < frames; ++i)
                value = Mathf.Lerp(value, 1f, WorldScaleUtility.FilterFactor(deltaTime));
            return value;
        }
    }
}
