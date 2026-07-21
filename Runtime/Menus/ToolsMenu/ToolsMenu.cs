using System;
using System.Collections.Generic;
using System.Linq;
using Unity.EditorXR.Core;
using Unity.EditorXR.Interfaces;
using Unity.EditorXR.Modules;
using Unity.EditorXR.Tools;
using Unity.EditorXR.Utilities;
using Unity.XRTools.ModuleLoader;
using UnityEngine;
using UnityEngine.InputNew;

namespace Unity.EditorXR.Menus
{
    sealed class ToolsMenu : MonoBehaviour, IToolsMenu, ITooltip, IUsesConnectInterfaces, IInstantiateUI, IUsesControlHaptics,
        IUsesViewerScale, IControlSpatialScrolling, IUsesControlSpatialHinting, IUsesRayVisibilitySettings, IUsesRayOrigin,
        IUsesRequestFeedback, IUsesFunctionalityInjection
    {
        const int k_ActiveToolOrderPosition = 1; // A active-tool button position used in this particular ToolButton implementation
        const int k_MaxButtonCount = 16;

#pragma warning disable 649
        [SerializeField]
        Sprite m_MainMenuIcon;

        [SerializeField]
        ActionMap m_ActionMap;

        [SerializeField]
        ToolsMenuUI m_ToolsMenuPrefab;

        [SerializeField]
        ToolsMenuButton m_ToolsMenuButtonTemplate;

        [SerializeField]
        HapticPulse m_ButtonClickPulse;

        [SerializeField]
        HapticPulse m_ButtonHoverPulse;

        [SerializeField]
        HapticPulse m_HidingPulse; // The pulse performed when ending a spatial selection
#pragma warning restore 649

        ToolsMenuUI m_ToolsMenuUI;
        Type activeButtonType; // used to prevent re-selection of the currently active tool

        public Transform menuOrigin { get; set; }

        List<IToolsMenuButton> buttons { get { return m_ToolsMenuUI.buttons; } }

        public bool alternateMenuVisible { set { m_ToolsMenuUI.moveToAlternatePosition = value; } }

        public Action<Transform, int, bool> highlightSingleButton { get; set; }
        public Action<Transform> selectHighlightedButton { get; set; }
        public Action<Type, Sprite> setButtonForType { get; set; }
        public Action<Type, Type> deleteToolsMenuButton { get; set; }
        public Node node { get; set; }
        public IToolsMenuButton PreviewToolsMenuButton { get; private set; }
        public Transform alternateMenuOrigin { get; set; }
        public SpatialScrollModule.SpatialScrollData spatialScrollData { get; set; }

        public ActionMap actionMap { get { return m_ActionMap; } }
        public bool ignoreActionMapInputLocking { get; private set; }

        public Transform rayOrigin { get; set; }
        public ControllerRole controllerRole { get; set; }
        public string tooltipText { get { return controllerRole + " Tools"; } }

        public bool mainMenuActivatorInteractable
        {
            set
            {
                if (PreviewToolsMenuButton != null)
                    PreviewToolsMenuButton.interactable = value;
            }
        }

#if !FI_AUTOFILL
        IProvidesViewerScale IFunctionalitySubscriber<IProvidesViewerScale>.provider { get; set; }
        IProvidesFunctionalityInjection IFunctionalitySubscriber<IProvidesFunctionalityInjection>.provider { get; set; }
        IProvidesSelectTool IFunctionalitySubscriber<IProvidesSelectTool>.provider { get; set; }
        IProvidesRequestFeedback IFunctionalitySubscriber<IProvidesRequestFeedback>.provider { get; set; }
        IProvidesRayVisibilitySettings IFunctionalitySubscriber<IProvidesRayVisibilitySettings>.provider { get; set; }
        IProvidesControlSpatialHinting IFunctionalitySubscriber<IProvidesControlSpatialHinting>.provider { get; set; }
        IProvidesControlHaptics IFunctionalitySubscriber<IProvidesControlHaptics>.provider { get; set; }
        IProvidesConnectInterfaces IFunctionalitySubscriber<IProvidesConnectInterfaces>.provider { get; set; }
#endif

        void Awake()
        {
            setButtonForType = CreateToolsMenuButton;
            deleteToolsMenuButton = DeleteToolsMenuButton;
        }

        void OnDestroy()
        {
            this.RemoveRayVisibilitySettings(rayOrigin, this);
        }

        void CreateToolsMenuUI()
        {
            m_ToolsMenuUI = this.InstantiateUI(m_ToolsMenuPrefab.gameObject, rayOrigin, false, rayOrigin).GetComponent<ToolsMenuUI>();
            m_ToolsMenuUI.maxButtonCount = k_MaxButtonCount;
            m_ToolsMenuUI.mainMenuActivatorSelected = this.MainMenuActivatorSelected;
            m_ToolsMenuUI.buttonHovered += OnButtonHover;
            m_ToolsMenuUI.buttonClicked += OnButtonClick;
            m_ToolsMenuUI.buttonSelected += OnButtonSelected;
            m_ToolsMenuUI.closeMenu += CloseMenu;

            // Alternate menu origin isn't set when awake or start run
            var toolsMenuUITransform = m_ToolsMenuUI.transform;
            toolsMenuUITransform.SetParent(alternateMenuOrigin, false);
            toolsMenuUITransform.localPosition = Vector3.zero;
            toolsMenuUITransform.localRotation = Quaternion.identity;
        }

        void CreateToolsMenuButton(Type toolType, Sprite buttonIcon)
        {
            // Verify first that the ToolsMenuUI exists
            // This is called in EditorXR.Tools before the UI can be created herein in Awake
            // The SelectionTool & MainMenu buttons are created immediately after instantiating the ToolsMenu
            if (m_ToolsMenuUI == null)
                CreateToolsMenuUI();

            // Select an existing ToolButton if the type is already present in a button
            if (buttons.Any(x => x.toolType == toolType))
            {
                m_ToolsMenuUI.SelectExistingToolType(toolType);
                return;
            }

            if (buttons.Count >= k_MaxButtonCount) // Return if tool type already occupies a tool button
                return;

            var buttonTransform = EditorXRUtils.Instantiate(m_ToolsMenuButtonTemplate.gameObject, m_ToolsMenuUI.buttonContainer, false).transform;
            var button = buttonTransform.GetComponent<ToolsMenuButton>();
            this.ConnectInterfaces(button);
            this.InjectFunctionalitySingle(button);

            button.rayOrigin = rayOrigin;
            button.toolType = toolType; // Assign Tool Type before assigning order
            if (toolType == typeof(IMainMenu))
                button.tooltip = this;
            button.icon = toolType != typeof(IMainMenu) ? buttonIcon : m_MainMenuIcon;
            button.highlightSingleButton = highlightSingleButton;
            button.selectHighlightedButton = selectHighlightedButton;
            button.rayOrigin = rayOrigin;

            if (toolType == typeof(IMainMenu))
                PreviewToolsMenuButton = button;

            m_ToolsMenuUI.AddButton(button, buttonTransform);
        }

        void DeleteToolsMenuButton(Type toolTypeToDelete, Type toolTypeToSelectAfterDelete)
        {
            if (m_ToolsMenuUI.DeleteButtonOfType(toolTypeToDelete))
                m_ToolsMenuUI.SelectNextExistingToolButton();
        }

        public void ProcessInput(ActionMapInput input, ConsumeControlDelegate consumeControl)
        {
            var toolslMenuInput = (ToolsMenuInput)input;
            if (!toolslMenuInput.show.wasJustPressed)
                return;

            consumeControl(toolslMenuInput.show);
            OnButtonClick();
            this.MainMenuActivatorSelected(rayOrigin);
        }

        void OnButtonClick()
        {
            this.Pulse(node, m_ButtonClickPulse);
            this.SetSpatialHintState(SpatialHintState.Hidden);
        }

        void OnButtonHover()
        {
            this.Pulse(node, m_ButtonHoverPulse);
        }

        void OnButtonSelected(Transform selectingRayOrigin, Type buttonType)
        {
            if (buttonType != typeof(SelectionTool) && buttonType == activeButtonType)
                return;

            activeButtonType = buttonType;
            this.SelectTool(selectingRayOrigin, buttonType, false);
        }

        void CloseMenu()
        {
            this.ClearFeedbackRequests(this);
            this.Pulse(node, m_HidingPulse);
            this.EndSpatialScroll(); // Free the spatial scroll data owned by this object
        }

        public void FakeActivate()
        {
            m_ToolsMenuUI.mainMenuActivatorSelected(rayOrigin);
        }
    }
}
