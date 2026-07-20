using System;
using UnityEngine;

namespace Unity.EditorXR.ClientSim
{
    public enum ClientSimControlMode { Authoring, ClientSim }
    public enum ClientSimCompatibilityStatus { Unavailable, WaitingForClientSim, Compatible, Incompatible }

    [Serializable]
    public struct ClientSimTrackedPose
    {
        public bool valid;
        public Vector3 localPosition;
        public Quaternion localRotation;
    }

    [Serializable]
    public struct ClientSimControllerState
    {
        public bool valid;
        public bool use;
        public bool grab;
        public bool drop;
        public bool jump;
        public bool menu;
        public bool run;
    }

    [Serializable]
    public struct ClientSimXRFrame
    {
        public ClientSimTrackedPose head;
        public ClientSimTrackedPose leftHand;
        public ClientSimTrackedPose rightHand;
        public ClientSimControllerState leftController;
        public ClientSimControllerState rightController;
    }
}
