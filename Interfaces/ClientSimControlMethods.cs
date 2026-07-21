using System;

namespace Unity.EditorXR.Interfaces
{
    /// <summary>
    /// Optional bridge used to keep EditorXR and VRChat ClientSim from consuming the same controller input.
    /// </summary>
    public static class ClientSimControlMethods
    {
        public static Func<bool> clientSimControlsActive = () => false;
    }
}
