# Managed VR Authoring Session

This Unity 2022 editor-only service enters Play Mode, captures explicitly scoped EditorXR edits, and offers to apply or discard them after Play Mode exits. Recovery data is stored in `Library/EditorXR/AuthoringRecovery.json` and is never included in a world build.

Start a session with `Window > EditorXR > Authoring > Start VR Authoring Session`. Exit through `Apply Changes and Exit` or `Discard Changes and Exit`. Stopping Play Mode normally opens the same choice after Unity restores Edit Mode.

When ClientSim is installed, VR controls initially target ClientSim so its startup acknowledgement is usable in-headset. Press `F8` or use `Window > EditorXR > ClientSim > Toggle VR Control Mode` to switch between ClientSim and EditorXR authoring controls.

## Tool integration

The stock transform, primitive creation, prefab/model placement, project-asset assignment, duplicate, paste, and delete workflows are instrumented. New or custom tools must keep an authoring scope open for their complete Undo-backed interaction:

```csharp
using (EditorXRAuthoring.BeginScope("Move Selection"))
{
    EditorXRAuthoring.RecordObject(targetTransform);
    targetTransform.position = position;
}
```

Use `RegisterCreatedHierarchy`, `DestroyHierarchy`, and `SetTransformParent` when a tool owns those operations. Global `Undo.postprocessModifications` and `ObjectChangeEvents` capture only while a scope is active, plus a two-update grace period for Unity's end-of-frame object event publication.

Runtime-assembly tools can call the equivalent no-op-safe delegates in `Unity.EditorXR.Interfaces.AuthoringSessionMethods`. The editor service installs those delegates after domain load; player builds retain the harmless defaults.

## Supported first milestone

- Serialized Inspector properties supported by `AuthoringPropertySnapshot`
- Transform edits and reparenting
- GameObject name, active state, layer, tag, static flags, and sibling order
- Creation, duplication, and deletion of GameObject hierarchies
- Prefab placement while retaining the prefab instance connection
- One Undo group when applying changes in Edit Mode

## Fail-closed operations

- Adding or removing components on an existing GameObject
- Editing prefab assets or project assets
- Unsaved, unloaded, transient, `DontSave`, and ClientSim-owned objects
- Unsupported serialized property kinds such as managed references and animation curves
- References to runtime-only objects
- Script compilation or script fingerprint changes during a session
- Missing targets, changed component layouts, unresolved references, or unclassified scene changes

Any blocking diagnostic prevents the complete change set from applying. No partial replay is retained, and the recovery file remains available for inspection or a later retry.
