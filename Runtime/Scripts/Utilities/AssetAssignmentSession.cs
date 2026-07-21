using Unity.EditorXR.Core;
using UnityEngine;

namespace Unity.EditorXR.Utilities
{
    static class AssetAssignmentSession
    {
        static Material s_Material;

        internal static bool active { get { return s_Material; } }
        internal static Material material { get { return s_Material; } }

        internal static bool Begin(Material material)
        {
            if (!material)
                return false;

            s_Material = material;
            Debug.LogFormat("[EditorXR] Material assignment active: {0}. Aim and press trigger; press either Authoring face button to finish.",
                material.name);
            return true;
        }

        internal static bool Apply(GameObject target)
        {
            if (!active || !target)
                return false;

            using (AuthoringSessionMethods.beginScope("Assign Project Material"))
                return AssetDropUtils.AssignMaterial(target, s_Material) != null;
        }

        internal static bool Cancel()
        {
            if (!active)
                return false;

            s_Material = null;
            Debug.Log("[EditorXR] Material assignment finished.");
            return true;
        }
    }
}
