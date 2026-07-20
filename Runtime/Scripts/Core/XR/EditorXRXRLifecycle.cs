using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace Unity.EditorXR.Core.XR
{
    sealed class EditorXRXRLifecycle
    {
        XRManagerSettings m_Manager;
        bool m_InitializingLoader;
        bool m_InitializedLoader;
        bool m_StartedSubsystems;

        internal bool isReady { get; private set; }

        internal IEnumerator Initialize()
        {
            var settings = XRGeneralSettings.Instance;
            if (settings == null || settings.Manager == null)
            {
                Debug.LogError("EditorXR could not find XR Management settings for the current build target.");
                yield break;
            }

            m_Manager = settings.Manager;
            if (m_Manager.activeLoader == null)
            {
                m_InitializingLoader = true;
                yield return m_Manager.InitializeLoader();
                m_InitializingLoader = false;
                if (m_Manager.activeLoader == null)
                {
                    Debug.LogError("EditorXR could not initialize an XR loader. Check XR Plug-in Management and Meta Link.");
                    yield break;
                }

                m_InitializedLoader = true;
            }

            var display = m_Manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
            var input = m_Manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
            if ((display != null && display.running) || (input != null && input.running))
            {
                if ((display == null || !display.running) || (input == null || !input.running))
                {
                    Debug.LogError("EditorXR found a partially running XR loader and will not take ownership of its lifecycle.");
                    yield break;
                }
            }
            else
            {
                m_Manager.StartSubsystems();
                m_StartedSubsystems = true;
                yield return null;
                display = m_Manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                input = m_Manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
            }

            if (display == null || !display.running || input == null || !input.running)
            {
                Debug.LogError("EditorXR initialized the XR loader, but its display or input subsystem did not start.");
                Shutdown();
                yield break;
            }

            var loaderName = m_Manager.activeLoader.GetType().FullName;
            if (loaderName == null || loaderName.IndexOf("Oculus", System.StringComparison.OrdinalIgnoreCase) < 0)
                Debug.LogWarningFormat("EditorXR expected the Oculus XR loader but started '{0}'. Controller mappings may differ.", loaderName);

            EditorXRXRDevices.backend.RefreshDevices();
            isReady = true;
            Debug.LogFormat("EditorXR started XR through {0} (owns loader: {1}, owns subsystems: {2}).",
                loaderName, m_InitializedLoader, m_StartedSubsystems);
        }

        internal void Shutdown()
        {
            isReady = false;
            EditorXRXRDevices.Shutdown();

            if (m_Manager == null)
                return;

            if ((m_InitializedLoader || m_InitializingLoader) && m_Manager.activeLoader != null
                && m_Manager.isInitializationComplete)
            {
                // DeinitializeLoader also stops all subsystems owned by this loader.
                m_Manager.DeinitializeLoader();
            }
            else if (m_StartedSubsystems)
            {
                m_Manager.StopSubsystems();
            }

            m_InitializingLoader = false;
            m_InitializedLoader = false;
            m_StartedSubsystems = false;
            m_Manager = null;
        }
    }
}
