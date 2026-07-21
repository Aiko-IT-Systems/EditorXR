#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using EditorUndo = UnityEditor.Undo;
using Unity.EditorXR.Interfaces;

namespace Unity.EditorXR.Authoring
{
    [InitializeOnLoad]
    internal static class EditorXRAuthoringSession
    {
        const string k_MenuRoot = "Window/EditorXR/Authoring/";
        const string k_ManagerTypeName = "Unity.EditorXR.Core.EditingContextManager";
        const string k_UndoLabel = "Apply EditorXR Authoring Session";

        static readonly string k_RecoveryPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
            "Library", "EditorXR", "AuthoringRecovery.json");

        static AuthoringRecoveryData s_Data;
        static readonly Dictionary<int, string> s_ExistingInstanceIds = new Dictionary<int, string>();
        static readonly HashSet<string> s_BaselineIds = new HashSet<string>();
        static readonly Dictionary<string, HashSet<string>> s_TrackedProperties = new Dictionary<string, HashSet<string>>();
        static readonly HashSet<string> s_TrackedGameObjects = new HashSet<string>();
        static readonly HashSet<int> s_CreatedRoots = new HashSet<int>();
        static readonly Dictionary<int, string> s_CreatedRootTokens = new Dictionary<int, string>();
        static readonly HashSet<int> s_HandledDestroyedInstances = new HashSet<int>();
        static readonly HashSet<string> s_Deletions = new HashSet<string>();
        static readonly List<AuthoringDiagnostic> s_Diagnostics = new List<AuthoringDiagnostic>();

        static int s_ScopeDepth;
        static int s_GraceUpdates;
        static string s_ScopeLabel = "EditorXR Authoring";
        static bool s_SnapshotDirty;
        static double s_NextRecoveryWrite;
        static bool s_Applying;

        internal static bool sessionActive
        {
            get { return s_Data != null && Application.isPlaying && GetState() == AuthoringRecoveryState.Recording; }
        }

        internal static bool scopeActive { get { return sessionActive && s_ScopeDepth > 0; } }
        static bool captureActive { get { return sessionActive && (s_ScopeDepth > 0 || s_GraceUpdates > 0); } }

        static EditorXRAuthoringSession()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorUndo.postprocessModifications += OnPostprocessModifications;
            CompilationPipeline.compilationStarted += OnCompilationStarted;

            AuthoringSessionMethods.isSessionActive = () => sessionActive;
            AuthoringSessionMethods.beginScope = BeginScope;
            AuthoringSessionMethods.recordObject = RecordObject;
            AuthoringSessionMethods.registerCreatedHierarchy = RegisterCreatedHierarchy;
            AuthoringSessionMethods.destroyHierarchy = DestroyHierarchy;
            AuthoringSessionMethods.setTransformParent = SetTransformParent;

