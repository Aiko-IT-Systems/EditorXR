using NUnit.Framework;
using UnityEditor;

namespace Unity.EditorXR.Tests
{
    class ControllerRoleTests
    {
        const string k_AuthoringHandLeft = "EditorXR.AuthoringHandLeft";

        class UnannotatedTool { }

        [ControllerToolRoles(ControllerRoleMask.Authoring, ControllerRoleMask.Utility)]
        class RoleRestrictedTool { }

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
