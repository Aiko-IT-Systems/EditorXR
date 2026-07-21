using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Unity.EditorXR.Tools.Tests
{
    class ScaleTransformUtilityTests
    {
        readonly List<GameObject> m_Objects = new List<GameObject>();

        GameObject Create(string name, Transform parent = null)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent);
            m_Objects.Add(gameObject);
            return gameObject;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in m_Objects)
                if (gameObject)
                    Object.DestroyImmediate(gameObject);
            m_Objects.Clear();
        }

        [TestCase(1f, false, 1.5f)]
        [TestCase(1f, true, 2f)]
        [TestCase(-10f, false, 0.0005f)]
        public void FaceScalingUsesTargetDimension(float distance, bool symmetric, float expected)
        {
            Assert.That(ScaleTransformUtility.FaceScaleFactor(2f, distance, symmetric), Is.EqualTo(expected).Within(0.00001f));
        }

        [Test]
        public void AllFaceDirectionsScaleOnlyTheirAxis()
        {
            var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var direction in directions)
            {
                var factors = ScaleTransformUtility.AxisFactors(direction, 2f);
                Assert.AreEqual(2f, Vector3.Dot(Vector3.Scale(factors, Abs(direction)), Abs(direction)));
                Assert.AreEqual(4f, factors.x + factors.y + factors.z);
            }
        }

        [Test]
        public void EightCornersAnchorTheirOppositeCorner()
        {
            var bounds = new OrientedSelectionBounds { center = Vector3.zero, rotation = Quaternion.identity, extents = Vector3.one };
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var direction = new Vector3(x, y, z);
                Assert.AreEqual(-direction, ScaleTransformUtility.OppositeAnchor(bounds, direction));
            }
        }

        [Test]
        public void NestedSelectionsOnlyScaleTheirSelectedRoot()
        {
            var parent = Create("Parent").transform;
            var child = Create("Child", parent).transform;
            CollectionAssert.AreEqual(new[] { parent }, ScaleTransformUtility.GetSelectionRoots(new[] { child, parent }));
        }

        [Test]
        public void GroupPositionsAndNegativeScaleArePreservedFromBaseline()
        {
            var left = Create("Left").transform;
            var right = Create("Right").transform;
            left.position = Vector3.left;
            right.position = Vector3.right;
            left.localScale = new Vector3(-2f, 1f, 1f);

            var states = ScaleTransformUtility.CaptureStates(new[] { left, right });
            ScaleTransformUtility.ApplyScale(states, Vector3.zero, Quaternion.identity, new Vector3(2f, 1f, 1f));

            Assert.AreEqual(-2f, left.position.x, 0.0001f);
            Assert.AreEqual(2f, right.position.x, 0.0001f);
            Assert.AreEqual(-4f, left.localScale.x, 0.0001f);
        }

        static Vector3 Abs(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }
    }
}
