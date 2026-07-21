using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.EditorXR.Core;
using Unity.EditorXR.Extensions;
using Unity.EditorXR.Interfaces;
using Unity.EditorXR.Menus;
using Unity.EditorXR.Utilities;
using Unity.XRTools.ModuleLoader;
using Unity.XRTools.Utils;
using UnityEngine;
using UnityEngine.InputNew;

namespace Unity.EditorXR
{
    /// <summary>
    /// The SpatialMenu controller
    /// A SpatialMenu controller is spawned in EditorXR.Tools SpawnDefaultTools() function, for each proxy/input-device
    /// There is a single static SpatialUI(view) that all SpatialMenu controllers direct
    /// </summary>
    [ProcessInput(2)] // Process input after the ProxyAnimator, but before other IProcessInput implementors
    sealed class SpatialMenu : SpatialUIController, IInstantiateUI, IUsesNode, IUsesRayOrigin,
        IUsesSelectTool, IUsesConnectInterfaces, IUsesControlHaptics, IUsesControlInputIntersection, IUsesSetManipulatorsVisible,
        IUsesRayVisibilitySettings, ICustomActionMap, IUsesViewerScale, IScriptReference
    {
        public class SpatialMenuData
        {
            /// <summary>
            /// Name of the menu whose contents will be added to the menu
            /// </summary>
            public string spatialMenuName { get; private set; }

            /// <summary>
            /// Description of the menu whose contents will be added to the menu
            /// </summary>
            public string spatialMenuDescription { get; private set; }

            /// <summary>
            /// Bool denoting that this element is currently highlighted as either a section title or a sub-menu element
            /// </summary>
            public bool highlighted { get; set; }

            /// <summary>
            /// Collection of elements with which to populate the corresponding spatial UI table/list/view
            /// </summary>
            public List<SpatialMenuElementContainer> spatialMenuElements { get; private set; }

            public SpatialMenuData(string menuName, string menuDescription, List<SpatialMenuElementContainer> menuElements)
            {
                spatialMenuName = menuName;
                spatialMenuDescription = menuDescription;
                spatialMenuElements = menuElements;
            }
        }

        public enum SpatialMenuState
        {
            Hidden,
            NavigatingTopLevel,
            NavigatingSubMenuContent,
        }

        static readonly List<SpatialMenuData> k_SpatialMenuData = new List<SpatialMenuData>();
        static readonly List<Transform> k_AllSpatialMenuRayOrigins = new List<Transform>();
        static readonly List<ISpatialMenuProvider> k_SpatialMenuProviders = new List<ISpatialMenuProvider>();
        static readonly List<SpatialMenu> k_Instances = new List<SpatialMenu>();

        static SpatialMenu s_ControllingSpatialMenu;
        static SpatialMenuUI s_SpatialMenuUI;
        static int s_SubMenuElementCount;
        static SpatialMenuData s_SubMenuData;
        static SpatialMenuState s_SpatialMenuState;

        static int subMenuElementCount { get { return s_SubMenuData.spatialMenuElements.Count; } }

#pragma warning disable 649
        [SerializeField]
        SpatialMenuUI m_SpatialMenuUiPrefab;

        [SerializeField]
        ActionMap m_ActionMap;

        [Header("Haptic Pulses")]
        [SerializeField]
        HapticPulse m_MenuOpenPulse;

        [SerializeField]
        HapticPulse m_MenuClosePulse;

        [SerializeField]
        HapticPulse m_NavigateBackPulse;
#pragma warning restore 649

        bool m_Visible;
        bool m_PersistentOpen;

        SpatialMenuInput m_CurrentSpatialActionMapInput;

        // Bool denoting that the input necessary to keep the SpatialMenu visible is currently being maintained
        bool m_SpatialInputHold;

        // Duration denoting how long the input value has been at default/neutral and thus is in deadzone or lifted
        float m_IdleAtCenterDuration;

        // "Rotate wrist to return" members
        float m_StartingWristXRotation;
        float m_WristReturnVelocity;

        string m_HighlightedSectionNameKey;
        int m_HighlightedTopLevelMenuElementPosition;
        int m_HighlightedSubLevelMenuElementPosition;
        List<SpatialMenuElementContainer> m_DisplayedMenuElements;

        // Trigger + continued/held circular-input related fields
        Vector2 m_OriginalShowMenuCircularInputDirection;
        Vector3 m_UpdatingShowMenuCircularInputDirection;
        bool m_ShowMenuCircularInputCrossedRotationThresholdForSelection;
        float m_TotalShowMenuCircularInputRotation;
        Coroutine m_CircularTriggerSelectionCyclingCoroutine;
        Transform m_RayOrigin;

        bool visible
        {
            set
            {
                if (m_Visible == value)
                    return;

                m_Visible = value;

                if (m_Visible)
                {
                    RefreshProviderData();
                    spatialMenuState = SpatialMenuState.NavigatingTopLevel;
                }
                else
                {
                    // Don't animate a return to the top menu level if closing
                    if (s_ControllingSpatialMenu != null)
                        ReturnToPreviousMenuLevel();

                    this.Pulse(Node.None, m_MenuClosePulse);
                    spatialMenuState = SpatialMenuState.Hidden;
                }
            }
        }

        SpatialMenuState spatialMenuState
        {
            set
            {
                if (s_SpatialMenuState == value)
                    return;

                RefreshProviderData();
                s_SpatialMenuState = value;
                s_SpatialMenuUI.spatialMenuState = s_SpatialMenuState;
                switch (s_SpatialMenuState)
                {
                    case SpatialMenuState.NavigatingTopLevel:
                        m_HighlightedSubLevelMenuElementPosition = -1;
                        s_SubMenuData = null;
                        break;
                    case SpatialMenuState.NavigatingSubMenuContent:
                        s_SubMenuData = k_SpatialMenuData.FirstOrDefault(x => x.highlighted);
                        this.Pulse(Node.None, m_MenuOpenPulse);
                        break;
                    case SpatialMenuState.Hidden:
#if UNITY_EDITOR
                        sceneViewGizmosVisible = true;
#endif
                        m_CircularTriggerSelectionCyclingCoroutine = null;
                        m_CurrentSpatialActionMapInput = null;
                        break;
                }
            }
        }

        public Transform rayOrigin
        {
            private get { return m_RayOrigin; }
            set
            {
                if (m_RayOrigin != null && m_RayOrigin == value)
                    return;

                m_RayOrigin = value;

                // All rayOrigins/devices having spawned a spatial menu are added to this collection
                // The rayorigins in this collection have their pointing direction compared against the spatial UI's
                // forward vector, in order to see if a ray origins that ISN'T currently controlling the spatial UI
                // has begun pointing at the spatial UI, which will override the input typs to ray-based interaction
                // (taking the opposite hand, and pointing it at the menu)

                if (!k_AllSpatialMenuRayOrigins.Contains(m_RayOrigin))
                    k_AllSpatialMenuRayOrigins.Add(m_RayOrigin);
            }
        }

        public Node node { private get; set; }
        public ControllerRole controllerRole { private get; set; }

        // Action Map interface members
        public ActionMap actionMap { get { return m_ActionMap; } }
        public bool ignoreActionMapInputLocking { get; private set; }

        public class SpatialMenuElementContainer
        {
            public SpatialMenuElementContainer(string name, string tooltipText, Action<Node> correspondingFunction)
            {
                this.name = name;
                this.tooltipText = tooltipText;
                this.correspondingFunction = correspondingFunction;
            }

            public string name { get; set; }

            public Action<Node> correspondingFunction { get; private set; }

            public string tooltipText { get; private set; }

            public SpatialMenuElement VisualElement { get; set; }
        }

#if !FI_AUTOFILL
        IProvidesViewerScale IFunctionalitySubscriber<IProvidesViewerScale>.provider { get; set; }
        IProvidesSetManipulatorsVisible IFunctionalitySubscriber<IProvidesSetManipulatorsVisible>.provider { get; set; }
        IProvidesSelectTool IFunctionalitySubscriber<IProvidesSelectTool>.provider { get; set; }
        IProvidesRayVisibilitySettings IFunctionalitySubscriber<IProvidesRayVisibilitySettings>.provider { get; set; }
        IProvidesControlInputIntersection IFunctionalitySubscriber<IProvidesControlInputIntersection>.provider { get; set; }
        IProvidesControlHaptics IFunctionalitySubscriber<IProvidesControlHaptics>.provider { get; set; }
        IProvidesConnectInterfaces IFunctionalitySubscriber<IProvidesConnectInterfaces>.provider { get; set; }
#endif

        public void Setup()
        {
            if (!k_Instances.Contains(this))
                k_Instances.Add(this);

            CreateUI();
        }

#if UNITY_EDITOR
        void OnDestroy()
        {
            // Reset the applicable selection gizmo (SceneView) states
            sceneViewGizmosVisible = true;

            k_Instances.Remove(this);
            if (s_ControllingSpatialMenu == this)
                s_ControllingSpatialMenu = null;

            if (k_Instances.Count == 0 && s_SpatialMenuUI)
                UnityObjectUtils.Destroy(s_SpatialMenuUI.gameObject);
        }
#endif

        void CreateUI()
        {
            if (s_SpatialMenuUI == null)
            {
                var parent = CameraUtils.GetCameraRig();
                s_SpatialMenuUI = this.InstantiateUI(m_SpatialMenuUiPrefab.gameObject, parent, rayOrigin: rayOrigin).GetComponent<SpatialMenuUI>();

                // HACK: For some reason, the spatial menu ends up outside of the VRCameraRig in play mode
                s_SpatialMenuUI.transform.parent = parent;

                s_SpatialMenuUI.spatialMenuData = k_SpatialMenuData; // set shared reference to menu name/type, elements, and highlighted state
                s_SpatialMenuUI.Setup();
                s_SpatialMenuUI.returnToPreviousMenuLevel = ReturnToPreviousMenuLevel;
                s_SpatialMenuUI.changeMenuState = ChangeMenuState;
            }

            visible = false;
        }

        // Delegate function assigned to SpatialMenuUI's changeMenuState action
        // This action is performed when selecting SpatialMenu elements/buttons
        void ChangeMenuState(SpatialMenuState state)
        {
            spatialMenuState = state;
        }

        void RefreshProviderData()
        {
            foreach (var provider in k_SpatialMenuProviders)
            {
                foreach (var menuData in provider.spatialMenuData)
                {
                    // Prevent menus/tools/etc that are instantiated multiple times from adding their contents to the Spatial Menu
                    if (!k_SpatialMenuData.Any(existingData => String.Equals(existingData.spatialMenuName, menuData.spatialMenuName)))
                        k_SpatialMenuData.Add(menuData);
                }
            }
        }

        public static void AddProvider(ISpatialMenuProvider provider)
        {
            if (k_SpatialMenuProviders.Contains(provider))
                return;

            k_SpatialMenuProviders.Add(provider);

            foreach (var menuElementSet in provider.spatialMenuData)
                k_SpatialMenuData.Add(menuElementSet);
        }

        void ReturnToPreviousMenuLevel()
        {
            if (s_SpatialMenuState == SpatialMenuState.NavigatingSubMenuContent)
                this.Pulse(Node.None, m_NavigateBackPulse); // Only perform haptic pulse when not at the top-level of the UI

            spatialMenuState = SpatialMenuState.NavigatingTopLevel;
            m_HighlightedTopLevelMenuElementPosition = -1;
        }

        bool IsAimingAtUI()
        {
            bool isAimingAtUi = false;

            const float kDivergenceThreshold = 45f; // Allowed angular deviation of the device and UI
            var divergenceThresholdConvertedToDot = Mathf.Sin(Mathf.Deg2Rad * kDivergenceThreshold);
            var spatialMenuUITransformPosition = s_SpatialMenuUI.adaptiveTransform != null ? s_SpatialMenuUI.adaptiveTransform.position : Vector3.zero;
            var viewerScale = this.GetViewerScale();
            foreach (var origin in k_AllSpatialMenuRayOrigins)
            {
                if (origin == null)
                    continue;

                var testVector = spatialMenuUITransformPosition - origin.position; // Test device to UI source vector
                var unscaledTestVector = testVector;
                testVector.Normalize(); // Normalize, in order to retain expected dot values
                var inputDeviceForwardDirection = origin.forward;
                var angularComparison = Vector3.Dot(testVector, inputDeviceForwardDirection);

                // Circularly expand/inflate outward from the center, the allowed target/intersection area of the device ray & the UI on the += X-axis
                // This expanded target area will allow a device ray to enable external-ray-mode, with greater tolerance on the +- X-axis, but not the Y-axis
                // This retains the ability of the ray to be more easily pointed upward/downward in order to deactivate this mode, and go into other modes (SpatialSelect, etc)
                // During testing, this allowed for easier targeting of the UI via ray at expected times, better accommodating the expectations of testers)
                const float kAdditiveXPositionOffsetShapingScalar = 3f; // Apply less when near the center of the UI, more towards the outer reach of an extended arm on the X
                var deviceXOffsetInlocalSpace = Mathf.Abs(origin.InverseTransformVector(unscaledTestVector).x - origin.localPosition.x);
                var xPositionOffsetFromCenterAdditiveScalar = 0.8f * viewerScale; // Lessen the amount added for better ergonomic shaping
                var xOffsetAddition = Mathf.Pow(deviceXOffsetInlocalSpace, kAdditiveXPositionOffsetShapingScalar) * xPositionOffsetFromCenterAdditiveScalar / viewerScale;
                angularComparison += xOffsetAddition;

                isAimingAtUi = angularComparison > divergenceThresholdConvertedToDot;

                // Only need to detect at least one proxy ray aiming at the UI
                if (isAimingAtUi)
                    break;
            }

            return isAimingAtUi;
        }

        public void ProcessInput(ActionMapInput input, ConsumeControlDelegate consumeControl)
        {
            m_CurrentSpatialActionMapInput = (SpatialMenuInput)input;
            this.SetRayOriginEnabled(m_RayOrigin, true);
            if (s_ControllingSpatialMenu != this || !m_PersistentOpen || s_SpatialMenuUI == null)
                return;

            s_SpatialMenuUI.spatialInterfaceInputMode = SpatialUIView.SpatialInterfaceInputMode.Ray;
            this.SetManipulatorsVisible(this, false);
            visible = true;
        }

        internal static bool persistentMenuOpen
        {
            get { return s_ControllingSpatialMenu != null && s_ControllingSpatialMenu.m_PersistentOpen; }
        }

        internal static bool TogglePersistent(Transform authoringRayOrigin)
        {
            var menu = k_Instances.FirstOrDefault(instance => instance && instance.m_RayOrigin == authoringRayOrigin);
            if (!menu)
                menu = k_Instances.FirstOrDefault(instance => instance && instance.controllerRole == ControllerRole.Authoring);

            if (!menu)
                return false;

            if (s_ControllingSpatialMenu == menu && menu.m_PersistentOpen)
                menu.EndDisplayOfMenu();
            else
                menu.BeginPersistentDisplay();

            return true;
        }

        internal static void CloseActiveMenu()
        {
            if (s_ControllingSpatialMenu != null)
                s_ControllingSpatialMenu.EndDisplayOfMenu();
        }

        void BeginPersistentDisplay()
        {
            if (s_ControllingSpatialMenu != null && s_ControllingSpatialMenu != this)
                s_ControllingSpatialMenu.EndDisplayOfMenu();

            s_ControllingSpatialMenu = this;
            m_PersistentOpen = true;
#if UNITY_EDITOR
            sceneViewGizmosVisible = false;
#endif
            s_SpatialMenuUI.changeMenuState = ChangeMenuState;
            visible = true;
            s_SpatialMenuUI.spatialInterfaceInputMode = SpatialUIView.SpatialInterfaceInputMode.Ray;
            this.SetRayOriginEnabled(m_RayOrigin, true);
            this.SetManipulatorsVisible(this, false);
            this.Pulse(node, m_MenuOpenPulse);
        }

        bool CancelWasJustPressedTest(ConsumeControlDelegate consumeControl)
        {
            var cancelJustPressed = false;
            if (m_CurrentSpatialActionMapInput.cancel.wasJustPressed || m_CurrentSpatialActionMapInput.grip.wasJustPressed)
            {
                cancelJustPressed = true;
                ConsumeControls(m_CurrentSpatialActionMapInput, consumeControl);
                m_HighlightedTopLevelMenuElementPosition = -1;
                s_ControllingSpatialMenu.ReturnToPreviousMenuLevel();
            }

            return cancelJustPressed;
        }

        void SelectJustPressedTest(ConsumeControlDelegate consumeControl, bool isNodeThatActivatedMenu = true)
        {
            if (m_CurrentSpatialActionMapInput.select.wasJustPressed)
            {
                if (s_SpatialMenuState == SpatialMenuState.NavigatingTopLevel)
                    s_SpatialMenuUI.SectionTitleButtonSelected(node);
                else if (s_SpatialMenuState == SpatialMenuState.NavigatingSubMenuContent)
                    s_SpatialMenuUI.SelectCurrentlyHighlightedElement(node, isNodeThatActivatedMenu);

                ConsumeControls(m_CurrentSpatialActionMapInput, consumeControl);
            }
        }

        void EndDisplayOfMenu()
        {
            s_ControllingSpatialMenu = null; // Allow another SpatialMenu to own control of the SpatialMenuUI
            m_PersistentOpen = false;
            m_CurrentSpatialActionMapInput = null;
            m_TotalShowMenuCircularInputRotation = 0;
            m_HighlightedTopLevelMenuElementPosition = -1;
            m_HighlightedSubLevelMenuElementPosition = -1;
            this.SetManipulatorsVisible(this, true);
            this.SetRayOriginEnabled(m_RayOrigin, true);
            visible = false;
        }

        IEnumerator TimedCircularTriggerSelection(bool selectNextItem = true)
        {
            var elementPositionOffset = selectNextItem ? 1 : -1;
            if (s_SpatialMenuState == SpatialMenuState.NavigatingTopLevel)
            {
                // User should return to the previously highligted position at this depth of the SpatialMenu
                var menuElementCount = k_SpatialMenuData.Count;
                m_HighlightedTopLevelMenuElementPosition = (int)Mathf.Repeat(m_HighlightedTopLevelMenuElementPosition + elementPositionOffset, menuElementCount);
                s_SpatialMenuUI.HighlightElementInCurrentlyDisplayedMenuSection(m_HighlightedTopLevelMenuElementPosition);
            }
            else if (s_SpatialMenuState == SpatialMenuState.NavigatingSubMenuContent)
            {
                // User should return to the previously highligted position at this depth of the SpatialMenu
                m_HighlightedSubLevelMenuElementPosition = (int)Mathf.Repeat(m_HighlightedSubLevelMenuElementPosition + elementPositionOffset, subMenuElementCount);
                s_SpatialMenuUI.HighlightElementInCurrentlyDisplayedMenuSection(m_HighlightedSubLevelMenuElementPosition);
            }

            // Prevent the cycling to another element by keeping the coroutine reference from being null for a period of time
            // The coroutine reference is tested against in ProcessInput(), only allowing the cycling to previous/next element if null
            const float kSelectionTimingBuffer = 0.2f;
            var duration = 0f;
            while (duration < kSelectionTimingBuffer)
            {
                duration += Time.unscaledDeltaTime;
                yield return null;
            }

            m_CircularTriggerSelectionCyclingCoroutine = null;
            yield return null;
        }
    }
}
