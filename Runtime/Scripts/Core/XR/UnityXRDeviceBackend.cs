using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Unity.EditorXR.Core.XR
{
    sealed class UnityXRDeviceBackend : IXRDeviceBackend
    {
        static readonly InputDeviceCharacteristics k_HmdCharacteristics = InputDeviceCharacteristics.HeadMounted;
        static readonly InputDeviceCharacteristics k_LeftControllerCharacteristics =
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Left;
        static readonly InputDeviceCharacteristics k_RightControllerCharacteristics =
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Right;

        readonly List<InputDevice> m_Devices = new List<InputDevice>();

        InputDevice m_Hmd;
        InputDevice m_LeftController;
        InputDevice m_RightController;
        XRInputSubsystem m_InputSubsystem;

        public bool hmdConnected { get { return m_Hmd.isValid; } }

        public bool userPresent
        {
            get
            {
                bool present;
                if (m_Hmd.isValid && m_Hmd.TryGetFeatureValue(CommonUsages.userPresence, out present))
                    return present;

                bool tracked;
                return m_Hmd.isValid && (!m_Hmd.TryGetFeatureValue(CommonUsages.isTracked, out tracked) || tracked);
            }
        }

        public bool isFloorTrackingOrigin
        {
            get
            {
                if (m_InputSubsystem == null)
                    return false;

                var mode = m_InputSubsystem.GetTrackingOriginMode();
                return (mode & TrackingOriginModeFlags.Floor) != 0;
            }
        }

        public UnityXRDeviceBackend()
        {
            InputDevices.deviceConnected += OnDeviceChanged;
            InputDevices.deviceDisconnected += OnDeviceChanged;
            InputDevices.deviceConfigChanged += OnDeviceChanged;
            RefreshDevices();
        }

        public void Dispose()
        {
            InputDevices.deviceConnected -= OnDeviceChanged;
            InputDevices.deviceDisconnected -= OnDeviceChanged;
            InputDevices.deviceConfigChanged -= OnDeviceChanged;
            StopHaptics(XRControllerHand.Left);
            StopHaptics(XRControllerHand.Right);
        }

        public void RefreshDevices()
        {
            if (!m_Hmd.isValid)
                m_Hmd = FindDevice(k_HmdCharacteristics, XRNode.Head);

            if (!m_LeftController.isValid)
                m_LeftController = FindDevice(k_LeftControllerCharacteristics, XRNode.LeftHand);

            if (!m_RightController.isValid)
                m_RightController = FindDevice(k_RightControllerCharacteristics, XRNode.RightHand);

            if (m_InputSubsystem == null || !m_InputSubsystem.running)
            {
                var inputSubsystems = new List<XRInputSubsystem>();
                SubsystemManager.GetInstances(inputSubsystems);
                m_InputSubsystem = inputSubsystems.Find(subsystem => subsystem != null && subsystem.running);
            }
        }

        public bool IsControllerConnected(XRControllerHand hand, string deviceFamily)
        {
            var device = GetController(hand);
            return device.isValid && MatchesDeviceFamily(device, deviceFamily);
        }

        public bool TryGetHeadPose(out Vector3 position, out Quaternion rotation)
        {
            return TryGetPose(m_Hmd, out position, out rotation);
        }

        public bool TryGetControllerPose(XRControllerHand hand, out Vector3 position, out Quaternion rotation)
        {
            return TryGetPose(GetController(hand), out position, out rotation);
        }

        public bool TryGetAxis(XRControllerHand hand, InputFeatureUsage<float> usage, out float value)
        {
            return GetController(hand).TryGetFeatureValue(usage, out value);
        }

        public bool TryGetAxis(XRControllerHand hand, InputFeatureUsage<Vector2> usage, out Vector2 value)
        {
            return GetController(hand).TryGetFeatureValue(usage, out value);
        }

        public bool TryGetButton(XRControllerHand hand, InputFeatureUsage<bool> usage, out bool value)
        {
            return GetController(hand).TryGetFeatureValue(usage, out value);
        }

        public bool SendHapticImpulse(XRControllerHand hand, float amplitude, float duration)
        {
            var device = GetController(hand);
            HapticCapabilities capabilities;
            if (!device.isValid || !device.TryGetHapticCapabilities(out capabilities) || !capabilities.supportsImpulse)
                return false;

            return device.SendHapticImpulse(0, Mathf.Clamp01(amplitude), Mathf.Max(0f, duration));
        }

        public void StopHaptics(XRControllerHand hand)
        {
            var device = GetController(hand);
            if (device.isValid)
                device.StopHaptics();
        }

        void OnDeviceChanged(InputDevice device)
        {
            if (device == m_Hmd)
                m_Hmd = default(InputDevice);
            if (device == m_LeftController)
                m_LeftController = default(InputDevice);
            if (device == m_RightController)
                m_RightController = default(InputDevice);

            RefreshDevices();
        }

        InputDevice FindDevice(InputDeviceCharacteristics characteristics, XRNode fallbackNode)
        {
            m_Devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(characteristics, m_Devices);
            foreach (var device in m_Devices)
            {
                if (device.isValid)
                    return device;
            }

            return InputDevices.GetDeviceAtXRNode(fallbackNode);
        }

        InputDevice GetController(XRControllerHand hand)
        {
            return hand == XRControllerHand.Left ? m_LeftController : m_RightController;
        }

        static bool TryGetPose(InputDevice device, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!device.isValid)
                return false;

            var hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
            var hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            return hasPosition && hasRotation;
        }

        static bool MatchesDeviceFamily(InputDevice device, string deviceFamily)
        {
            if (string.IsNullOrEmpty(deviceFamily))
                return true;

            var identity = string.Concat(device.name, " ", device.manufacturer).ToLowerInvariant();
            if (deviceFamily.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0)
                return identity.Contains("oculus") || identity.Contains("meta") || identity.Contains("quest") || identity.Contains("touch");

            if (deviceFamily.IndexOf("openvr", StringComparison.OrdinalIgnoreCase) >= 0)
                return identity.Contains("openvr") || identity.Contains("vive") || identity.Contains("index");

            return identity.IndexOf(deviceFamily, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    static class EditorXRXRDevices
    {
        static IXRDeviceBackend s_Backend;

        internal static IXRDeviceBackend backend
        {
            get
            {
                if (s_Backend == null)
                    s_Backend = new UnityXRDeviceBackend();

                return s_Backend;
            }
        }

        internal static void Shutdown()
        {
            if (s_Backend == null)
                return;

            s_Backend.Dispose();
            s_Backend = null;
        }
    }
}
