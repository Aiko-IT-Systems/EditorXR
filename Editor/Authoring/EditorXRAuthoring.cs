#if UNITY_2022_1_OR_NEWER
using System;
using UnityEngine;

namespace Unity.EditorXR.Authoring
{
    /// <summary>
    /// Marks Undo-backed EditorXR work as eligible for the managed authoring session change set.
    /// Keep a scope open for the complete interaction, including the frame in which Undo is flushed.
    /// </summary>
    public static class EditorXRAuthoring
    {
        public static bool sessionActive { get { return EditorXRAuthoringSession.sessionActive; } }
        public static bool scopeActive { get { return EditorXRAuthoringSession.scopeActive; } }

        public static IDisposable BeginScope(string label)
        {
            return EditorXRAuthoringSession.BeginScope(label);
        }

        public static void RecordObject(UnityEngine.Object target)
        {
            EditorXRAuthoringSession.RecordObject(target);
        }

        public static void RegisterCreatedHierarchy(GameObject root)
        {
            EditorXRAuthoringSession.RegisterCreatedHierarchy(root);
        }

        public static void DestroyHierarchy(GameObject root)
        {
            EditorXRAuthoringSession.DestroyHierarchy(root);
        }

        public static void SetTransformParent(Transform child, Transform parent)
        {
            EditorXRAuthoringSession.SetTransformParent(child, parent);
        }
    }
}
#endif
