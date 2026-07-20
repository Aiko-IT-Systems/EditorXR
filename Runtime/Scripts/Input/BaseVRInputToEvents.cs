using System;
using Unity.EditorXR.Core.XR;
using UnityEngine;
using UnityEngine.InputNew;
using UnityEngine.XR;

namespace Unity.EditorXR.Input
{
    abstract class BaseVRInputToEvents : BaseInputToEvents
    {
        protected virtual string DeviceName
        {
            get { return "Unknown VR Device"; }
        }

        const uint k_ControllerCount = 2;
        const int k_AxisCount = (int)VRInputDevice.VRControl.Analog9 + 1;
        const float k_DeadZone = 0.05f;

        float[,] m_LastAxisValues = new float[k_ControllerCount, k_AxisCount];
        bool[,] m_LastButtonValues = new bool[k_ControllerCount, k_AxisCount];
        Vector3[] m_LastPositionValues = new Vector3[k_ControllerCount];
        Quaternion[] m_LastRotationValues = new Quaternion[k_ControllerCount];
        bool[] m_HasLastPose = new bool[k_ControllerCount];
        static readonly VRInputDevice.VRControl[] k_Buttons =
        {
            VRInputDevice.VRControl.Action1,
            VRInputDevice.VRControl.Action2,
            VRInputDevice.VRControl.LeftStickButton
        };

		public void Update()
        {
            var backend = EditorXRXRDevices.backend;
            backend.RefreshDevices();
            var deviceActive = backend.IsControllerConnected(XRControllerHand.Left, DeviceName)
                && backend.IsControllerConnected(XRControllerHand.Right, DeviceName);

            if (!deviceActive)
            {
                if (active)
                    ReleaseControls();

                active = false;
                return;
            }

            active = true;

            for (VRInputDevice.Handedness hand = VRInputDevice.Handedness.Left;
                (int)hand <= (int)VRInputDevice.Handedness.Right;
                hand++)
            {
                int deviceIndex = hand == VRInputDevice.Handedness.Left ? 3 : 4;

                // TODO change 3 and 4 based on virtual devices defined in InputDeviceManager (using actual hardware available)
                SendButtonEvents(hand, deviceIndex);
                SendAxisEvents(hand, deviceIndex);
                SendTrackingEvents(hand, deviceIndex);
            }
        }

        bool GetAxis(VRInputDevice.Handedness hand, VRInputDevice.VRControl axis, out float value)
        {
            var backendHand = ToBackendHand(hand);
            switch (axis)
            {
                case VRInputDevice.VRControl.Trigger1:
                    return EditorXRXRDevices.backend.TryGetAxis(backendHand, CommonUsages.trigger, out value);
                case VRInputDevice.VRControl.Trigger2:
                    return EditorXRXRDevices.backend.TryGetAxis(backendHand, CommonUsages.grip, out value);
                case VRInputDevice.VRControl.LeftStickX:
                case VRInputDevice.VRControl.LeftStickY:
                    Vector2 axisValue;
                    if (!EditorXRXRDevices.backend.TryGetAxis(backendHand, CommonUsages.primary2DAxis, out axisValue))
                        break;

                    value = axis == VRInputDevice.VRControl.LeftStickX ? axisValue.x : -axisValue.y;
                    return true;
            }

            value = 0f;
            return false;
        }

        void SendAxisEvents(VRInputDevice.Handedness hand, int deviceIndex)
        {
            for (var axis = 0; axis < k_AxisCount; ++axis)
            {
                float value;
                if (GetAxis(hand, (VRInputDevice.VRControl)axis, out value))
                {
                    if (Mathf.Abs(value) < k_DeadZone)
                        value = 0f;

                    if (Mathf.Approximately(m_LastAxisValues[(int)hand, axis], value))
                        continue;

                    var inputEvent = InputSystem.CreateEvent<GenericControlEvent>();
                    inputEvent.deviceType = typeof(VRInputDevice);
                    inputEvent.deviceIndex = deviceIndex;
                    inputEvent.controlIndex = axis;
                    inputEvent.value = value;

                    m_LastAxisValues[(int)hand, axis] = inputEvent.value;

                    InputSystem.QueueEvent(inputEvent);
                }
            }
        }

        protected virtual string GetButtonAxis(VRInputDevice.Handedness hand, VRInputDevice.VRControl button)
        {
            switch (button)
            {
                case VRInputDevice.VRControl.Action1:
                    if (hand == VRInputDevice.Handedness.Left)
                        return "XRI_Left_PrimaryButton";
                    else
                        return "XRI_Right_PrimaryButton";

                case VRInputDevice.VRControl.Action2:
                    if (hand == VRInputDevice.Handedness.Left)
                        return "XRI_Left_SecondaryButton";
                    else
                        return "XRI_Right_SecondaryButton";

                case VRInputDevice.VRControl.LeftStickButton:
                    if (hand == VRInputDevice.Handedness.Left)
                        return "XRI_Left_Primary2DAxisClick";
                    else
                        return "XRI_Right_Primary2DAxisClick";
            }

            // Not all buttons are currently mapped
            return null;
        }