            LoadRecovery();
            if (s_Data != null && Application.isPlaying && GetState() == AuthoringRecoveryState.Recording)
                RehydrateRecordingState();
        }

        [MenuItem(k_MenuRoot + "Start VR Authoring Session", false, 2000)]
        static void StartSession()
        {
            var diagnostics = RunPreflight();
            var blocking = diagnostics.Where(diagnostic => diagnostic.blocking).ToArray();
            if (blocking.Length > 0)
            {
                EditorUtility.DisplayDialog("EditorXR Authoring Preflight Failed",
                    string.Join("\n\n", blocking.Select(diagnostic => diagnostic.message).ToArray()), "Close");
                return;
            }

            var warnings = diagnostics.Where(diagnostic => !diagnostic.blocking).ToArray();
            if (warnings.Length > 0 && !EditorUtility.DisplayDialog("EditorXR Authoring Preflight",
                    string.Join("\n\n", warnings.Select(diagnostic => diagnostic.message).ToArray())
                    + "\n\nStart the session anyway?", "Start", "Cancel"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            if (!EditorSceneManager.SaveOpenScenes())
            {
                EditorUtility.DisplayDialog("EditorXR Authoring", "The open scenes could not be saved.", "Close");
                return;
            }

            s_Data = new AuthoringRecoveryData
            {
                sessionId = Guid.NewGuid().ToString("N"),
                state = AuthoringRecoveryState.Starting.ToString(),
                requestedExitAction = AuthoringExitAction.Ask.ToString(),
                scriptFingerprint = ComputeScriptFingerprint()
            };
            s_Data.scenePaths.AddRange(GetSessionScenePaths());
            BuildBaseline();
            ResetTracking();
            WriteRecovery();
            EditorApplication.EnterPlaymode();
        }

        [MenuItem(k_MenuRoot + "Start VR Authoring Session", true)]
        static bool CanStartSession()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && s_Data == null;
        }

        [MenuItem(k_MenuRoot + "Apply Changes and Exit", false, 2010)]
        static void ApplyAndExit()
        {
            RequestExit(AuthoringExitAction.Apply);
        }

        [MenuItem(k_MenuRoot + "Apply Changes and Exit", true)]
        static bool CanApplyAndExit()
        {
            return sessionActive;
        }

        [MenuItem(k_MenuRoot + "Discard Changes and Exit", false, 2011)]
        static void DiscardAndExit()
        {
            RequestExit(AuthoringExitAction.Discard);
        }

        [MenuItem(k_MenuRoot + "Discard Changes and Exit", true)]
        static bool CanDiscardAndExit()
        {
            return sessionActive;
        }

        [MenuItem(k_MenuRoot + "Recover Pending Changes", false, 2020)]
        static void RecoverPendingChanges()
        {
            LoadRecovery();
            if (s_Data == null)
                return;

            if (GetState() != AuthoringRecoveryState.PendingApply)
            {
                s_Data.state = AuthoringRecoveryState.PendingApply.ToString();
                s_Data.requestedExitAction = AuthoringExitAction.Ask.ToString();
                WriteRecovery();
            }
            PromptForPendingChanges();
        }

        [MenuItem(k_MenuRoot + "Recover Pending Changes", true)]
        static bool CanRecoverPendingChanges()
        {
            return !Application.isPlaying && File.Exists(k_RecoveryPath);
        }

        internal static IDisposable BeginScope(string label)
        {
            if (!sessionActive)
                return EmptyScope.instance;

            ++s_ScopeDepth;
            s_GraceUpdates = 0;
            if (!string.IsNullOrEmpty(label))
                s_ScopeLabel = label;
            return new AuthoringScope();
        }

        internal static void RecordObject(UnityEngine.Object target)
        {
            if (target)
                EditorUndo.RecordObject(target, s_ScopeLabel);
            TrackTarget(target, null);
        }

        internal static void RegisterCreatedHierarchy(GameObject root)
        {
            if (!root)
                return;

            if (sessionActive && root.transform.parent == null
                && !s_Data.scenePaths.Any(path => string.Equals(path, root.scene.path,
                    StringComparison.OrdinalIgnoreCase)))
            {
                var authoringScene = s_Data.scenePaths.Select(AuthoringSnapshotUtility.FindLoadedScene)
                    .FirstOrDefault(scene => scene.IsValid());
                if (authoringScene.IsValid())
                    SceneManager.MoveGameObjectToScene(root, authoringScene);
            }

            if (!sessionActive || !s_CreatedRoots.Contains(root.GetInstanceID()))
                EditorUndo.RegisterCreatedObjectUndo(root, s_ScopeLabel);
            TrackCreatedRoot(root);
        }

        internal static void DestroyHierarchy(GameObject root)
        {
            if (!root)
                return;

            TrackDeletion(root);
            s_HandledDestroyedInstances.Add(root.GetInstanceID());
            EditorUndo.DestroyObjectImmediate(root);
        }

        internal static void SetTransformParent(Transform child, Transform parent)
        {
            if (!child)
                return;

            TrackGameObject(child.gameObject, true);
            EditorUndo.SetTransformParent(child, parent, s_ScopeLabel);
        }

        static void EndScope()
        {
            if (s_ScopeDepth == 0)
                return;

            --s_ScopeDepth;
            if (s_ScopeDepth == 0)
            {
                // ObjectChangeEvents are published at the end of the frame, after most tool scopes close.
                s_GraceUpdates = 2;
                s_SnapshotDirty = true;
            }
        }

        static void RequestExit(AuthoringExitAction action)
        {
            if (!sessionActive)
                return;

            s_Data.requestedExitAction = action.ToString();
            WriteRecovery();
            EditorApplication.ExitPlaymode();
        }

        static List<AuthoringDiagnostic> RunPreflight()
        {
            var diagnostics = new List<AuthoringDiagnostic>();
            if (!Application.unityVersion.StartsWith("2022.3", StringComparison.Ordinal))
                diagnostics.Add(new AuthoringDiagnostic(true,
                    "This authoring session is supported only on Unity 2022.3 LTS. Current version: " + Application.unityVersion));
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                diagnostics.Add(new AuthoringDiagnostic(true, "Wait for compilation and asset importing to finish."));
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                diagnostics.Add(new AuthoringDiagnostic(true, "Close Prefab Mode before starting a VR authoring session."));
            if (File.Exists(k_RecoveryPath))
                diagnostics.Add(new AuthoringDiagnostic(true,
                    "A previous recovery file exists. Recover or discard it before starting another session."));

            var scenePaths = GetSessionScenePaths();
            if (scenePaths.Count == 0)
                diagnostics.Add(new AuthoringDiagnostic(true, "No saved scene is open."));
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.IsNullOrEmpty(scene.path))
                    diagnostics.Add(new AuthoringDiagnostic(true, "Every open scene must be saved before authoring."));
            }

            var managers = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(behaviour => behaviour && behaviour.GetType().FullName == k_ManagerTypeName
                    && behaviour.gameObject.scene.IsValid()).ToArray();
            if (managers.Length == 0)
                diagnostics.Add(new AuthoringDiagnostic(true,
                    "No EditingContextManager exists in the open scenes."));
            else if (!managers.Any(manager => manager.enabled && manager.gameObject.activeInHierarchy))
                diagnostics.Add(new AuthoringDiagnostic(true,
                    "The EditingContextManager is disabled or inactive."));
            else if (managers.Length > 1)
                diagnostics.Add(new AuthoringDiagnostic(false,
                    "Multiple EditingContextManagers were found. EditorXR may initialize more than once."));

            var eventSystemCount = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Count(behaviour =>
                behaviour && behaviour.GetType().FullName == "UnityEngine.EventSystems.EventSystem"
                && behaviour.gameObject.scene.IsValid() && behaviour.gameObject.activeInHierarchy);
            if (eventSystemCount > 1)
                diagnostics.Add(new AuthoringDiagnostic(false,
                    "Multiple active EventSystems were found. VR UI input may be ambiguous."));
            return diagnostics;
        }

        static List<string> GetSessionScenePaths()
        {
            var paths = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    paths.Add(scene.path);
            }

            return paths;
        }

        static void BuildBaseline()
        {
            s_Data.baseline.Clear();
            s_BaselineIds.Clear();
            foreach (var scenePath in s_Data.scenePaths)
            {
                var scene = AuthoringSnapshotUtility.FindLoadedScene(scenePath);
                if (!scene.IsValid())
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                        AddBaselineObject(transform.gameObject);
                }
            }
        }

        static void AddBaselineObject(UnityEngine.Object target)
        {
            string id;
            if (AuthoringSnapshotUtility.TryGetGlobalId(target, out id))
            {
                s_BaselineIds.Add(id);
                s_Data.baseline.Add(new AuthoringBaselineEntry
                {
                    globalObjectId = id,
                    objectType = target.GetType().AssemblyQualifiedName
                });
            }
        }

        static void RebuildInstanceMap()
        {
            s_ExistingInstanceIds.Clear();
            if (s_Data == null)
                return;

            var baselineIds = new HashSet<string>(s_Data.baseline.Select(entry => entry.globalObjectId));
            foreach (var scenePath in s_Data.scenePaths)
            {
                var scene = AuthoringSnapshotUtility.FindLoadedScene(scenePath);
                if (!scene.IsValid())
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        string id;
                        if (AuthoringSnapshotUtility.TryGetGlobalId(transform.gameObject, out id)
                            && baselineIds.Contains(id))
                            s_ExistingInstanceIds[transform.gameObject.GetInstanceID()] = id;
                    }
                }
            }
        }

        static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (!captureActive || s_Applying)
                return modifications;

            foreach (var modification in modifications)
            {
                var current = modification.currentValue;
                if (current == null || !current.target)
                    continue;

                TrackTarget(current.target, current.propertyPath);
            }

            return modifications;
        }

        static void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (!captureActive || s_Applying)
                return;

            for (var i = 0; i < stream.length; ++i)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var createEvent);
                        TrackCreatedRoot(EditorUtility.InstanceIDToObject(createEvent.instanceId) as GameObject);
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyEvent);
                        TrackDestroyedInstance(destroyEvent.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parentEvent);
                        TrackGameObject(EditorUtility.InstanceIDToObject(parentEvent.instanceId) as GameObject, true);
                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var childrenEvent);
                        var parent = EditorUtility.InstanceIDToObject(childrenEvent.instanceId) as GameObject;
                        if (parent)
                        {
                            foreach (Transform child in parent.transform)
                                TrackGameObject(child.gameObject, true);
                        }
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propertyEvent);
                        TrackPropertyEventFallback(EditorUtility.InstanceIDToObject(propertyEvent.instanceId));
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        AddDiagnostic(true,
                            "Adding or removing components on an existing object is not safely supported yet.");
                        break;
                    case ObjectChangeKind.ChangeScene:
                        AddDiagnostic(true,
                            "An unclassified scene change occurred inside an authoring scope and cannot be replayed safely.");
                        break;
                    case ObjectChangeKind.CreateAssetObject:
                    case ObjectChangeKind.DestroyAssetObject:
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        AddDiagnostic(true, "Project asset mutations are outside the authoring session boundary.");
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        AddDiagnostic(true,
                            "A prefab source changed during the authoring scope. Prefab asset edits are not persisted by this slice.");
                        break;
                }
            }

            if (s_ScopeDepth == 0)
                s_GraceUpdates = 0;
            s_SnapshotDirty = true;
        }

        static void TrackTarget(UnityEngine.Object target, string propertyPath)
        {
            if (!captureActive || !target || IsInsideCreatedHierarchy(target))
                return;

            string reason;
            if (!AuthoringSnapshotUtility.IsEligible(target, s_Data.scenePaths, out reason))
            {
                AddDiagnostic(true, string.Format("Cannot persist '{0}': {1}", target.name, reason));
                return;
            }

            var gameObject = target as GameObject;
            if (gameObject)
            {
                TrackGameObject(gameObject, target is Transform);
                return;
            }

            string id;
            if (!AuthoringSnapshotUtility.TryGetGlobalId(target, out id))
            {
                AddDiagnostic(true, "A changed object has no stable GlobalObjectId: " + target.name);
                return;
            }

            if (!s_BaselineIds.Contains(id))
                return;

            HashSet<string> paths;
            if (!s_TrackedProperties.TryGetValue(id, out paths))
            {
                paths = new HashSet<string>();
                s_TrackedProperties[id] = paths;
            }

            if (string.IsNullOrEmpty(propertyPath))
            {
                paths.Clear();
                paths.Add("*");
            }
            else if (!paths.Contains("*"))
            {
                paths.Add(propertyPath);
            }

            s_SnapshotDirty = true;
        }

        static void TrackPropertyEventFallback(UnityEngine.Object target)
        {
            if (!target || IsInsideCreatedHierarchy(target))
                return;

            var gameObject = target as GameObject;
            if (gameObject)
            {
                TrackGameObject(gameObject, false);
                return;
            }

            string id;
            if (!AuthoringSnapshotUtility.TryGetGlobalId(target, out id) || s_TrackedProperties.ContainsKey(id))
                return;

            // Undo.postprocessModifications normally supplies precise paths. This fallback is deliberately
            // conservative for custom tools that publish an object change without property details.
            TrackTarget(target, null);
        }

        static void TrackGameObject(GameObject gameObject, bool includeTransform)
        {
            if (!captureActive || !gameObject || IsInsideCreatedHierarchy(gameObject))
                return;

            string reason;
            string id;
            if (!AuthoringSnapshotUtility.IsEligible(gameObject, s_Data.scenePaths, out reason)
                || !AuthoringSnapshotUtility.TryGetGlobalId(gameObject, out id))
            {
                AddDiagnostic(true, string.Format("Cannot persist '{0}': {1}", gameObject.name, reason));
                return;
            }

            if (!s_BaselineIds.Contains(id))
                return;

            s_TrackedGameObjects.Add(id);
            if (includeTransform)
            {
                string transformId;
                if (AuthoringSnapshotUtility.TryGetGlobalId(gameObject.transform, out transformId))
                {
                    s_TrackedProperties[transformId] = new HashSet<string>
                    {
                        "m_LocalPosition", "m_LocalRotation", "m_LocalScale"
                    };
                }
            }

            s_SnapshotDirty = true;
        }

        static void TrackCreatedRoot(GameObject root)
        {
            if (!captureActive || !root)
                return;

            string reason;
            if (!AuthoringSnapshotUtility.IsEligible(root, s_Data.scenePaths, out reason))
            {
                AddDiagnostic(true, string.Format("Cannot persist created object '{0}': {1}", root.name, reason));
                return;
            }

            var instanceId = root.GetInstanceID();
            s_CreatedRoots.Add(instanceId);
            if (!s_CreatedRootTokens.ContainsKey(instanceId))
                s_CreatedRootTokens[instanceId] = Guid.NewGuid().ToString("N");
            s_SnapshotDirty = true;
        }

        static void TrackDeletion(GameObject root)
        {
            if (!captureActive || !root)
                return;

            var createdRoot = FindCreatedRoot(root.transform);
            if (createdRoot)
            {
                if (createdRoot == root)
                    s_CreatedRoots.Remove(root.GetInstanceID());
                s_SnapshotDirty = true;
                return;
            }

            string id;
            if (!AuthoringSnapshotUtility.TryGetGlobalId(root, out id))
            {
                AddDiagnostic(true, "Deleted hierarchy had no stable GlobalObjectId: " + root.name);
                return;
            }

            if (!s_BaselineIds.Contains(id))
                return;

            s_Deletions.Add(id);
            s_SnapshotDirty = true;
        }

        static void TrackDestroyedInstance(int instanceId)
        {
            if (s_HandledDestroyedInstances.Remove(instanceId))
                return;

            if (s_CreatedRoots.Remove(instanceId))
            {
                s_SnapshotDirty = true;
                return;
            }

            string id;
            if (s_ExistingInstanceIds.TryGetValue(instanceId, out id))
            {
                s_Deletions.Add(id);
                s_SnapshotDirty = true;
            }
        }

        static bool IsInsideCreatedHierarchy(UnityEngine.Object target)
        {
            var gameObject = target as GameObject;
            var component = target as Component;
            if (!gameObject && component)
                gameObject = component.gameObject;
            return gameObject && FindCreatedRoot(gameObject.transform);
        }

        static GameObject FindCreatedRoot(Transform transform)
        {
            for (var current = transform; current; current = current.parent)
            {
                if (s_CreatedRoots.Contains(current.gameObject.GetInstanceID()))
                    return current.gameObject;
            }

            return null;
        }

        static void OnEditorUpdate()
        {
            if (s_GraceUpdates > 0 && s_ScopeDepth == 0)
                --s_GraceUpdates;

            if (!sessionActive || !s_SnapshotDirty || EditorApplication.timeSinceStartup < s_NextRecoveryWrite)
                return;

            CaptureChangeSet();
            WriteRecovery();
            s_SnapshotDirty = false;
            s_NextRecoveryWrite = EditorApplication.timeSinceStartup + 0.5d;
        }

        static void CaptureChangeSet()
        {
            if (s_Data == null)
                return;

            var changeSet = new AuthoringChangeSet();
            changeSet.deletions.AddRange(s_Deletions.Where(s_BaselineIds.Contains));
            changeSet.diagnostics.AddRange(s_Diagnostics);

            var createdIds = new Dictionary<int, string>();
            var roots = new List<GameObject>();
            foreach (var instanceId in s_CreatedRoots.ToArray())
            {
                var root = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (!root)
                    continue;
                var parentCreatedRoot = root.transform.parent ? FindCreatedRoot(root.transform.parent) : null;
                if (!parentCreatedRoot)
                    roots.Add(root);
            }

            foreach (var root in roots)
            {
                string token;
                if (!s_CreatedRootTokens.TryGetValue(root.GetInstanceID(), out token))
                    token = s_CreatedRootTokens[root.GetInstanceID()] = Guid.NewGuid().ToString("N");
                AssignCreatedIds(root.transform, token, "0", createdIds);
            }

            foreach (var root in roots)
                changeSet.creations.Add(CaptureHierarchy(root, createdIds, changeSet.diagnostics));

            foreach (var gameObjectId in s_TrackedGameObjects)
            {
                if (!s_BaselineIds.Contains(gameObjectId))
                    continue;
                var gameObject = AuthoringSnapshotUtility.ResolveGlobalId(gameObjectId) as GameObject;
                if (!gameObject || s_Deletions.Contains(gameObjectId))
                    continue;
                changeSet.updates.Add(CaptureGameObjectSnapshot(gameObject, createdIds, changeSet.diagnostics));
            }

            foreach (var pair in s_TrackedProperties)
            {
                if (!s_BaselineIds.Contains(pair.Key))
                    continue;
                var target = AuthoringSnapshotUtility.ResolveGlobalId(pair.Key);
                if (!target)
                    continue;
                var requestedPaths = pair.Value.Contains("*") ? null : pair.Value;
                changeSet.updates.Add(new AuthoringObjectSnapshot
                {
                    targetGlobalObjectId = pair.Key,
                    targetType = target.GetType().AssemblyQualifiedName,
                    isGameObject = false,
                    properties = AuthoringSnapshotUtility.CaptureProperties(target, requestedPaths, createdIds,
                        changeSet.diagnostics)
                });
            }

            s_Data.changeSet = changeSet;
        }

        static void AssignCreatedIds(Transform transform, string token, string path, IDictionary<int, string> ids)
        {
            var nodeId = token + ":go:" + path;
            ids[transform.gameObject.GetInstanceID()] = nodeId;
            var components = transform.GetComponents<Component>();
            for (var i = 0; i < components.Length; ++i)
            {
                if (components[i])
                    ids[components[i].GetInstanceID()] = token + ":component:" + path + ":" + i;
            }

            for (var i = 0; i < transform.childCount; ++i)
                AssignCreatedIds(transform.GetChild(i), token, path + "." + i, ids);
        }

        static AuthoringHierarchySnapshot CaptureHierarchy(GameObject root, IDictionary<int, string> createdIds,
            IList<AuthoringDiagnostic> diagnostics)
        {
            var snapshot = new AuthoringHierarchySnapshot
            {
                rootLocalId = createdIds[root.GetInstanceID()],
                scenePath = root.scene.path,
                siblingIndex = root.transform.GetSiblingIndex(),
                parent = AuthoringSnapshotUtility.CaptureReference(root.transform.parent
                    ? (UnityEngine.Object)root.transform.parent.gameObject : null, createdIds, diagnostics, root.name + ".parent"),
                root = CaptureCreatedNode(root.transform, createdIds, diagnostics)
            };

            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(root);
            string prefabId;
            if (prefabSource && AuthoringSnapshotUtility.TryGetGlobalId(prefabSource, out prefabId))
                snapshot.prefabAssetGlobalObjectId = prefabId;
            return snapshot;
        }

        static AuthoringCreatedNode CaptureCreatedNode(Transform transform, IDictionary<int, string> createdIds,
            IList<AuthoringDiagnostic> diagnostics)
        {
            var gameObject = transform.gameObject;
            var node = new AuthoringCreatedNode
            {
                localId = createdIds[gameObject.GetInstanceID()],
                name = gameObject.name,
                activeSelf = gameObject.activeSelf,
                layer = gameObject.layer,
                tag = gameObject.tag,
                staticFlags = (int)GameObjectUtility.GetStaticEditorFlags(gameObject),
                localPosition = transform.localPosition,
                localRotation = transform.localRotation,
                localScale = transform.localScale
            };

            var components = gameObject.GetComponents<Component>();
            for (var i = 1; i < components.Length; ++i)
            {
                var component = components[i];
                if (!component)
                {
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        "Created object contains a missing script: " + gameObject.name));
                    continue;
                }

                node.components.Add(new AuthoringCreatedComponent
                {
                    localId = createdIds[component.GetInstanceID()],
                    typeName = component.GetType().AssemblyQualifiedName,
                    componentIndex = i,
                    properties = AuthoringSnapshotUtility.CaptureProperties(component, null, createdIds, diagnostics)
                });
            }

            for (var i = 0; i < transform.childCount; ++i)
                node.children.Add(CaptureCreatedNode(transform.GetChild(i), createdIds, diagnostics));
            return node;
        }

        static AuthoringObjectSnapshot CaptureGameObjectSnapshot(GameObject gameObject,
            IDictionary<int, string> createdIds, IList<AuthoringDiagnostic> diagnostics)
        {
            string id;
            AuthoringSnapshotUtility.TryGetGlobalId(gameObject, out id);
            return new AuthoringObjectSnapshot
            {
                targetGlobalObjectId = id,
                targetType = typeof(GameObject).AssemblyQualifiedName,
                isGameObject = true,
                gameObjectState = new AuthoringGameObjectState
                {
                    name = gameObject.name,
                    activeSelf = gameObject.activeSelf,
                    layer = gameObject.layer,
                    tag = gameObject.tag,
                    staticFlags = (int)GameObjectUtility.GetStaticEditorFlags(gameObject),
                    siblingIndex = gameObject.transform.GetSiblingIndex(),
                    parent = AuthoringSnapshotUtility.CaptureReference(gameObject.transform.parent
                        ? (UnityEngine.Object)gameObject.transform.parent.gameObject : null, createdIds, diagnostics,
                        gameObject.name + ".parent")
                }
            };
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && s_Data != null
                && GetState() == AuthoringRecoveryState.Starting)
            {
                s_Data.state = AuthoringRecoveryState.Recording.ToString();
                RebuildInstanceMap();
                WriteRecovery();
                Debug.Log("[EditorXR Authoring] Session started. Only changes inside EditorXRAuthoring scopes are eligible for persistence.");
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && s_Data != null
                && GetState() == AuthoringRecoveryState.Recording)
            {
                if (s_ScopeDepth > 0)
                    AddDiagnostic(true, "Play Mode exited while an EditorXR authoring scope was still open.");
                CaptureChangeSet();
                s_Data.state = AuthoringRecoveryState.PendingApply.ToString();
                WriteRecovery();
            }
            else if (state == PlayModeStateChange.EnteredEditMode && s_Data != null
                && GetState() == AuthoringRecoveryState.PendingApply)
            {
                EditorApplication.delayCall += HandlePendingExit;
            }
        }

        static void HandlePendingExit()
        {
            if (s_Data == null)
                LoadRecovery();
            if (s_Data == null)
                return;

            var action = GetExitAction();
            if (action == AuthoringExitAction.Apply)
                ApplyPendingChanges();
            else if (action == AuthoringExitAction.Discard)
                DiscardRecovery();
            else
                PromptForPendingChanges();
        }

        static void PromptForPendingChanges()
        {
            var count = s_Data.changeSet.updates.Count + s_Data.changeSet.creations.Count + s_Data.changeSet.deletions.Count;
            var blocking = s_Data.changeSet.diagnostics.Count(diagnostic => diagnostic.blocking);
            var choice = EditorUtility.DisplayDialogComplex("EditorXR Authoring Session",
                string.Format("Captured {0} change record(s). Blocking diagnostics: {1}.\n\nApply changes to Edit Mode?",
                    count, blocking), "Apply", "Discard", "Keep Recovery");
            if (choice == 0)
                ApplyPendingChanges();
            else if (choice == 1)
                DiscardRecovery();
        }

        static void ApplyPendingChanges()
        {
            var validation = ValidateChangeSet();
            if (validation.Any(diagnostic => diagnostic.blocking))
            {
                s_Data.changeSet.diagnostics = validation;
                WriteRecovery();
                EditorUtility.DisplayDialog("EditorXR Changes Not Applied",
                    string.Join("\n\n", validation.Where(diagnostic => diagnostic.blocking)
                        .Select(diagnostic => diagnostic.message).ToArray()), "Keep Recovery");
                return;
            }

            var undoGroup = -1;
            try
            {
                s_Applying = true;
                EditorUndo.IncrementCurrentGroup();
                undoGroup = EditorUndo.GetCurrentGroup();
                EditorUndo.SetCurrentGroupName(k_UndoLabel);

                var createdObjects = new Dictionary<string, UnityEngine.Object>();
                foreach (var creation in s_Data.changeSet.creations)
                    CreateHierarchy(creation, createdObjects);
                foreach (var creation in s_Data.changeSet.creations)
                    ApplyCreatedHierarchy(creation, createdObjects);

                foreach (var update in s_Data.changeSet.updates)
                    ApplyUpdate(update, createdObjects);

                foreach (var id in s_Data.changeSet.deletions)
                {
                    var target = AuthoringSnapshotUtility.ResolveGlobalId(id) as GameObject;
                    if (target)
                        EditorUndo.DestroyObjectImmediate(target);
                }

                foreach (var path in s_Data.scenePaths)
                {
                    var scene = AuthoringSnapshotUtility.FindLoadedScene(path);
                    if (scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(scene);
                }

                EditorUndo.CollapseUndoOperations(undoGroup);
                DiscardRecovery();
                Debug.Log("[EditorXR Authoring] Changes applied as one Undo group. Scenes were left dirty for review.");
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                    EditorUndo.RevertAllDownToGroup(undoGroup);
                AddDiagnostic(true, "Apply failed and was reverted: " + exception.Message);
                CaptureDiagnosticsOnly();
                WriteRecovery();
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("EditorXR Apply Failed",
                    "No partial change was kept. The recovery file remains available.\n\n" + exception.Message, "Close");
            }
            finally
            {
                s_Applying = false;
            }
        }

        static List<AuthoringDiagnostic> ValidateChangeSet()
        {
            var diagnostics = new List<AuthoringDiagnostic>(s_Data.changeSet.diagnostics);
            if (s_Data.version != AuthoringRecoveryData.CurrentVersion)
                diagnostics.Add(new AuthoringDiagnostic(true, "The recovery file version is not supported."));
            if (s_Data.scriptFingerprint != ComputeScriptFingerprint())
                diagnostics.Add(new AuthoringDiagnostic(true,
                    "Scripts changed during or after the session. Recompile-safe replay cannot be guaranteed."));

            foreach (var path in s_Data.scenePaths)
            {
                if (!AuthoringSnapshotUtility.FindLoadedScene(path).IsValid())
                    diagnostics.Add(new AuthoringDiagnostic(true, "Authoring scene is no longer loaded: " + path));
            }

            var createdIds = CollectCreatedIds(s_Data.changeSet.creations);
            foreach (var id in s_Data.changeSet.deletions)
            {
                if (!(AuthoringSnapshotUtility.ResolveGlobalId(id) is GameObject))
                    diagnostics.Add(new AuthoringDiagnostic(true, "Deleted target can no longer be resolved: " + id));
            }

            foreach (var update in s_Data.changeSet.updates)
            {
                var target = AuthoringSnapshotUtility.ResolveGlobalId(update.targetGlobalObjectId);
                if (!target)
                {
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        "Updated target can no longer be resolved: " + update.targetGlobalObjectId));
                    continue;
                }

                if (target.GetType().AssemblyQualifiedName != update.targetType)
                    diagnostics.Add(new AuthoringDiagnostic(true, "Updated target type changed: " + target.name));
                ValidatePropertyShape(target, update.properties, createdIds, diagnostics);
                if (update.isGameObject)
                    ValidateReferenceShape(update.gameObjectState.parent, createdIds, diagnostics, target.name + ".parent");
            }

            foreach (var creation in s_Data.changeSet.creations)
            {
                var scene = AuthoringSnapshotUtility.FindLoadedScene(creation.scenePath);
                if (!scene.IsValid())
                    continue;
                ValidateReferenceShape(creation.parent, createdIds, diagnostics, creation.root.name + ".parent");
                if (!string.IsNullOrEmpty(creation.prefabAssetGlobalObjectId)
                    && !(AuthoringSnapshotUtility.ResolveGlobalId(creation.prefabAssetGlobalObjectId) is GameObject))
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        "Prefab source can no longer be resolved for " + creation.root.name));
                ValidateCreatedNode(creation.root, createdIds, diagnostics);
            }

            return diagnostics;
        }

        static HashSet<string> CollectCreatedIds(IEnumerable<AuthoringHierarchySnapshot> creations)
        {
            var result = new HashSet<string>();
            foreach (var creation in creations)
                CollectCreatedIds(creation.root, result);
            return result;
        }

        static void CollectCreatedIds(AuthoringCreatedNode node, ISet<string> result)
        {
            result.Add(node.localId);
            foreach (var component in node.components)
                result.Add(component.localId);
            foreach (var child in node.children)
                CollectCreatedIds(child, result);
        }

        static void ValidateCreatedNode(AuthoringCreatedNode node, ISet<string> createdIds,
            IList<AuthoringDiagnostic> diagnostics)
        {
            foreach (var component in node.components)
            {
                var type = Type.GetType(component.typeName, false);
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                {
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        "Component type is unavailable: " + component.typeName));
                    continue;
                }

                foreach (var property in component.properties)
                    ValidateReferenceShape(property.objectReference, createdIds, diagnostics,
                        node.name + "." + property.propertyPath);
            }

            foreach (var child in node.children)
                ValidateCreatedNode(child, createdIds, diagnostics);
        }

        static void ValidatePropertyShape(UnityEngine.Object target, IEnumerable<AuthoringPropertySnapshot> properties,
            ISet<string> createdIds, IList<AuthoringDiagnostic> diagnostics)
        {
            var serializedObject = new SerializedObject(target);
            foreach (var property in properties)
            {
                if (!AuthoringSnapshotUtility.IsSafePropertyPath(property.propertyPath))
                    continue;
                if (serializedObject.FindProperty(property.propertyPath) == null)
                    diagnostics.Add(new AuthoringDiagnostic(true,
                        string.Format("'{0}' no longer has property '{1}'.", target.name, property.propertyPath)));
                ValidateReferenceShape(property.objectReference, createdIds, diagnostics,
                    target.name + "." + property.propertyPath);
            }
        }

        static void ValidateReferenceShape(AuthoringObjectReference reference, ISet<string> createdIds,
            IList<AuthoringDiagnostic> diagnostics, string context)
        {
            if (reference == null || reference.kind == (int)AuthoringReferenceKind.Null)
                return;
            if (reference.kind == (int)AuthoringReferenceKind.GlobalObjectId
                && !AuthoringSnapshotUtility.ResolveGlobalId(reference.value))
                diagnostics.Add(new AuthoringDiagnostic(true, "Unresolved reference at " + context));
            else if (reference.kind == (int)AuthoringReferenceKind.CreatedObject
                && !createdIds.Contains(reference.value))
                diagnostics.Add(new AuthoringDiagnostic(true, "Missing created-object reference at " + context));
        }

        static void CreateHierarchy(AuthoringHierarchySnapshot snapshot,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            var scene = AuthoringSnapshotUtility.FindLoadedScene(snapshot.scenePath);
            GameObject root;
            if (!string.IsNullOrEmpty(snapshot.prefabAssetGlobalObjectId))
            {
                var prefab = AuthoringSnapshotUtility.ResolveGlobalId(snapshot.prefabAssetGlobalObjectId) as GameObject;
                root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                ValidatePrefabShape(root.transform, snapshot.root);
                MapExistingHierarchy(root.transform, snapshot.root, createdObjects);
            }
            else
            {
                root = CreateBlankNode(snapshot.root, createdObjects);
                SceneManager.MoveGameObjectToScene(root, scene);
            }

            EditorUndo.RegisterCreatedObjectUndo(root, k_UndoLabel);
        }

        static GameObject CreateBlankNode(AuthoringCreatedNode node,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            var gameObject = new GameObject(node.name);
            createdObjects[node.localId] = gameObject;
            createdObjects[node.localId.Replace(":go:", ":component:") + ":0"] = gameObject.transform;
            foreach (var componentSnapshot in node.components)
            {
                var component = gameObject.AddComponent(Type.GetType(componentSnapshot.typeName, true));
                createdObjects[componentSnapshot.localId] = component;
            }

            foreach (var childSnapshot in node.children)
            {
                var child = CreateBlankNode(childSnapshot, createdObjects);
                child.transform.SetParent(gameObject.transform, false);
            }

            return gameObject;
        }

        static void ValidatePrefabShape(Transform transform, AuthoringCreatedNode node)
        {
            if (transform.childCount != node.children.Count)
                throw new InvalidOperationException("Prefab hierarchy changed for " + node.name);
            var components = transform.GetComponents<Component>();
            foreach (var snapshot in node.components)
            {
                if (snapshot.componentIndex >= components.Length || !components[snapshot.componentIndex]
                    || components[snapshot.componentIndex].GetType().AssemblyQualifiedName != snapshot.typeName)
                    throw new InvalidOperationException("Prefab component layout changed for " + node.name);
            }

            for (var i = 0; i < node.children.Count; ++i)
                ValidatePrefabShape(transform.GetChild(i), node.children[i]);
        }

        static void MapExistingHierarchy(Transform transform, AuthoringCreatedNode node,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            createdObjects[node.localId] = transform.gameObject;
            var components = transform.GetComponents<Component>();
            createdObjects[node.localId.Replace(":go:", ":component:") + ":0"] = transform;
            foreach (var snapshot in node.components)
                createdObjects[snapshot.localId] = components[snapshot.componentIndex];
            for (var i = 0; i < node.children.Count; ++i)
                MapExistingHierarchy(transform.GetChild(i), node.children[i], createdObjects);
        }

        static void ApplyCreatedHierarchy(AuthoringHierarchySnapshot snapshot,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            var root = (GameObject)createdObjects[snapshot.root.localId];
            var parent = AuthoringSnapshotUtility.ResolveReference(snapshot.parent, createdObjects) as GameObject;
            EditorUndo.SetTransformParent(root.transform, parent ? parent.transform : null, k_UndoLabel);
            root.transform.SetSiblingIndex(Mathf.Min(snapshot.siblingIndex,
                root.transform.parent ? root.transform.parent.childCount - 1 : root.scene.rootCount - 1));
            ApplyCreatedNode(snapshot.root, createdObjects);
        }

        static void ApplyCreatedNode(AuthoringCreatedNode node, IDictionary<string, UnityEngine.Object> createdObjects)
        {
            var gameObject = (GameObject)createdObjects[node.localId];
            EditorUndo.RecordObject(gameObject, k_UndoLabel);
            gameObject.name = node.name;
            gameObject.layer = node.layer;
            gameObject.tag = node.tag;
            gameObject.SetActive(node.activeSelf);
            GameObjectUtility.SetStaticEditorFlags(gameObject, (StaticEditorFlags)node.staticFlags);

            EditorUndo.RecordObject(gameObject.transform, k_UndoLabel);
            gameObject.transform.localPosition = node.localPosition;
            gameObject.transform.localRotation = node.localRotation;
            gameObject.transform.localScale = node.localScale;
            foreach (var componentSnapshot in node.components)
            {
                var component = (Component)createdObjects[componentSnapshot.localId];
                EditorUndo.RecordObject(component, k_UndoLabel);
                if (!AuthoringSnapshotUtility.ApplyProperties(component, componentSnapshot.properties, createdObjects,
                        s_Data.changeSet.diagnostics))
                    throw new InvalidOperationException("Could not restore component " + componentSnapshot.typeName);
            }

            foreach (var child in node.children)
                ApplyCreatedNode(child, createdObjects);
        }

        static void ApplyUpdate(AuthoringObjectSnapshot snapshot,
            IDictionary<string, UnityEngine.Object> createdObjects)
        {
            var target = AuthoringSnapshotUtility.ResolveGlobalId(snapshot.targetGlobalObjectId);
            if (snapshot.isGameObject)
            {
                var gameObject = (GameObject)target;
                var state = snapshot.gameObjectState;
                EditorUndo.RecordObject(gameObject, k_UndoLabel);
                gameObject.name = state.name;
                gameObject.layer = state.layer;
                gameObject.tag = state.tag;
                gameObject.SetActive(state.activeSelf);
                GameObjectUtility.SetStaticEditorFlags(gameObject, (StaticEditorFlags)state.staticFlags);
                var parent = AuthoringSnapshotUtility.ResolveReference(state.parent, createdObjects) as GameObject;
                EditorUndo.SetTransformParent(gameObject.transform, parent ? parent.transform : null, k_UndoLabel);
                gameObject.transform.SetSiblingIndex(state.siblingIndex);
                return;
            }

            EditorUndo.RecordObject(target, k_UndoLabel);
            if (!AuthoringSnapshotUtility.ApplyProperties(target, snapshot.properties, createdObjects,
                    s_Data.changeSet.diagnostics))
                throw new InvalidOperationException("Could not restore properties on " + target.name);
        }

        static void OnCompilationStarted(object context)
        {
            if (sessionActive)
            {
                AddDiagnostic(true, "Scripts compiled during the authoring session.");
                CaptureChangeSet();
                WriteRecovery();
            }
        }

        static string ComputeScriptFingerprint()
        {
            var builder = new StringBuilder();
            foreach (var path in AssetDatabase.GetAllAssetPaths().Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                builder.Append(path).Append(':').Append(AssetDatabase.GetAssetDependencyHash(path)).Append(';');
            return Hash128.Compute(builder.ToString()).ToString();
        }

        static void RehydrateRecordingState()
        {
            ResetTracking();
            RebuildInstanceMap();
            foreach (var update in s_Data.changeSet.updates)
            {
                if (update.isGameObject)
                    s_TrackedGameObjects.Add(update.targetGlobalObjectId);
                else
                    s_TrackedProperties[update.targetGlobalObjectId] = new HashSet<string>(
                        update.properties.Select(property => property.propertyPath));
            }
            foreach (var deletion in s_Data.changeSet.deletions)
                s_Deletions.Add(deletion);
            s_Diagnostics.AddRange(s_Data.changeSet.diagnostics);
        }

        static void ResetTracking()
        {
            s_ExistingInstanceIds.Clear();
            s_TrackedProperties.Clear();
            s_TrackedGameObjects.Clear();
            s_CreatedRoots.Clear();
            s_CreatedRootTokens.Clear();
            s_HandledDestroyedInstances.Clear();
            s_Deletions.Clear();
            s_Diagnostics.Clear();
            s_ScopeDepth = 0;
            s_GraceUpdates = 0;
            s_SnapshotDirty = false;
        }

        static void AddDiagnostic(bool blocking, string message)
        {
            if (s_Diagnostics.Any(diagnostic => diagnostic.blocking == blocking && diagnostic.message == message))
                return;
            s_Diagnostics.Add(new AuthoringDiagnostic(blocking, message));
            s_SnapshotDirty = true;
        }

        static void CaptureDiagnosticsOnly()
        {
            if (s_Data != null)
                s_Data.changeSet.diagnostics = new List<AuthoringDiagnostic>(s_Diagnostics);
        }

        static AuthoringRecoveryState GetState()
        {
            AuthoringRecoveryState state;
            return s_Data != null && Enum.TryParse(s_Data.state, out state) ? state : AuthoringRecoveryState.Starting;
        }

        static AuthoringExitAction GetExitAction()
        {
            AuthoringExitAction action;
            return s_Data != null && Enum.TryParse(s_Data.requestedExitAction, out action) ? action : AuthoringExitAction.Ask;
        }

        static void WriteRecovery()
        {
            if (s_Data == null)
                return;
            s_Data.updatedUtcTicks = DateTime.UtcNow.Ticks;
            Directory.CreateDirectory(Path.GetDirectoryName(k_RecoveryPath));
            File.WriteAllText(k_RecoveryPath, AuthoringRecoverySerializer.ToJson(s_Data));
        }

        static void LoadRecovery()
        {
            if (!File.Exists(k_RecoveryPath))
                return;
            try
            {
                s_Data = AuthoringRecoverySerializer.FromJson(File.ReadAllText(k_RecoveryPath));
                RebuildBaselineIds();
                if (UpgradeRecoveryData())
                    WriteRecovery();
            }
            catch (Exception exception)
            {
                s_Data = null;
                Debug.LogError("[EditorXR Authoring] Recovery file is invalid: " + exception.Message);
            }
        }

        static void RebuildBaselineIds()
        {
            s_BaselineIds.Clear();
            if (s_Data == null)
                return;

            foreach (var entry in s_Data.baseline)
            {
                if (!string.IsNullOrEmpty(entry.globalObjectId))
                    s_BaselineIds.Add(entry.globalObjectId);
            }
        }

        static bool UpgradeRecoveryData()
        {
            if (s_Data == null || s_Data.version >= AuthoringRecoveryData.CurrentVersion)
                return false;
            if (s_Data.version != 1 && s_Data.version != 2)
                return false;

            if (s_Data.version == 1)
            {
                s_Data.changeSet.deletions.RemoveAll(id => !s_BaselineIds.Contains(id));
                s_Data.changeSet.updates.RemoveAll(update => !s_BaselineIds.Contains(update.targetGlobalObjectId));
                foreach (var update in s_Data.changeSet.updates)
                    update.properties.RemoveAll(property => !AuthoringSnapshotUtility.IsSafePropertyPath(property.propertyPath));
                foreach (var creation in s_Data.changeSet.creations)
                    RemoveUnsafeCreatedProperties(creation.root);

                s_Data.changeSet.diagnostics.RemoveAll(diagnostic =>
                    diagnostic != null && !string.IsNullOrEmpty(diagnostic.message)
                    && (diagnostic.message.StartsWith("A destroyed hierarchy could not be matched", StringComparison.Ordinal)
                        || diagnostic.message.StartsWith("Deleted target can no longer be resolved", StringComparison.Ordinal)
                        || diagnostic.message.StartsWith("Updated target can no longer be resolved", StringComparison.Ordinal)));
            }

            // Package-owned preview scenes (for example NDMF) are transient and cannot be recovered in Edit Mode.
            s_Data.scenePaths.RemoveAll(path =>
                string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
            s_Data.version = AuthoringRecoveryData.CurrentVersion;
            s_Data.scriptFingerprint = ComputeScriptFingerprint();
            return true;
        }

        static void RemoveUnsafeCreatedProperties(AuthoringCreatedNode node)
        {
            if (node == null)
                return;

            foreach (var component in node.components)
                component.properties.RemoveAll(property =>
                    !AuthoringSnapshotUtility.IsSafePropertyPath(property.propertyPath));
            foreach (var child in node.children)
                RemoveUnsafeCreatedProperties(child);
        }

        static void DiscardRecovery()
        {
            s_Data = null;
            s_BaselineIds.Clear();
            ResetTracking();
            if (File.Exists(k_RecoveryPath))
                File.Delete(k_RecoveryPath);
        }

        sealed class AuthoringScope : IDisposable
        {
            bool m_Disposed;

            public void Dispose()
            {
                if (m_Disposed)
                    return;
                m_Disposed = true;
                EndScope();
            }
        }

        sealed class EmptyScope : IDisposable
        {
            internal static readonly EmptyScope instance = new EmptyScope();
            public void Dispose() { }
        }
    }
}
#endif
