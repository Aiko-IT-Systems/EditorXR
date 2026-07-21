using System;
using System.Linq;
using System.Reflection;
#if UNITY_EDITOR
using UnityEditor.PackageManager;
#endif
using UnityEngine;
using UnityEngine.EventSystems;

namespace Unity.EditorXR.ClientSim
{
    sealed class ClientSimReflectionAdapter
    {
        public const string ExpectedVersion = "3.10.4";
        const BindingFlags k_All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        readonly FieldInfo m_MainInstance, m_MainInputManager, m_Input, m_Head, m_LeftHand, m_RightHand,
            m_MouseReleased, m_PlayerXRotationBase, m_PlayerYRotationBase;
        readonly Type m_TrackingProviderType;
        readonly MethodInfo m_HasInstance, m_SendJump, m_SendUse, m_SendGrab, m_SendDrop, m_SendMenu, m_SendRun,
            m_SendInputMethod;
        readonly object m_LeftHandValue, m_RightHandValue, m_OculusInputMethod;
        object m_InputObject, m_TrackingProvider;
        Transform m_HeadTransform, m_LeftHandTransform, m_RightHandTransform, m_PlayerXRotationTransform,
            m_PlayerYRotationTransform, m_PlayerRoot;
        bool m_PreviousMouseReleased;

        public bool isBound { get { return m_InputObject != null && m_TrackingProvider != null && m_HeadTransform != null; } }
        public Transform playerRoot { get { return m_PlayerRoot; } }

        ClientSimReflectionAdapter(Assembly assembly)
        {
            var main = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimMain");
            var manager = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimInputManager");
            var inputBase = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimInputBase");
            var inputAction = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimInputActionBased");
            m_TrackingProviderType = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimTrackingProviderBase");
            var desktopTrackingProvider = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            var inputModule = RequireType(assembly, "VRC.SDK3.ClientSim.ClientSimInputModule");
            var handType = RequireType("VRC.Udon.Common.HandType");
            var inputMethodType = RequireType("VRC.SDKBase.VRCInputMethod");
            if (!typeof(BaseInputModule).IsAssignableFrom(inputModule))
                throw new MissingMemberException(inputModule.FullName, "BaseInputModule inheritance");

            RequireMethod(inputModule, "Process", typeof(void), Type.EmptyTypes);
            m_HasInstance = RequireMethod(main, "HasInstance", typeof(bool), Type.EmptyTypes);
            m_MainInstance = RequireField(main, "_instance", main, true);
            m_MainInputManager = RequireField(main, "inputManager", manager, false);
            m_Input = RequireField(manager, "_input", inputAction, false);
            m_Head = RequireField(m_TrackingProviderType, "head", typeof(Transform), false);
            m_LeftHand = RequireField(m_TrackingProviderType, "leftHand", typeof(Transform), false);
            m_RightHand = RequireField(m_TrackingProviderType, "rightHand", typeof(Transform), false);
            m_MouseReleased = RequireField(desktopTrackingProvider, "_mouseReleased", typeof(bool), false);
            m_PlayerXRotationBase = RequireField(desktopTrackingProvider, "playerXRotationBase", typeof(Transform), false);
            m_PlayerYRotationBase = RequireField(desktopTrackingProvider, "playerYRotationBase", typeof(Transform), false);
            var handArgs = new[] { typeof(bool), handType };
            m_SendJump = RequireMethod(inputBase, "SendJumpEvent", typeof(void), handArgs);
            m_SendUse = RequireMethod(inputBase, "SendUseEvent", typeof(void), handArgs);
            m_SendGrab = RequireMethod(inputBase, "SendGrabEvent", typeof(void), handArgs);
            m_SendDrop = RequireMethod(inputBase, "SendDropEvent", typeof(void), handArgs);
            m_SendMenu = RequireMethod(inputBase, "SendToggleMenuEvent", typeof(void), handArgs);
            m_SendRun = RequireMethod(inputBase, "SendRunEvent", typeof(void), new[] { typeof(bool) });
            m_SendInputMethod = RequireMethod(inputBase, "SendInputMethodChangedEvent", typeof(void),
                new[] { inputMethodType });
            m_LeftHandValue = Enum.Parse(handType, "LEFT");
            m_RightHandValue = Enum.Parse(handType, "RIGHT");
            m_OculusInputMethod = Enum.Parse(inputMethodType, "Oculus");
        }

        public static bool IsVRChatWorldsInstalled(out string version)
        {
            version = null;
#if UNITY_EDITOR
            try
            {
                var package = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(info => info.name == "com.vrchat.worlds");
                if (package == null)
                    return false;
                version = package.version;
                return true;
            }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }

        public static bool TryCreate(out ClientSimReflectionAdapter adapter, out ClientSimCompatibilityStatus status,
            out string diagnostic)
        {
            adapter = null;
            string version;
            if (!IsVRChatWorldsInstalled(out version))
            {
                status = ClientSimCompatibilityStatus.Unavailable;
                diagnostic = "VRChat Worlds is not installed; ClientSim integration is inactive.";
                return false;
            }
            if (version != ExpectedVersion)
            {
                status = ClientSimCompatibilityStatus.Incompatible;
                diagnostic = "Expected VRChat Worlds/ClientSim 3.10.4 exactly, but found " + version
                    + ". Integration is disabled to avoid an unknown API.";
                return false;
            }
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(item => item.GetName().Name == "VRC.ClientSim");
                if (assembly == null)
                    throw new TypeLoadException("Assembly VRC.ClientSim is not loaded.");
                adapter = new ClientSimReflectionAdapter(assembly);
                status = ClientSimCompatibilityStatus.WaitingForClientSim;
                diagnostic = "ClientSim 3.10.4 contract verified; waiting for its local player.";
                return true;
            }
            catch (Exception exception)
            {
                status = ClientSimCompatibilityStatus.Incompatible;
                diagnostic = "ClientSim 3.10.4 contract mismatch: " + exception.Message + " Integration is disabled.";
                return false;
            }
        }

