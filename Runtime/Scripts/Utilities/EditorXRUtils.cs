using System;
using System.Collections.Generic;
using System.IO;
using Unity.XRTools.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace Unity.EditorXR.Utilities
{
    static class EditorXRUtils
    {
        const string k_PackageAssetPrefix = "Packages/com.unity.editorxr/";
        const string k_WritablePackageAssetRoot = "Assets/EditorXR Generated/Package Assets/";

        static HideFlags s_HideFlags = HideFlags.DontSaveInEditor;

#if UNITY_EDITOR
        static readonly HashSet<string> s_SynchronizedPrefabPaths = new HashSet<string>();
#endif

        public static HideFlags hideFlags
        {
            get { return s_HideFlags; }
            set { s_HideFlags = value; }
        }

        /// <summary>
        /// Create an empty VR GameObject.
        /// </summary>
        /// <param name="name">Name of the new GameObject</param>
        /// <param name="parent">Transform to parent new object under</param>
        /// <returns>The newly created empty GameObject</returns>
        public static GameObject CreateEmptyGameObject(string name = null, Transform parent = null)
        {
            GameObject empty = null;
            if (string.IsNullOrEmpty(name))
                name = "New Game Object";

#if UNITY_EDITOR
            empty = EditorUtility.CreateGameObjectWithHideFlags(name, hideFlags);
#else
            empty = new GameObject(name);
            empty.hideFlags = hideFlags;
#endif
            empty.transform.parent = parent;
            empty.transform.localPosition = Vector3.zero;

            return empty;
        }

        public static GameObject Instantiate(GameObject prefab, Transform parent = null, bool worldPositionStays = true,
            bool runInEditMode = true, bool active = true)
        {
#if UNITY_EDITOR
            prefab = GetWritablePackagePrefab(prefab);
#endif
            var go = UnityObject.Instantiate(prefab, parent, worldPositionStays);
            if (worldPositionStays)
            {
                var goTransform = go.transform;
                var prefabTransform = prefab.transform;
                goTransform.position = prefabTransform.position;
                goTransform.rotation = prefabTransform.rotation;
            }

            go.SetActive(active);
            if (!Application.isPlaying && runInEditMode)
                go.SetRunInEditModeRecursively(true);

            go.SetHideFlagsRecursively(hideFlags);

#if UNITY_EDITOR
            if (Application.isPlaying)
                IsolatePackageMaterials(go);
#endif

            return go;
        }

#if UNITY_EDITOR
        static GameObject GetWritablePackagePrefab(GameObject prefab)
        {
            if (!prefab)
                return prefab;

            var sourcePath = AssetDatabase.GetAssetPath(prefab);
            if (!sourcePath.StartsWith(k_PackageAssetPrefix, StringComparison.OrdinalIgnoreCase))
                return prefab;

            var destinationPath = k_WritablePackageAssetRoot + sourcePath.Substring(k_PackageAssetPrefix.Length);
            if (!s_SynchronizedPrefabPaths.Add(destinationPath))
                return AssetDatabase.LoadAssetAtPath<GameObject>(destinationPath) ?? prefab;

            EnsureAssetFolder(Path.GetDirectoryName(destinationPath));

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(sourcePath);
            if (packageInfo == null)
                return prefab;

            var relativeSourcePath = sourcePath.Substring(("Packages/" + packageInfo.name + "/").Length);
            var physicalSourcePath = Path.Combine(packageInfo.resolvedPath, relativeSourcePath);
            var physicalDestinationPath = Path.GetFullPath(destinationPath);

            if (!File.Exists(physicalDestinationPath))
            {
                File.Copy(physicalSourcePath, physicalDestinationPath);
                AssetDatabase.ImportAsset(destinationPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            else if (!FilesMatch(physicalSourcePath, physicalDestinationPath))
            {
                File.Copy(physicalSourcePath, physicalDestinationPath, true);
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(destinationPath) ?? prefab;
        }

        static void EnsureAssetFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            var parentPath = Path.GetDirectoryName(folderPath).Replace('\\', '/');
            EnsureAssetFolder(parentPath);
            AssetDatabase.CreateFolder(parentPath, Path.GetFileName(folderPath));
        }

        static bool FilesMatch(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (!first.Exists || !second.Exists || first.Length != second.Length)
                return false;

            var firstBytes = File.ReadAllBytes(firstPath);
            var secondBytes = File.ReadAllBytes(secondPath);
            for (var i = 0; i < firstBytes.Length; ++i)
            {
                if (firstBytes[i] != secondBytes[i])
                    return false;
            }

            return true;
        }

        static void IsolatePackageMaterials(GameObject root)
        {
            var clones = new System.Collections.Generic.Dictionary<Material, Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; ++i)
                {
                    var clone = GetPackageMaterialClone(materials[i], clones);
                    if (clone == materials[i])
                        continue;
                    materials[i] = clone;
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = materials;
            }

            foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
            {
                var material = graphic.material;
                var clone = GetPackageMaterialClone(material, clones);
                if (clone != material)
                    graphic.material = clone;
            }
        }

        static Material GetPackageMaterialClone(Material material,
            System.Collections.Generic.IDictionary<Material, Material> clones)
        {
            if (!material)
                return material;

            var path = AssetDatabase.GetAssetPath(material);
            if (!path.StartsWith("Packages/com.unity.editorxr/", StringComparison.OrdinalIgnoreCase))
                return material;

            Material clone;
            if (clones.TryGetValue(material, out clone))
                return clone;

            clone = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
            clones.Add(material, clone);
            return clone;
        }
#endif

        public static T CreateGameObjectWithComponent<T>(Transform parent = null, bool worldPositionStays = true,
            bool runInEditMode = true)
            where T : Component
        {
            return (T)CreateGameObjectWithComponent(typeof(T), parent, worldPositionStays, runInEditMode);
        }

        public static Component CreateGameObjectWithComponent(Type type, Transform parent = null,
            bool worldPositionStays = true, bool runInEditMode = true)
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
                return RuntimeCreateGameObjectWithComponent(type, parent, worldPositionStays);

            var go = EditorUtility.CreateGameObjectWithHideFlags(type.Name, hideFlags, type);
            var component = go.GetComponent(type);
            if (component)
            {
                component.gameObject.SetRunInEditModeRecursively(runInEditMode);
                component.transform.SetParent(parent, worldPositionStays);
            }
            else
            {
                UnityObject.DestroyImmediate(go);
            }

            return component;
#else
            return RuntimeCreateGameObjectWithComponent(type, parent, worldPositionStays);
#endif
        }

        static Component RuntimeCreateGameObjectWithComponent(Type type, Transform parent = null,
            bool worldPositionStays = true)
        {
            var go = new GameObject(type.Name);
            go.hideFlags = hideFlags;
            go.transform.SetParent(parent, worldPositionStays);
            return AddComponent(type, go);
        }

        public static T AddComponent<T>(GameObject go) where T : Component
        {
            return (T)AddComponent(typeof(T), go);
        }

        public static Component AddComponent(Type type, GameObject go)
        {
            Component component = null;
            if (Application.isPlaying)
            {
                var mb = DefaultScriptReferences.Create(type);
                if (mb)
                {
                    mb.transform.parent = go.transform;
                    mb.enabled = true;
                    component = mb;
                }
            }

            if (!component)
            {
#if UNITY_EDITOR
                var wasActive = go.activeSelf;
                if (wasActive)
                    go.SetActive(false);
#endif

                component = go.AddComponent(type);

#if UNITY_EDITOR
                ApplyEditorDefaultReferences(component);
                if (wasActive)
                    go.SetActive(true);
#endif
            }

            go.SetRunInEditModeRecursively(true);
            return component;
        }

        internal static bool ShouldHideEditorOnlyWorkspace(Type type)
        {
#if UNITY_EDITOR
            // EditorXR authoring intentionally runs inside Play Mode for XR and ClientSim input.
            return false;
#else
            return Attribute.IsDefined(type, typeof(EditorOnlyWorkspaceAttribute), true);
#endif
        }

#if UNITY_EDITOR
        internal static int ApplyEditorDefaultReferences(Component component)
        {
            var behaviour = component as MonoBehaviour;
            if (!behaviour)
                return 0;

            var script = MonoScript.FromMonoBehaviour(behaviour);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(script)) as MonoImporter;
            if (!importer)
                return 0;

            var assigned = 0;
            for (var currentType = component.GetType(); currentType != null; currentType = currentType.BaseType)
            {
                var fields = currentType.GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (!typeof(UnityObject).IsAssignableFrom(field.FieldType) || field.GetValue(component) != null)
                        continue;

                    var reference = importer.GetDefaultReference(field.Name);
                    if (!reference || !field.FieldType.IsInstanceOfType(reference))
                        continue;

                    field.SetValue(component, reference);
                    assigned++;
                }
            }

            return assigned;
        }
#endif

        public static T CopyComponent<T>(T sourceComponent, GameObject targetGameObject) where T : Component
        {
            var sourceType = sourceComponent.GetType();
            var clonedTargetComponent = AddComponent(sourceType, targetGameObject);
            if (Application.isPlaying)
            {
                Type type = sourceComponent.GetType();
                var fields = type.GetFields();
                foreach (var field in fields)
                {
                    if (field.IsStatic)
                        continue;

                    field.SetValue(clonedTargetComponent, field.GetValue(sourceComponent));
                }

                var props = type.GetProperties();
                foreach (var prop in props)
                {
                    if (!prop.CanWrite || !prop.CanWrite || prop.Name == "name")
                        continue;

                    prop.SetValue(clonedTargetComponent, prop.GetValue(sourceComponent, null), null);
                }
            }
#if UNITY_EDITOR
            else
            {
                EditorUtility.CopySerialized(sourceComponent, clonedTargetComponent);
            }
#endif
            return (T)clonedTargetComponent;
        }
    }
}
