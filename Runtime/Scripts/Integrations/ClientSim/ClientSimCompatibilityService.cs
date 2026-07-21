using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Unity.EditorXR.Interfaces;
using UnityEngine;

namespace Unity.EditorXR.ClientSim
{
    [AddComponentMenu("EditorXR/VRChat ClientSim Compatibility")]
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class ClientSimCompatibilityService : MonoBehaviour
    {
        static ClientSimCompatibilityService s_Instance;
        [SerializeField] ClientSimControlMode m_ControlMode = ClientSimControlMode.ClientSim;
        readonly ClientSimCooperativeEventSystem m_EventSystem = new ClientSimCooperativeEventSystem();
        ClientSimReflectionAdapter m_Adapter;
        ClientSimControlMode m_ActiveMode;
        ClientSimCompatibilityStatus m_Status;
        ClientSimControllerState m_PreviousLeft, m_PreviousRight;
        string m_Diagnostic;
        float m_NextBindAttempt;
        bool m_LoggedFailure, m_LoggedReady;

        public static ClientSimCompatibilityService instance { get { return s_Instance; } }
        public ClientSimControlMode controlMode { get { return m_ActiveMode; } }
        public ClientSimCompatibilityStatus status { get { return m_Status; } }
        public string diagnostic { get { return m_Diagnostic; } }
        public bool isOperational { get { return m_Status == ClientSimCompatibilityStatus.Compatible; } }
        public event Action<ClientSimControlMode> controlModeChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            string version;
            if (!Application.isPlaying || !ClientSimReflectionAdapter.IsVRChatWorldsInstalled(out version)
                || FindObjectOfType<ClientSimCompatibilityService>() != null)
                return;
            var go = new GameObject("EditorXR ClientSim Compatibility");
            DontDestroyOnLoad(go);
            go.AddComponent<ClientSimCompatibilityService>();
        }

        public static bool SetControlMode(ClientSimControlMode mode)
        {
            if (s_Instance == null) return false;
            s_Instance.SwitchMode(mode);
            return true;
        }

#if UNITY_EDITOR
        [MenuItem("Window/EditorXR/ClientSim/Toggle VR Control Mode _F8")]
        static void ToggleControlMode()
        {
            if (s_Instance == null)
            {
                Debug.LogWarning("[EditorXR ClientSim] Enter Play Mode before switching VR control mode.");
                return;
            }

            s_Instance.SwitchMode(s_Instance.m_ActiveMode == ClientSimControlMode.Authoring
                ? ClientSimControlMode.ClientSim
                : ClientSimControlMode.Authoring);
            Debug.Log("[EditorXR ClientSim] VR controls now target " + s_Instance.m_ActiveMode + ".", s_Instance);
        }
#endif

        // The caller supplies its already-resolved ray target; this never creates another EventSystem/input module.
        public static bool ProcessCooperativePointer(int pointerId, GameObject target, Vector2 screenPosition, bool pressed)
        {
            return s_Instance != null && s_Instance.m_ActiveMode == ClientSimControlMode.ClientSim
                && s_Instance.m_EventSystem.Process(pointerId, target, screenPosition, pressed);
        }

        public static bool SubmitFrame(ClientSimXRFrame frame)
        {
            return s_Instance != null && s_Instance.m_ActiveMode == ClientSimControlMode.ClientSim
                && s_Instance.ProcessFrame(frame);
        }

        public void Revalidate()
        {
            ReleaseInjectedState();
            if (m_Adapter != null)
                m_Adapter.ReleaseTrackingOverride();
            m_Adapter = null;
            m_LoggedFailure = m_LoggedReady = false;
            ValidateContract();
        }

        void Awake()
        {
            if (s_Instance != null && s_Instance != this) { Destroy(gameObject); return; }
            s_Instance = this;
            m_ActiveMode = m_ControlMode;
            ValidateContract();
        }

        void OnEnable()
        {
            ClientSimControlMethods.clientSimControlsActive = () => s_Instance != null
                && s_Instance.isActiveAndEnabled
                && s_Instance.m_ActiveMode == ClientSimControlMode.ClientSim;
        }

