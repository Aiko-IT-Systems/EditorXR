using System;
using System.Collections.Generic;
using UnityEngine.XR;

namespace Unity.EditorXR
{
    enum DeviceType
    {
        Oculus,
        Vive
    }

    /// <summary>
    /// In cases where you must have different input logic (e.g. button press + axis input) you can get the device type
    /// </summary>
    interface IUsesDeviceType
    {
    }

    static class UsesDeviceTypeMethods
    {
        static readonly List<InputDevice> k_Devices = new List<InputDevice>();

        /// <summary>
        /// Returns the type of device currently in use
        /// </summary>
        /// <returns>The device type</returns>
        public static DeviceType GetDeviceType(this IUsesDeviceType @this)
        {
            return GetDeviceType();
        }

        internal static DeviceType GetDeviceType()
        {
            k_Devices.Clear();
            InputDevices.GetDevices(k_Devices);
            foreach (var device in k_Devices)
            {
                var name = device.name;
                if (!string.IsNullOrEmpty(name)
                    && (name.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("meta", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) >= 0))
                    return DeviceType.Oculus;
            }

            return DeviceType.Vive;
        }
    }
}