        public bool TryBind()
        {
            if (!(bool)m_HasInstance.Invoke(null, null))
                return false;
            var main = m_MainInstance.GetValue(null);
            var manager = main == null ? null : m_MainInputManager.GetValue(main);
            m_InputObject = manager == null ? null : m_Input.GetValue(manager);
            if (m_InputObject == null)
                return false;
            var providers = Resources.FindObjectsOfTypeAll(m_TrackingProviderType).OfType<Component>()
                .Where(item => item.gameObject.scene.IsValid() && item.gameObject.activeInHierarchy).ToArray();
            if (providers.Length != 1)
            {
                m_InputObject = null;
                return false;
            }
            m_TrackingProvider = providers[0];
            m_PlayerRoot = providers[0].transform.root;
            m_HeadTransform = (Transform)m_Head.GetValue(m_TrackingProvider);
            m_LeftHandTransform = (Transform)m_LeftHand.GetValue(m_TrackingProvider);
            m_RightHandTransform = (Transform)m_RightHand.GetValue(m_TrackingProvider);
            if (m_HeadTransform == null || m_LeftHandTransform == null || m_RightHandTransform == null)
            {
                m_InputObject = null;
                return false;
            }
            m_PreviousMouseReleased = (bool)m_MouseReleased.GetValue(m_TrackingProvider);
            m_MouseReleased.SetValue(m_TrackingProvider, true);
            m_PlayerXRotationTransform = (Transform)m_PlayerXRotationBase.GetValue(m_TrackingProvider);
            m_PlayerYRotationTransform = (Transform)m_PlayerYRotationBase.GetValue(m_TrackingProvider);
            ResetDesktopRotation();
            m_SendInputMethod.Invoke(m_InputObject, new[] { m_OculusInputMethod });
            return true;
        }

        public void ApplyTracking(ClientSimXRFrame frame)
        {
            m_MouseReleased.SetValue(m_TrackingProvider, true);
            ResetDesktopRotation();
            ApplyPose(m_HeadTransform, frame.head);
            ApplyPose(m_LeftHandTransform, frame.leftHand);
            ApplyPose(m_RightHandTransform, frame.rightHand);
        }

        public void ReleaseTrackingOverride()
        {
            if (m_TrackingProvider != null)
                m_MouseReleased.SetValue(m_TrackingProvider, m_PreviousMouseReleased);

            m_TrackingProvider = null;
            m_InputObject = null;
            m_HeadTransform = null;
            m_LeftHandTransform = null;
            m_RightHandTransform = null;
            m_PlayerXRotationTransform = null;
            m_PlayerYRotationTransform = null;
            m_PlayerRoot = null;
        }

        void ResetDesktopRotation()
        {
            if (m_PlayerXRotationTransform != null)
                m_PlayerXRotationTransform.localRotation = Quaternion.identity;
            if (m_PlayerYRotationTransform != null)
                m_PlayerYRotationTransform.localRotation = Quaternion.identity;
        }

        public void SendChanges(ClientSimControllerState oldLeft, ClientSimControllerState left,
            ClientSimControllerState oldRight, ClientSimControllerState right)
        {
            SendHand(oldLeft, left, m_LeftHandValue);
            SendHand(oldRight, right, m_RightHandValue);
            if ((oldLeft.run || oldRight.run) != (left.run || right.run))
                m_SendRun.Invoke(m_InputObject, new object[] { left.run || right.run });
        }

        static void ApplyPose(Transform target, ClientSimTrackedPose pose)
        {
            if (!pose.valid) return;
            target.localPosition = pose.localPosition;
            target.localRotation = pose.localRotation;
        }

        void SendHand(ClientSimControllerState oldState, ClientSimControllerState state, object hand)
        {
            Send(m_SendUse, oldState.use, state.use, hand);
            Send(m_SendGrab, oldState.grab, state.grab, hand);
            Send(m_SendDrop, oldState.drop, state.drop, hand);
            Send(m_SendJump, oldState.jump, state.jump, hand);
            Send(m_SendMenu, oldState.menu, state.menu, hand);
        }

        void Send(MethodInfo method, bool oldValue, bool value, object hand)
        {
            if (oldValue != value) method.Invoke(m_InputObject, new[] { (object)value, hand });
        }

        static Type RequireType(Assembly assembly, string name)
        {
            var type = assembly.GetType(name, false);
            if (type == null) throw new TypeLoadException(name);
            return type;
        }

        static Type RequireType(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(name, false))
                .FirstOrDefault(item => item != null);
            if (type == null) throw new TypeLoadException(name);
            return type;
        }

        static FieldInfo RequireField(Type type, string name, Type fieldType, bool isStatic)
        {
            var field = type.GetField(name, k_All);
            if (field == null || field.FieldType != fieldType || field.IsStatic != isStatic)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }

        static MethodInfo RequireMethod(Type type, string name, Type returnType, Type[] args)
        {
            var method = type.GetMethod(name, k_All, null, args, null);
            if (method == null || method.ReturnType != returnType) throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
