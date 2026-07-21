using System;
using UnityEngine;

namespace Unity.EditorXR.Interfaces
{
    /// <summary>
    /// Optional bridge used to keep EditorXR and VRChat ClientSim from consuming the same controller input.
    /// </summary>
    public static class ClientSimControlMethods
    {
        public static Func<bool> clientSimControlsActive = () => false;

        /// <summary>
        /// Aligns the EditorXR camera rig with ClientSim's local player root when both integrations are ready.
        /// </summary>
        public static Func<Transform, bool> alignViewerToPlayer = playerRoot => false;
    }
}
