#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.EditorXR.Authoring
{
    internal static class AuthoringSnapshotUtility
    {
        const string k_ClientSimRoot = "__ClientSim";

        static readonly string[] k_UnsafePropertyRoots =
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject"
        };

        internal static bool IsEligible(UnityEngine.Object target, ICollection<string> scenePaths, out string reason)
        {
            reason = null;
            if (!target)
            {
                reason = "The target no longer exists.";
                return false;
            }

            var gameObject = target as GameObject;
            var component = target as Component;
            if (!gameObject && component)
                gameObject = component.gameObject;

            if (!gameObject)
            {
                reason = "Only scene GameObjects and Components can be persisted.";
                return false;
            }

            if (!gameObject.scene.IsValid() || !gameObject.scene.isLoaded || string.IsNullOrEmpty(gameObject.scene.path))
            {
                reason = "The target is not in a saved, loaded scene.";
                return false;
            }

            if (scenePaths != null && !scenePaths.Contains(gameObject.scene.path))
            {
                reason = "The target belongs to a scene that was not part of the authoring session.";
                return false;
            }

            if ((gameObject.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0)
            {
                reason = "The target is transient (DontSave).";
                return false;
            }

            for (var current = gameObject.transform; current; current = current.parent)
            {
                if (current.name.StartsWith(k_ClientSimRoot, StringComparison.OrdinalIgnoreCase)
                    || current.name.IndexOf("ClientSimSystem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = "ClientSim-owned objects are never persisted.";
                    return false;
                }
            }

            var type = target.GetType();
            var fullName = type.FullName ?? string.Empty;
            var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            if (fullName.IndexOf("ClientSim", StringComparison.OrdinalIgnoreCase) >= 0
                || assemblyName.IndexOf("ClientSim", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "ClientSim-owned components are never persisted.";
                return false;
            }

            return true;
        }

        internal static bool TryGetGlobalId(UnityEngine.Object target, out string id)
        {
            id = null;
            if (!target)
                return false;

            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if ((int)globalId.identifierType == 0)
                return false;

            id = globalId.ToString();
            return true;
        }

        internal static UnityEngine.Object ResolveGlobalId(string id)
        {
            GlobalObjectId globalId;
            return !string.IsNullOrEmpty(id) && GlobalObjectId.TryParse(id, out globalId)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId)
                : null;
        }

        internal static AuthoringObjectReference CaptureReference(UnityEngine.Object target,
            IDictionary<int, string> createdObjectIds, IList<AuthoringDiagnostic> diagnostics, string context)
        {
            if (!target)
                return AuthoringObjectReference.Null();

            string localId;
            if (createdObjectIds != null && createdObjectIds.TryGetValue(target.GetInstanceID(), out localId))
            {
                return new AuthoringObjectReference
                {
                    kind = (int)AuthoringReferenceKind.CreatedObject,
                    value = localId
                };
            }

            string globalId;
            if (TryGetGlobalId(target, out globalId))
            {
                return new AuthoringObjectReference
                {
                    kind = (int)AuthoringReferenceKind.GlobalObjectId,
                    value = globalId
                };
            }

            diagnostics.Add(new AuthoringDiagnostic(true,
                string.Format("{0} references transient object '{1}'.", context, target.name)));
            return AuthoringObjectReference.Null();
        }

        internal static UnityEngine.Object ResolveReference(AuthoringObjectReference reference,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            if (reference == null || reference.kind == (int)AuthoringReferenceKind.Null)
                return null;

            if (reference.kind == (int)AuthoringReferenceKind.GlobalObjectId)
                return ResolveGlobalId(reference.value);

            UnityEngine.Object result;
            return createdObjects != null && createdObjects.TryGetValue(reference.value, out result) ? result : null;
        }

        internal static List<AuthoringPropertySnapshot> CaptureProperties(UnityEngine.Object target,
            ICollection<string> requestedPaths, IDictionary<int, string> createdObjectIds,
            IList<AuthoringDiagnostic> diagnostics)
        {
            var result = new List<AuthoringPropertySnapshot>();
            if (!target)
                return result;

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(target);
                serializedObject.UpdateIfRequiredOrScript();
            }
            catch (Exception exception)
            {
                diagnostics.Add(new AuthoringDiagnostic(true,
                    string.Format("Cannot inspect '{0}': {1}", target.name, exception.Message)));
                return result;
            }

            if (requestedPaths != null)
            {
                foreach (var path in requestedPaths)
                {
                    if (path == "m_Script")
                        continue;

                    var property = serializedObject.FindProperty(path);
                    if (property == null)
                    {
                        diagnostics.Add(new AuthoringDiagnostic(true,
                            string.Format("Serialized property '{0}' disappeared from '{1}'.", path, target.name)));
                        continue;
                    }

                    if (!IsSafeProperty(property))
                        continue;

                    CaptureProperty(property, target, createdObjectIds, diagnostics, result);
                }

                return result;
            }

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = iterator.propertyType == SerializedPropertyType.Generic;
                if (iterator.propertyPath == "m_Script")
                {
                    enterChildren = false;
                    continue;
                }

                if (iterator.propertyType == SerializedPropertyType.Generic)
                    continue;

                if (!IsSafeProperty(iterator))
                {
                    enterChildren = false;
                    continue;
                }

                CaptureProperty(iterator, target, createdObjectIds, diagnostics, result);
                enterChildren = false;
            }

            return result;
        }

        static void CaptureProperty(SerializedProperty property, UnityEngine.Object owner,
            IDictionary<int, string> createdObjectIds, IList<AuthoringDiagnostic> diagnostics,
            ICollection<AuthoringPropertySnapshot> output)
        {
            var snapshot = new AuthoringPropertySnapshot
            {
                propertyPath = property.propertyPath,
                propertyType = (int)property.propertyType
            };

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    snapshot.longValue = property.longValue;
                    break;
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.ArraySize:
                    snapshot.longValue = property.intValue;
                    break;
                case SerializedPropertyType.Boolean:
                    snapshot.boolValue = property.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    snapshot.doubleValue = property.doubleValue;
                    break;
                case SerializedPropertyType.String:
                    snapshot.stringValue = property.stringValue;
                    break;
                case SerializedPropertyType.Color:
                    snapshot.colorValue = property.colorValue;
                    break;
                case SerializedPropertyType.ObjectReference:
                    snapshot.objectReference = CaptureReference(property.objectReferenceValue, createdObjectIds,
                        diagnostics, owner.name + "." + property.propertyPath);
                    break;
                case SerializedPropertyType.Enum:
                    snapshot.longValue = property.enumValueIndex;
                    break;
                case SerializedPropertyType.Vector2:
                    snapshot.vector2Value = property.vector2Value;
                    break;
                case SerializedPropertyType.Vector3:
                    snapshot.vector3Value = property.vector3Value;
                    break;
                case SerializedPropertyType.Vector4:
                    snapshot.vector4Value = property.vector4Value;
                    break;
                case SerializedPropertyType.Rect:
                    snapshot.rectValue = property.rectValue;
                    break;
                case SerializedPropertyType.Bounds:
                    snapshot.boundsValue = property.boundsValue;
                    break;
                case SerializedPropertyType.Quaternion:
                    snapshot.quaternionValue = property.quaternionValue;
                    break;
                case SerializedPropertyType.Vector2Int:
                    snapshot.vector2IntValue = property.vector2IntValue;
                    break;
                case SerializedPropertyType.Vector3Int:
                    snapshot.vector3IntValue = property.vector3IntValue;
                    break;
                case SerializedPropertyType.RectInt:
                    snapshot.rectIntValue = property.rectIntValue;
                    break;
                case SerializedPropertyType.BoundsInt:
                    snapshot.boundsIntValue = property.boundsIntValue;
                    break;
                case SerializedPropertyType.ExposedReference:
                    snapshot.objectReference = CaptureReference(property.exposedReferenceValue, createdObjectIds,
                        diagnostics, owner.name + "." + property.propertyPath);
                    break;
                default:
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        string.Format("'{0}.{1}' uses unsupported serialized type {2}.", owner.name,
                            property.propertyPath, property.propertyType)));
                    return;
            }

            output.Add(snapshot);
        }

        internal static bool ValidateProperties(UnityEngine.Object target, IEnumerable<AuthoringPropertySnapshot> properties,
            IDictionary<string, UnityEngine.Object> createdObjects, IList<AuthoringDiagnostic> diagnostics)
        {
            if (!target)
                return false;

            var valid = true;
            var serializedObject = new SerializedObject(target);
            foreach (var snapshot in properties)
            {
                if (!IsSafePropertyPath(snapshot.propertyPath))
                    continue;

                if (serializedObject.FindProperty(snapshot.propertyPath) == null)
                {
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        string.Format("'{0}' no longer has serialized property '{1}'.", target.name, snapshot.propertyPath)));
                    valid = false;
                }

                if (snapshot.objectReference != null
                    && snapshot.objectReference.kind != (int)AuthoringReferenceKind.Null
                    && !ResolveReference(snapshot.objectReference, createdObjects))
                {
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        string.Format("Reference for '{0}.{1}' cannot be resolved.", target.name, snapshot.propertyPath)));
                    valid = false;
                }
            }

            return valid;
        }

        internal static bool ApplyProperties(UnityEngine.Object target, IEnumerable<AuthoringPropertySnapshot> properties,
            IDictionary<string, UnityEngine.Object> createdObjects, IList<AuthoringDiagnostic> diagnostics)
        {
            var serializedObject = new SerializedObject(target);
            serializedObject.Update();
            foreach (var snapshot in properties)
            {
                if (!IsSafePropertyPath(snapshot.propertyPath))
                    continue;

                var property = serializedObject.FindProperty(snapshot.propertyPath);
                if (property == null || !IsSafeProperty(property))
                    return false;

                switch ((SerializedPropertyType)snapshot.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.longValue = snapshot.longValue;
                        break;
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Character:
                    case SerializedPropertyType.ArraySize:
                        property.intValue = (int)snapshot.longValue;
                        break;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = snapshot.boolValue;
                        break;
                    case SerializedPropertyType.Float:
                        property.doubleValue = snapshot.doubleValue;
                        break;
                    case SerializedPropertyType.String:
                        property.stringValue = snapshot.stringValue;
                        break;
                    case SerializedPropertyType.Color:
                        property.colorValue = snapshot.colorValue;
                        break;
                    case SerializedPropertyType.ObjectReference:
                        property.objectReferenceValue = ResolveReference(snapshot.objectReference, createdObjects);
                        break;
                    case SerializedPropertyType.Enum:
                        property.enumValueIndex = (int)snapshot.longValue;
                        break;
                    case SerializedPropertyType.Vector2:
                        property.vector2Value = snapshot.vector2Value;
                        break;
                    case SerializedPropertyType.Vector3:
                        property.vector3Value = snapshot.vector3Value;
                        break;
                    case SerializedPropertyType.Vector4:
                        property.vector4Value = snapshot.vector4Value;
                        break;
                    case SerializedPropertyType.Rect:
                        property.rectValue = snapshot.rectValue;
                        break;
                    case SerializedPropertyType.Bounds:
                        property.boundsValue = snapshot.boundsValue;
                        break;
                    case SerializedPropertyType.Quaternion:
                        property.quaternionValue = snapshot.quaternionValue;
                        break;
                    case SerializedPropertyType.Vector2Int:
                        property.vector2IntValue = snapshot.vector2IntValue;
                        break;
                    case SerializedPropertyType.Vector3Int:
                        property.vector3IntValue = snapshot.vector3IntValue;
                        break;
                    case SerializedPropertyType.RectInt:
                        property.rectIntValue = snapshot.rectIntValue;
                        break;
                    case SerializedPropertyType.BoundsInt:
                        property.boundsIntValue = snapshot.boundsIntValue;
                        break;
                    case SerializedPropertyType.ExposedReference:
                        property.exposedReferenceValue = ResolveReference(snapshot.objectReference, createdObjects);
                        break;
                    default:
                        diagnostics.Add(new AuthoringDiagnostic(true,
                            string.Format("Cannot apply unsupported property '{0}.{1}'.", target.name, snapshot.propertyPath)));
                        return false;
                }
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            return true;
        }

        internal static bool IsSafePropertyPath(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
                return false;

            foreach (var root in k_UnsafePropertyRoots)
            {
                if (propertyPath == root || propertyPath.StartsWith(root + ".", StringComparison.Ordinal))
                    return false;
            }

            return propertyPath != "m_Script";
        }

        static bool IsSafeProperty(SerializedProperty property)
        {
            return property != null && property.editable && IsSafePropertyPath(property.propertyPath);
        }

        internal static string GetScenePath(UnityEngine.Object target)
        {
            var gameObject = target as GameObject;
            var component = target as Component;
            if (!gameObject && component)
                gameObject = component.gameObject;
            return gameObject ? gameObject.scene.path : null;
        }

        internal static Scene FindLoadedScene(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path == path)
                    return scene;
            }

            return default(Scene);
        }
    }
}
#endif
