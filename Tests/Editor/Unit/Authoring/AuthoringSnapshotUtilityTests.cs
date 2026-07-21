#if UNITY_2022_1_OR_NEWER
using System.Collections.Generic;
using NUnit.Framework;
using Unity.EditorXR.Authoring;
using UnityEditor;
using UnityEngine;

namespace Unity.EditorXR.Tests.Authoring
{
    [TestFixture]
    class AuthoringSnapshotUtilityTests
    {
        readonly List<GameObject> m_Objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in m_Objects)
            {
                if (gameObject)
                    Object.DestroyImmediate(gameObject);
            }
            m_Objects.Clear();
        }

        [Test]
        public void PropertySnapshot_RestoresSupportedTransformValue()
        {
            var gameObject = CreateGameObject("Snapshot Target");
            gameObject.transform.localPosition = new Vector3(1f, 2f, 3f);
            var diagnostics = new List<AuthoringDiagnostic>();

            var snapshots = AuthoringSnapshotUtility.CaptureProperties(gameObject.transform,
                new[] { "m_LocalPosition" }, new Dictionary<int, string>(), diagnostics);
            gameObject.transform.localPosition = Vector3.zero;

            Assert.IsTrue(AuthoringSnapshotUtility.ApplyProperties(gameObject.transform, snapshots,
                new Dictionary<string, Object>(), diagnostics));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), gameObject.transform.localPosition);
            Assert.IsFalse(diagnostics.Exists(diagnostic => diagnostic.blocking));
        }

        [Test]
        public void CaptureReference_UsesCreatedLocalIdentifier()
        {
            var gameObject = CreateGameObject("Created Target");
            var diagnostics = new List<AuthoringDiagnostic>();
            var ids = new Dictionary<int, string> { { gameObject.GetInstanceID(), "local:test" } };

            var reference = AuthoringSnapshotUtility.CaptureReference(gameObject, ids, diagnostics, "test");

            Assert.AreEqual((int)AuthoringReferenceKind.CreatedObject, reference.kind);
            Assert.AreEqual("local:test", reference.value);
            Assert.IsEmpty(diagnostics);
        }

        [Test]
        public void IsEligible_RejectsUnsavedSceneObjects()
        {
            var gameObject = CreateGameObject("Transient");
            Assert.IsFalse(AuthoringSnapshotUtility.IsEligible(gameObject, null, out var reason));
            StringAssert.Contains("saved, loaded scene", reason);
        }

        [Test]
        public void RecoveryData_RoundTripsChangeSet()
        {
            var data = new AuthoringRecoveryData { sessionId = "session", state = "PendingApply" };
            data.changeSet.deletions.Add("GlobalObjectId_V1-2-test-1-0");
            data.changeSet.diagnostics.Add(new AuthoringDiagnostic(true, "blocked"));

            var restored = AuthoringRecoverySerializer.FromJson(AuthoringRecoverySerializer.ToJson(data));

            Assert.AreEqual("session", restored.sessionId);
            Assert.AreEqual(1, restored.changeSet.deletions.Count);
            Assert.IsTrue(restored.changeSet.diagnostics[0].blocking);
        }

        [Test]
        public void RecoveryData_RoundTripsDeepCreatedHierarchy()
        {
            var data = new AuthoringRecoveryData();
            var snapshot = new AuthoringHierarchySnapshot
            {
                root = new AuthoringCreatedNode
                {
                    name = "Level 0",
                    localPosition = new Vector3(1f, 2f, 3f),
                    localRotation = Quaternion.Euler(10f, 20f, 30f),
                    localScale = new Vector3(2f, 3f, 4f)
                }
            };
            data.changeSet.creations.Add(snapshot);
            var current = snapshot.root;
            for (var depth = 1; depth <= 20; ++depth)
            {
                var child = new AuthoringCreatedNode { name = "Level " + depth };
                current.children.Add(child);
                current = child;
            }

            var restored = AuthoringRecoverySerializer.FromJson(AuthoringRecoverySerializer.ToJson(data));
            current = restored.changeSet.creations[0].root;
            for (var depth = 1; depth <= 20; ++depth)
                current = current.children[0];

            Assert.AreEqual("Level 20", current.name);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), restored.changeSet.creations[0].root.localPosition);
            Assert.AreEqual(new Vector3(2f, 3f, 4f), restored.changeSet.creations[0].root.localScale);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(10f, 20f, 30f),
                restored.changeSet.creations[0].root.localRotation), 0.001f);
        }

        GameObject CreateGameObject(string name)
        {
            var gameObject = new GameObject(name);
            m_Objects.Add(gameObject);
            return gameObject;
        }
    }
}
#endif
