using NUnit.Framework;
using System.Collections.Generic;
using Unity.EditorXR.Data;
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

        [Test]
        public void TypeFilterCollectsMatchingAssetsAcrossFolders()
        {
            var root = new FolderData("Assets", 1, 0, "Assets");
            var first = root.AddFolder("First", "Assets/First", 2);
            var second = root.AddFolder("Second", "Assets/Second", 3);
            first.AddAsset(new AssetData("First Material", "material-a", "Material"));
            second.AddAsset(new AssetData("Second Material", "material-b", "Material"));
            second.AddAsset(new AssetData("Prefab", "prefab", "Prefab"));

            var results = new List<AssetData>();
            root.CollectAssets("Material", results);

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.TrueForAll(asset => asset.type == "Material"));
        }
    }
}
