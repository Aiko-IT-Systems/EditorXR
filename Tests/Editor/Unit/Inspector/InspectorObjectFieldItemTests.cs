#if UNITY_2022_1_OR_NEWER
using NUnit.Framework;
using Unity.EditorXR.Workspaces;
using UnityEngine;

namespace Unity.EditorXR.Tests.Inspector
{
    class InspectorObjectFieldItemTests
    {
        GameObject m_GameObject;

        [TearDown]
        public void TearDown()
        {
            if (m_GameObject)
                Object.DestroyImmediate(m_GameObject);
        }

        [Test]
        public void IsAssignable_AcceptsDerivedUnityObjectType()
        {
            m_GameObject = new GameObject("Camera");
            var camera = m_GameObject.AddComponent<Camera>();

            Assert.IsTrue(InspectorObjectFieldItem.IsAssignable(typeof(Behaviour), camera));
        }

        [Test]
        public void IsAssignable_RejectsUnrelatedUnityObjectType()
        {
            m_GameObject = new GameObject("Camera");
            var camera = m_GameObject.AddComponent<Camera>();

            Assert.IsFalse(InspectorObjectFieldItem.IsAssignable(typeof(Material), camera));
        }
    }
}
#endif
