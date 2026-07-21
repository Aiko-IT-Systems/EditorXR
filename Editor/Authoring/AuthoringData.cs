#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.EditorXR.Authoring
{
    internal enum AuthoringRecoveryState
    {
        Starting,
        Recording,
        PendingApply
    }

    internal enum AuthoringExitAction
    {
        Ask,
        Apply,
        Discard
    }

    internal enum AuthoringReferenceKind
    {
        Null,
        GlobalObjectId,
        CreatedObject
    }

    [Serializable]
    internal sealed class AuthoringRecoveryData
    {
        public const int CurrentVersion = 3;

        public int version = CurrentVersion;
        public string sessionId;
        public string state;
        public string requestedExitAction;
        public string scriptFingerprint;
        public long updatedUtcTicks;
        public List<string> scenePaths = new List<string>();
        public List<AuthoringBaselineEntry> baseline = new List<AuthoringBaselineEntry>();
        public AuthoringChangeSet changeSet = new AuthoringChangeSet();
    }

    [Serializable]
    internal sealed class AuthoringBaselineEntry
    {
        public string globalObjectId;
        public string objectType;
    }

    [Serializable]
    internal sealed class AuthoringChangeSet
    {
        public List<AuthoringObjectSnapshot> updates = new List<AuthoringObjectSnapshot>();
        public List<AuthoringHierarchySnapshot> creations = new List<AuthoringHierarchySnapshot>();
        public List<string> deletions = new List<string>();
        public List<AuthoringDiagnostic> diagnostics = new List<AuthoringDiagnostic>();
    }

    [Serializable]
    internal sealed class AuthoringDiagnostic
    {
        public bool blocking;
        public string message;

        public AuthoringDiagnostic() { }

        public AuthoringDiagnostic(bool isBlocking, string diagnosticMessage)
        {
            blocking = isBlocking;
            message = diagnosticMessage;
        }
    }

    [Serializable]
    internal sealed class AuthoringObjectSnapshot
    {
        public string targetGlobalObjectId;
        public string targetType;
        public bool isGameObject;
        public AuthoringGameObjectState gameObjectState;
        public List<AuthoringPropertySnapshot> properties = new List<AuthoringPropertySnapshot>();
    }

    [Serializable]
    internal sealed class AuthoringHierarchySnapshot
    {
        public string rootLocalId;
        public string scenePath;
        public string prefabAssetGlobalObjectId;
        public AuthoringObjectReference parent;
        public int siblingIndex;
        public AuthoringCreatedNode root;
    }

    [Serializable]
    internal sealed class AuthoringCreatedNode
    {
        public string localId;
        public string name;
        public bool activeSelf;
        public int layer;
        public string tag;
        public int staticFlags;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public List<AuthoringCreatedComponent> components = new List<AuthoringCreatedComponent>();
        public List<AuthoringCreatedNode> children = new List<AuthoringCreatedNode>();
    }

    [Serializable]
    internal sealed class AuthoringCreatedComponent
    {
        public string localId;
        public string typeName;
        public int componentIndex;
        public List<AuthoringPropertySnapshot> properties = new List<AuthoringPropertySnapshot>();
    }

    [Serializable]
    internal sealed class AuthoringGameObjectState
    {
        public string name;
        public bool activeSelf;
        public int layer;
        public string tag;
        public int staticFlags;
        public AuthoringObjectReference parent;
        public int siblingIndex;
    }

    [Serializable]
    internal sealed class AuthoringObjectReference
    {
        public int kind;
        public string value;

        public static AuthoringObjectReference Null()
        {
            return new AuthoringObjectReference { kind = (int)AuthoringReferenceKind.Null };
        }
    }

    [Serializable]
    internal sealed class AuthoringPropertySnapshot
    {
        public string propertyPath;
        public int propertyType;
        public long longValue;
        public double doubleValue;
        public bool boolValue;
        public string stringValue;
        public Color colorValue;
        public Vector2 vector2Value;
        public Vector3 vector3Value;
        public Vector4 vector4Value;
        public Quaternion quaternionValue;
        public Rect rectValue;
        public Bounds boundsValue;
        public Vector2Int vector2IntValue;
        public Vector3Int vector3IntValue;
        public RectInt rectIntValue;
        public BoundsInt boundsIntValue;
        public AuthoringObjectReference objectReference;
    }
}
#endif
