#if UNITY_2022_1_OR_NEWER
using NUnit.Framework;
using Unity.EditorXR.Data;
using Unity.EditorXR.Utilities;
using UnityEngine;

namespace Unity.EditorXR.Tests.Utilities
{
    class AssetDropUtilsTests
    {
        GameObject m_Root;
        Material m_Material;
        PhysicMaterial m_PhysicMaterial;

        [TearDown]
        public void TearDown()
        {
            if (m_Root)
                Object.DestroyImmediate(m_Root);
            if (m_Material)
                Object.DestroyImmediate(m_Material);
            if (m_PhysicMaterial)
                Object.DestroyImmediate(m_PhysicMaterial);
        }

        [Test]
        public void AssignMaterial_AppliesToRootAndChildRenderers()
        {
            m_Root = new GameObject("Root", typeof(MeshRenderer));
            var child = new GameObject("Child", typeof(MeshRenderer));
            child.transform.SetParent(m_Root.transform);
            child.SetActive(false);
            m_Material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var data = new AssetData("Test Material", "test", "Material") { asset = m_Material };

            AssetDropUtils.AssignMaterial(m_Root, data);

            Assert.AreSame(m_Material, m_Root.GetComponent<MeshRenderer>().sharedMaterial);
            Assert.AreSame(m_Material, child.GetComponent<MeshRenderer>().sharedMaterial);
        }

        [Test]
        public void AssignPhysicMaterial_AppliesToRootAndChildColliders()
        {
            m_Root = new GameObject("Root", typeof(BoxCollider));
            var child = new GameObject("Child", typeof(BoxCollider));
            child.transform.SetParent(m_Root.transform);
            child.SetActive(false);
            m_PhysicMaterial = new PhysicMaterial("Test Physics Material");
            var data = new AssetData("Test Physics Material", "test", "PhysicMaterial")
            {
                asset = m_PhysicMaterial
            };

            AssetDropUtils.AssignPhysicMaterial(m_Root, data);

            Assert.AreSame(m_PhysicMaterial, m_Root.GetComponent<BoxCollider>().sharedMaterial);
            Assert.AreSame(m_PhysicMaterial, child.GetComponent<BoxCollider>().sharedMaterial);
        }
    }
}
#endif
