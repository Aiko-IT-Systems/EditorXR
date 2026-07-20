using UnityEngine;
using UnityEngine.XR;

namespace Unity.EditorXR.ClientSim
{
    static class ClientSimMetaXRInput
    {
        const float k_ButtonThreshold = 0.75f;

        public static bool TryGetFrame(out ClientSimXRFrame frame)
        {
            frame = default(ClientSimXRFrame);
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            frame.head = ReadPose(head);
            frame.leftHand = ReadPose(left);
            frame.rightHand = ReadPose(right);
            frame.leftController = ReadController(left);
            frame.rightController = ReadController(right);
            return frame.head.valid || frame.leftController.valid || frame.rightController.valid;
        }

        static ClientSimTrackedPose ReadPose(InputDevice device)
        {
            var pose = default(ClientSimTrackedPose);
            bool tracked;
            Vector3 position;
            Quaternion rotation;
            if (!device.isValid || (device.TryGetFeatureValue(CommonUsages.isTracked, out tracked) && !tracked)
                || !device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                || !device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation))
                return pose;

            pose.valid = true;
            pose.localPosition = position;
            pose.localRotation = rotation;
            return pose;
        }

        static ClientSimControllerState ReadController(InputDevice device)
        {
            var state = default(ClientSimControllerState);
            bool tracked;
            if (!device.isValid || (device.TryGetFeatureValue(CommonUsages.isTracked, out tracked) && !tracked))
                return state;

            state.valid = true;
            state.use = ReadButton(device, CommonUsages.triggerButton, CommonUsages.trigger);
            state.grab = ReadButton(device, CommonUsages.gripButton, CommonUsages.grip);
            state.drop = ReadButton(device, CommonUsages.secondaryButton);
            state.jump = ReadButton(device, CommonUsages.primaryButton);
            state.menu = ReadButton(device, CommonUsages.menuButton);
            state.run = ReadButton(device, CommonUsages.primary2DAxisClick);
            return state;
        }

        static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage)
        {
            bool value;
            return device.TryGetFeatureValue(usage, out value) && value;
        }

        static bool ReadButton(InputDevice device, InputFeatureUsage<bool> button, InputFeatureUsage<float> analog)
        {
            float value;
            return ReadButton(device, button)
                || (device.TryGetFeatureValue(analog, out value) && value >= k_ButtonThreshold);
        }
    }
}
