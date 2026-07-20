using System;
using UnityEngine;

namespace Unity.EditorXR.Interfaces
{
    /// <summary>
    /// Editor-backed authoring hooks that Runtime tools can call without referencing UnityEditor.
    /// The default delegates are safe no-ops in players and outside managed sessions.
    /// </summary>
    public static class AuthoringSessionMethods
    {
        static readonly IDisposable k_EmptyScope = new EmptyScope();

        public static Func<bool> isSessionActive = () => false;
        public static Func<string, IDisposable> beginScope = label => k_EmptyScope;
        public static Action<UnityEngine.Object> recordObject = target => { };
        public static Action<GameObject> registerCreatedHierarchy = root => { };
        public static Action<GameObject> destroyHierarchy = root => { };
        public static Action<Transform, Transform> setTransformParent = (child, parent) => { };

        sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