        void SendButtonEvents(VRInputDevice.Handedness hand, int deviceIndex)
        {
            foreach (VRInputDevice.VRControl button in k_Buttons)
            {
                var axis = GetButtonAxis(hand, button);
                InputFeatureUsage<bool> usage;
                if (!TryGetButtonUsage(axis, out usage))
                    continue;

                bool pressed;
                if (!EditorXRXRDevices.backend.TryGetButton(ToBackendHand(hand), usage, out pressed))
                    continue;

                if (pressed != m_LastButtonValues[(int)hand, (int)button])
                {
                    var inputEvent = InputSystem.CreateEvent<GenericControlEvent>();
                    inputEvent.deviceType = typeof(VRInputDevice);
                    inputEvent.deviceIndex = deviceIndex;
                    inputEvent.controlIndex = (int)button;
                    inputEvent.value = pressed ? 1.0f : 0.0f;

                    m_LastButtonValues[(int)hand, (int)button] = pressed;
                    InputSystem.QueueEvent(inputEvent);
                }
            }
        }

        void SendTrackingEvents(VRInputDevice.Handedness hand, int deviceIndex)
        {
            Vector3 localPosition;
            Quaternion localRotation;
            if (!EditorXRXRDevices.backend.TryGetControllerPose(ToBackendHand(hand), out localPosition, out localRotation))
                return;

            if (m_HasLastPose[(int)hand] && localPosition == m_LastPositionValues[(int)hand]
                && localRotation == m_LastRotationValues[(int)hand])
                return;

            var inputEvent = InputSystem.CreateEvent<VREvent>();
            inputEvent.deviceType = typeof(VRInputDevice);
            inputEvent.deviceIndex = deviceIndex;
            inputEvent.localPosition = localPosition;
            inputEvent.localRotation = localRotation;

            m_LastPositionValues[(int)hand] = inputEvent.localPosition;
            m_LastRotationValues[(int)hand] = inputEvent.localRotation;
            m_HasLastPose[(int)hand] = true;

            InputSystem.QueueEvent(inputEvent);
        }

        void ReleaseControls()
        {
            for (var hand = VRInputDevice.Handedness.Left;
                (int)hand <= (int)VRInputDevice.Handedness.Right;
                hand++)
            {
                var deviceIndex = hand == VRInputDevice.Handedness.Left ? 3 : 4;
                foreach (var button in k_Buttons)
                {
                    if (!m_LastButtonValues[(int)hand, (int)button])
                        continue;

                    var inputEvent = InputSystem.CreateEvent<GenericControlEvent>();
                    inputEvent.deviceType = typeof(VRInputDevice);
                    inputEvent.deviceIndex = deviceIndex;
                    inputEvent.controlIndex = (int)button;
                    inputEvent.value = 0f;
                    InputSystem.QueueEvent(inputEvent);
                    m_LastButtonValues[(int)hand, (int)button] = false;
                }

                for (var axis = 0; axis < k_AxisCount; ++axis)
                {
                    if (!Mathf.Approximately(m_LastAxisValues[(int)hand, axis], 0f))
                    {
                        var inputEvent = InputSystem.CreateEvent<GenericControlEvent>();
                        inputEvent.deviceType = typeof(VRInputDevice);
                        inputEvent.deviceIndex = deviceIndex;
                        inputEvent.controlIndex = axis;
                        inputEvent.value = 0f;
                        InputSystem.QueueEvent(inputEvent);
                    }

                    m_LastAxisValues[(int)hand, axis] = 0f;
                }

                m_HasLastPose[(int)hand] = false;
            }
        }

        static XRControllerHand ToBackendHand(VRInputDevice.Handedness hand)
        {
            return hand == VRInputDevice.Handedness.Left ? XRControllerHand.Left : XRControllerHand.Right;
        }

        static bool TryGetButtonUsage(string axis, out InputFeatureUsage<bool> usage)
        {
            if (!string.IsNullOrEmpty(axis) && axis.IndexOf("SecondaryButton", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                usage = CommonUsages.secondaryButton;
                return true;
            }

            if (!string.IsNullOrEmpty(axis) && axis.IndexOf("PrimaryButton", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                usage = CommonUsages.primaryButton;
                return true;
            }

            if (!string.IsNullOrEmpty(axis) && axis.IndexOf("Primary2DAxisClick", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                usage = CommonUsages.primary2DAxisClick;
                return true;
            }

            usage = default(InputFeatureUsage<bool>);
            return false;
        }
    }
}
