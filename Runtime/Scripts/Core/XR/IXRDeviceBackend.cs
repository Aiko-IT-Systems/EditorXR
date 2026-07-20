using System;
using UnityEngine;
using UnityEngine.XR;

namespace Unity.EditorXR.Core.XR
{
    enum XRControllerHand
    {
        Left,
        Right
    }

    interface IXRDeviceBackend : IDisposable
    {
        bool hmdConnected { get; }
        bool userPresent { get; }
        bool isFloorTrackingOrigin { get; }

        void RefreshDevices();
        bool IsControllerConnected(XRControllerHand hand, string deviceFamily);
        bool TryGetHeadPose(out Vector3 position, out Quaternion rotation);
        bool TryGetControllerPose(XRControllerHand hand, out Vector3 position, out Quaternion rotation);
        bool TryGetAxis(XRControllerHand hand, InputFeatureUsage<float> usage, out float value);
        bool TryGetAxis(XRControllerHand hand, InputFeatureUsage<Vector2> usage, out Vector2 value);
        bool TryGetButton(XRControllerHand hand, InputFeatureUsage<bool> usage, out bool value);
        bool SendHapticImpulse(XRControllerHand hand, float amplitude, float duration);
        void StopHaptics(XRControllerHand hand);
    }
}
