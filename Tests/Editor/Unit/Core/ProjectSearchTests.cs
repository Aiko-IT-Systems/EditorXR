using NUnit.Framework;
using Unity.EditorXR.Modules;
using UnityEditor;

namespace Unity.EditorXR.Tests
{
    class ProjectSearchTests
    {
        [Test]
        public void SearchGlobalObjectIdResolvesToAssetPath()
        {
            const string assetPath = "Packages/com.unity.editorxr/Runtime/Menus/MainMenu/Materials/SolidMenuSurface.mat";
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Assert.IsNotNull(asset);

            var searchId = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString();
            string resolvedPath;
            Assert.IsTrue(ProjectSearchUtility.TryResolveAssetPath(searchId, out resolvedPath));
            Assert.AreEqual(assetPath, resolvedPath);
        }

        [TestCase("Assets/Test.prefab", "Prefab")]
        [TestCase("Assets/Test.fbx", "Model")]
        [TestCase("Assets/Test.asset", "Asset")]
        public void CommonAssetTypesAvoidLoadingAssets(string path, string expectedType)
        {
            Assert.AreEqual(expectedType, ProjectSearchUtility.GetAssetTypeName(path));
        }
    }
}