        void Update()
        {
            if (m_ControlMode != m_ActiveMode) SwitchMode(m_ControlMode);
            if (m_Adapter == null || m_Status == ClientSimCompatibilityStatus.Incompatible) return;
            if (!m_Adapter.isBound)
            {
                if (Time.unscaledTime < m_NextBindAttempt) return;
                m_NextBindAttempt = Time.unscaledTime + 1f;
                try
                {
                    if (!m_Adapter.TryBind()) return;
                    m_Status = ClientSimCompatibilityStatus.Compatible;
                    m_Diagnostic = "ClientSim 3.10.4 is bound; safe pose and button bridging is available.";
                    if (!m_LoggedReady) { Debug.Log("[EditorXR ClientSim] " + m_Diagnostic, this); m_LoggedReady = true; }
                }
                catch (Exception exception) { FailClosed("Runtime binding failed: " + exception.Message); return; }
            }

            if (m_ActiveMode == ClientSimControlMode.ClientSim)
                ClientSimControlMethods.alignViewerToPlayer(m_Adapter.playerRoot);

            ClientSimXRFrame frame;
            if (ClientSimMetaXRInput.TryGetFrame(out frame))
                ProcessFrame(frame);
        }

        void OnDisable()
        {
            ReleaseInjectedState();
            if (m_Adapter != null)
                m_Adapter.ReleaseTrackingOverride();
            if (s_Instance == this)
                ClientSimControlMethods.clientSimControlsActive = () => false;
        }

        void OnDestroy()
        {
            if (s_Instance != this)
                return;

            s_Instance = null;
            ClientSimControlMethods.clientSimControlsActive = () => false;
        }

        void ValidateContract()
        {
            ClientSimCompatibilityStatus status;
            string message;
            if (ClientSimReflectionAdapter.TryCreate(out m_Adapter, out status, out message))
            { m_Status = status; m_Diagnostic = message; return; }
            m_Status = status;
            m_Diagnostic = message;
            if (status == ClientSimCompatibilityStatus.Incompatible && !m_LoggedFailure)
            { Debug.LogError("[EditorXR ClientSim] " + message, this); m_LoggedFailure = true; }
        }

        bool ProcessFrame(ClientSimXRFrame frame)
        {
            if (m_Adapter == null || !m_Adapter.isBound || m_Status != ClientSimCompatibilityStatus.Compatible)
                return false;
            try
            {
                m_Adapter.ApplyTracking(frame);
                if (m_ActiveMode == ClientSimControlMode.ClientSim)
                {
                    if ((!m_PreviousLeft.use && frame.leftController.use)
                        || (!m_PreviousRight.use && frame.rightController.use))
                        m_EventSystem.TryClickActiveObject("AcceptButton");

                    m_Adapter.SendChanges(m_PreviousLeft, frame.leftController, m_PreviousRight, frame.rightController);
                    m_PreviousLeft = frame.leftController;
                    m_PreviousRight = frame.rightController;
                }
                return true;
            }
            catch (Exception exception) { FailClosed("Frame bridge failed: " + exception.Message); return false; }
        }

        void SwitchMode(ClientSimControlMode mode)
        {
            if (mode == m_ActiveMode) return;
            ReleaseInjectedState();
            m_ActiveMode = m_ControlMode = mode;
            if (mode == ClientSimControlMode.ClientSim && m_Adapter != null && m_Adapter.isBound)
                ClientSimControlMethods.alignViewerToPlayer(m_Adapter.playerRoot);
            var handler = controlModeChanged;
            if (handler != null) handler(mode);
        }

        void ReleaseInjectedState()
        {
            m_EventSystem.ReleaseAll();
            if (m_Adapter != null && m_Adapter.isBound)
            {
                try { m_Adapter.SendChanges(m_PreviousLeft, default(ClientSimControllerState), m_PreviousRight,
                    default(ClientSimControllerState)); }
                catch (Exception exception) { FailClosed("Could not release controls: " + exception.Message); }
            }
            m_PreviousLeft = default(ClientSimControllerState);
            m_PreviousRight = default(ClientSimControllerState);
        }

        void FailClosed(string reason)
        {
            m_Status = ClientSimCompatibilityStatus.Incompatible;
            m_Diagnostic = reason + " Integration has been disabled.";
            if (m_Adapter != null)
            {
                try { m_Adapter.ReleaseTrackingOverride(); }
                catch (Exception) { }
            }
            m_Adapter = null;
            if (!m_LoggedFailure) { Debug.LogError("[EditorXR ClientSim] " + m_Diagnostic, this); m_LoggedFailure = true; }
        }
    }
}
