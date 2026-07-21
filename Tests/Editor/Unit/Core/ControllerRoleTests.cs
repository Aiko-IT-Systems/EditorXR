using NUnit.Framework;
using Unity.EditorXR.Core;
using Unity.EditorXR.Input;
using Unity.EditorXR.Tools;
using Unity.EditorXR.Utilities;
using Unity.EditorXR.Workspaces;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Unity.EditorXR.Tests
{
    class ControllerRoleTests
    {
        const string k_AuthoringHandLeft = "EditorXR.AuthoringHandLeft";

        class UnannotatedTool { }

        [ControllerToolRoles(ControllerRoleMask.Authoring, ControllerRoleMask.Utility)]
        class RoleRestrictedTool { }

        [ControllerToolRoles(ControllerRoleMask.Authoring)]
        class RoleRestrictedMultiDeviceTool : IMultiDeviceTool
        {
            public bool primary { private get; set; }
        }

        [Test]
        public void UnannotatedToolsSupportEitherRole()
        {
            ControllerRoleMask supported;
            ControllerRoleMask companion;
            ControllerRoleUtility.GetToolRoles(typeof(UnannotatedTool), out supported, out companion);

            Assert.AreEqual(ControllerRoleMask.Both, supported);
            Assert.AreEqual(ControllerRoleMask.None, companion);
        }

        [Test]
        public void RestrictedToolsExposeCompanionRoleSeparately()
        {
            ControllerRoleMask supported;
            ControllerRoleMask companion;
            ControllerRoleUtility.GetToolRoles(typeof(RoleRestrictedTool), out supported, out companion);

            Assert.AreEqual(ControllerRoleMask.Authoring, supported);
            Assert.AreEqual(ControllerRoleMask.Utility, companion);
        }

        [Test]
        public void MultiDeviceToolsStillHonorExplicitRoleRestrictions()
        {
            bool companion;
            Assert.IsTrue(EditorXRToolModule.ToolSupportsRole(typeof(RoleRestrictedMultiDeviceTool),
                ControllerRole.Authoring, out companion));
            Assert.IsFalse(companion);
            Assert.IsFalse(EditorXRToolModule.ToolSupportsRole(typeof(RoleRestrictedMultiDeviceTool),
                ControllerRole.Utility, out companion));
            Assert.IsFalse(companion);
        }

        [Test]
        public void LeftMenuBindingResolvesToXRMenuButton()
        {
            InputFeatureUsage<bool> usage;
            Assert.IsTrue(BaseVRInputToEvents.TryGetButtonUsage("XRI_Left_MenuButton", out usage));
            Assert.AreEqual(CommonUsages.menuButton.name, usage.name);
        }

        [Test]
        public void BuiltInRoleMetadataMatchesControllerResponsibilities()
        {
            bool companion;
            Assert.IsFalse(EditorXRToolModule.ToolSupportsRole(typeof(AnnotationTool), ControllerRole.Utility,
                out companion));
            Assert.IsTrue(EditorXRToolModule.ToolSupportsRole(typeof(LocomotionTool), ControllerRole.Authoring,
                out companion));
            Assert.IsTrue(companion);
        }

        [Test]
        public void EditorOnlyWorkspacesRemainAvailableForAuthoringPlayMode()
        {
            Assert.IsFalse(EditorXRUtils.ShouldHideEditorOnlyWorkspace(typeof(ProjectWorkspace)));
            Assert.IsFalse(EditorXRUtils.ShouldHideEditorOnlyWorkspace(typeof(HierarchyWorkspace)));
        }

        [Test]
        public void ProjectWorkspaceReceivesEditorDefaultReferences()
        {
            var go = new GameObject("Project Workspace Reference Test");
            go.SetActive(false);
            try
            {
                var workspace = go.AddComponent<ProjectWorkspace>();
                Assert.Greater(EditorXRUtils.ApplyEditorDefaultReferences(workspace), 0);

                var contentField = typeof(ProjectWorkspace).GetField("m_ContentPrefab",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(contentField);
                Assert.IsNotNull(contentField.GetValue(workspace));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AuthoringHandPreferencePersists()
        {
            var hadValue = EditorPrefs.HasKey(k_AuthoringHandLeft);
            var previous = EditorPrefs.GetBool(k_AuthoringHandLeft, false);
            try
            {
                global::Unity.EditorXR.Core.EditorXR.authoringHandLeft = true;
                Assert.IsTrue(EditorPrefs.GetBool(k_AuthoringHandLeft));

                global::Unity.EditorXR.Core.EditorXR.authoringHandLeft = false;
                Assert.IsFalse(EditorPrefs.GetBool(k_AuthoringHandLeft));
            }
            finally
            {
                if (hadValue)
                    EditorPrefs.SetBool(k_AuthoringHandLeft, previous);
                else
                    EditorPrefs.DeleteKey(k_AuthoringHandLeft);
            }
        }
    }
}
