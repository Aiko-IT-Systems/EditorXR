#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.EditorXR.Data;
using Unity.EditorXR.Handles;
using Unity.EditorXR.Interfaces;
using Unity.EditorXR.UI;
using Unity.EditorXR.Utilities;
using Unity.XRTools.ModuleLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Unity.EditorXR.Workspaces
{
    [EditorOnlyWorkspace]
    [MainMenuItem("Project", "Workspaces", "Manage the assets that belong to your project")]
    [SpatialMenuItem("Project", "Workspaces", "Manage the assets that belong to your project")]
    sealed class ProjectWorkspace : Workspace, IUsesProjectFolderData, IFilterUI, ISerializeWorkspace
    {
        const float k_LeftPaneRatio = 0.3333333f; // Size of left pane relative to workspace bounds
        const float k_YBounds = 0.1f;

        const float k_MinScale = 0.04f;
        const float k_MaxScale = 0.09f;
        const float k_ThumbstickScrollSpeed = 0.4f;

        static readonly Vector3 k_MinBounds = new Vector3(MinBounds.x, k_YBounds, 0.5f);
        static readonly Vector3 k_CustomStartingBounds = new Vector3(0.8f, k_YBounds, 0.5f);

        bool m_AssetGridDragging;
        bool m_FolderPanelDragging;

#pragma warning disable 649
        [SerializeField]
        GameObject m_ContentPrefab;

        [SerializeField]
        GameObject m_SliderPrefab;

        [SerializeField]
        GameObject m_FilterPrefab;
#pragma warning restore 649

        ProjectUI m_ProjectUI;
        FilterUI m_FilterUI;
        ZoomSliderUI m_ZoomSliderUI;
        FolderData m_SelectedFolder;

        List<FolderData> m_FolderData;
        List<string> m_FilterList;

        public List<FolderData> folderData
        {
            set
            {
                m_FolderData = value;

                if (m_ProjectUI)
                    m_ProjectUI.folderListView.data = value;
            }
        }

        public List<string> filterList
        {
            set
            {
                m_FilterList = value;
                m_FilterList.Sort();

                if (m_FilterUI)
                    m_FilterUI.filterList = value;
            }
        }

        public string searchQuery { get { return m_FilterUI ? m_FilterUI.searchQuery : string.Empty; } }

        [Serializable]
        class Preferences
        {
            [SerializeField]
            float m_ScaleFactor;

            [SerializeField]
            int m_SelectedFolder;

            [SerializeField]
            List<int> m_ExpandedFolders;

            public float scaleFactor
            {
                get { return m_ScaleFactor; }
                set { m_ScaleFactor = value; }
            }

            public int selectedFolder
            {
                get { return m_SelectedFolder; }
                set { m_SelectedFolder = value; }
            }

            public List<int> expandedFolders
            {
                get { return m_ExpandedFolders; }
                set { m_ExpandedFolders = value; }
            }
        }

        public override void Setup()
        {
            // Initial bounds must be set before the base.Setup() is called
            minBounds = k_MinBounds;
            m_CustomStartingBounds = k_CustomStartingBounds;

            base.Setup();

            topPanelDividerOffset = k_LeftPaneRatio; // enable & position the top-divider(mask) slightly to the left of workspace center

            var content = EditorXRUtils.Instantiate(m_ContentPrefab, m_WorkspaceUI.sceneContainer, false);
            m_ProjectUI = content.GetComponent<ProjectUI>();
            m_ProjectUI.scrolled += OnThumbstickScroll;
            foreach (var behavior in content.GetComponentsInChildren<MonoBehaviour>(true))
            {
                this.InjectFunctionalitySingle(behavior);
            }

            var assetGridView = m_ProjectUI.assetGridView;
            this.ConnectInterfaces(assetGridView);
            assetGridView.matchesFilter = this.MatchesFilter;
            assetGridView.data = new List<AssetData>();

            var folderListView = m_ProjectUI.folderListView;
            this.ConnectInterfaces(folderListView);
            folderListView.folderSelected += OnFolderSelected;

            m_FilterUI = EditorXRUtils.Instantiate(m_FilterPrefab, m_WorkspaceUI.frontPanel, false).GetComponent<FilterUI>();
            foreach (var mb in m_FilterUI.GetComponentsInChildren<MonoBehaviour>())
            {
                this.ConnectInterfaces(mb);
                this.InjectFunctionalitySingle(mb);
            }

            filterList = m_FilterList;
            m_FilterUI.filterChanged += RefreshVisibleAssets;

            // Assigning folder data selects the first folder and refreshes assets, so the filter UI must exist first.
            folderData = m_FolderData;

            foreach (var button in m_FilterUI.GetComponentsInChildren<WorkspaceButton>())
            {
                button.clicked += OnButtonClicked;
                button.hovered += OnButtonHovered;
            }

            var sliderObject = EditorXRUtils.Instantiate(m_SliderPrefab, m_WorkspaceUI.frontPanel, false);
            m_ZoomSliderUI = sliderObject.GetComponent<ZoomSliderUI>();
            m_ZoomSliderUI.zoomSlider.minValue = Mathf.Log10(k_MinScale);
            m_ZoomSliderUI.zoomSlider.maxValue = Mathf.Log10(k_MaxScale);
            m_ZoomSliderUI.sliding += Scale;
            UpdateZoomSliderValue();
            foreach (var mb in m_ZoomSliderUI.GetComponentsInChildren<MonoBehaviour>())
            {
                this.ConnectInterfaces(mb);
                this.InjectFunctionalitySingle(mb);
            }

            var zoomTooltip = sliderObject.GetComponentInChildren<Tooltip>();
            if (zoomTooltip)
                zoomTooltip.tooltipText = "Drag the Handle to Zoom the Asset Grid";

            var scrollHandles = new[]
            {
                m_ProjectUI.folderScrollHandle,
                m_ProjectUI.assetScrollHandle
            };
            foreach (var handle in scrollHandles)
            {
                // Scroll Handle shouldn't move on bounds change
                handle.transform.parent = m_WorkspaceUI.sceneContainer;

                handle.pointerDown += OnScrollPointerDown;
                handle.dragging += OnScrollDragging;
                handle.pointerUp += OnScrollPointerUp;
            }

            // Hookup highlighting calls
            m_ProjectUI.assetScrollHandle.pointerDown += OnAssetGridDragHighlightBegin;
            m_ProjectUI.assetScrollHandle.pointerUp += OnAssetGridDragHighlightEnd;
            m_ProjectUI.assetScrollHandle.hoverStarted += OnAssetGridHoverHighlightBegin;
            m_ProjectUI.assetScrollHandle.hoverEnded += OnAssetGridHoverHighlightEnd;
            m_ProjectUI.folderScrollHandle.pointerDown += OnFolderPanelDragHighlightBegin;
            m_ProjectUI.folderScrollHandle.pointerUp += OnFolderPanelDragHighlightEnd;
            m_ProjectUI.folderScrollHandle.hoverStarted += OnFolderPanelHoverHighlightBegin;
            m_ProjectUI.folderScrollHandle.hoverEnded += OnFolderPanelHoverHighlightEnd;

            // Propagate initial bounds
            OnBoundsChanged();
        }

        public object OnSerializeWorkspace()
        {
            var folderListView = m_ProjectUI.folderListView;

            var preferences = new Preferences
            {
                scaleFactor = m_ProjectUI.assetGridView.scaleFactor,
                expandedFolders = folderListView.expandStates.Where(es => es.Value).Select(es => es.Key).ToList(),
                selectedFolder = folderListView.selectedFolder
            };
            return preferences;
        }

        public void OnDeserializeWorkspace(object obj)
        {
            var folderListView = m_ProjectUI.folderListView;

            var preferences = (Preferences)obj;
            m_ProjectUI.assetGridView.scaleFactor = preferences.scaleFactor;
            preferences.expandedFolders.ForEach(guid => folderListView.expandStates[guid] = true);
            folderListView.selectedFolder = preferences.selectedFolder;
            UpdateZoomSliderValue();
        }

        protected override void OnBoundsChanged()
        {
            const float kScrollHandleHeight = 0.001f;
            const float kScrollHandleYPosition = -0.002f;
            const float kDividerSize = 0.006f;

            var size = contentBounds.size;

            var contentSizeX = size.x - FaceMargin;

            var sizeX = size.x * k_LeftPaneRatio - kDividerSize;
            var sizeZ = size.z - FaceMargin + HighlightMargin;

            var xOffset = (contentSizeX - sizeX) * -0.5f - HighlightMargin * 0.5f;

            var folderScrollHandleTransform = m_ProjectUI.folderScrollHandle.transform;
            folderScrollHandleTransform.localPosition = new Vector3(xOffset, kScrollHandleYPosition, 0);
            folderScrollHandleTransform.localScale = new Vector3(sizeX, kScrollHandleHeight, sizeZ);

            var folderListView = m_ProjectUI.folderListView;
            folderListView.size = new Vector3(sizeX - FaceMargin, k_YBounds, sizeZ - FaceMargin);
            folderListView.transform.localPosition = new Vector3(xOffset, folderListView.itemSize.y * 0.5f, 0); // Center in Y

            sizeX = contentSizeX * (1 - k_LeftPaneRatio);
            xOffset = (contentSizeX - sizeX) * 0.5f;
            sizeX += HighlightMargin;

            var assetScrollHandleTransform = m_ProjectUI.assetScrollHandle.transform;
            assetScrollHandleTransform.localPosition = new Vector3(xOffset, kScrollHandleYPosition, 0);
            assetScrollHandleTransform.localScale = new Vector3(sizeX, kScrollHandleHeight, sizeZ);

            var assetListView = m_ProjectUI.assetGridView;
            assetListView.size = new Vector3(sizeX - FaceMargin, k_YBounds, sizeZ - FaceMargin);
            assetListView.transform.localPosition = Vector3.right * xOffset;
        }

        void OnFolderSelected(FolderData data)
        {
            m_SelectedFolder = data;
            RefreshVisibleAssets();
        }

        void RefreshVisibleAssets()
        {
            var assets = new List<AssetData>();
            var query = searchQuery;
            if (!string.IsNullOrEmpty(query) && m_FolderData != null && m_FolderData.Count > 0)
                m_FolderData[0].CollectAssets(query, assets);
            else if (m_SelectedFolder != null && m_SelectedFolder.assets != null)
                assets.AddRange(m_SelectedFolder.assets);

            m_ProjectUI.assetGridView.data = assets;
            m_ProjectUI.assetGridView.scrollOffset = 0;
        }

        void OnScrollPointerDown(BaseHandle handle, HandleEventData eventData)
        {
            if (handle == m_ProjectUI.folderScrollHandle)
                m_ProjectUI.folderListView.OnScrollStarted();
            else if (handle == m_ProjectUI.assetScrollHandle)
                m_ProjectUI.assetGridView.OnScrollStarted();
        }

        void OnThumbstickScroll(PointerEventData eventData)
        {
            if (eventData == null || Mathf.Approximately(eventData.scrollDelta.y, 0f))
                return;

            var target = eventData.pointerCurrentRaycast.gameObject;
            var targetTransform = target ? target.transform : null;
            var folderList = m_ProjectUI.folderListView;
            var assetGrid = m_ProjectUI.assetGridView;
            var scrollFolder = targetTransform && targetTransform.IsChildOf(folderList.transform);

            var delta = eventData.scrollDelta.y * k_ThumbstickScrollSpeed * Time.unscaledDeltaTime;
            if (scrollFolder)
            {
                folderList.OnScrollStarted();
                folderList.scrollOffset += delta;
                folderList.OnScrollEnded();
            }
            else
            {
                assetGrid.OnScrollStarted();
                assetGrid.scrollOffset += delta;
                assetGrid.OnScrollEnded();
            }
        }

        void OnScrollDragging(BaseHandle handle, HandleEventData eventData)
        {
            if (handle == m_ProjectUI.folderScrollHandle)
                m_ProjectUI.folderListView.scrollOffset -= Vector3.Dot(eventData.deltaPosition, handle.transform.forward) / this.GetViewerScale();
            else if (handle == m_ProjectUI.assetScrollHandle)
                m_ProjectUI.assetGridView.scrollOffset -= Vector3.Dot(eventData.deltaPosition, handle.transform.forward) / this.GetViewerScale();
        }

        void OnScrollPointerUp(BaseHandle handle, HandleEventData eventData)
        {
            if (handle == m_ProjectUI.folderScrollHandle)
                m_ProjectUI.folderListView.OnScrollEnded();
            else if (handle == m_ProjectUI.assetScrollHandle)
                m_ProjectUI.assetGridView.OnScrollEnded();
        }

        void OnAssetGridDragHighlightBegin(BaseHandle handle, HandleEventData eventData)
        {
            m_AssetGridDragging = true;
            m_ProjectUI.assetGridHighlight.visible = true;
        }

        void OnAssetGridDragHighlightEnd(BaseHandle handle, HandleEventData eventData)
        {
            m_AssetGridDragging = false;
            m_ProjectUI.assetGridHighlight.visible = false;
        }

        void OnAssetGridHoverHighlightBegin(BaseHandle handle, HandleEventData eventData)
        {
            m_ProjectUI.assetGridHighlight.visible = true;
        }

        void OnAssetGridHoverHighlightEnd(BaseHandle handle, HandleEventData eventData)
        {
            if (!m_AssetGridDragging)
                m_ProjectUI.assetGridHighlight.visible = false;
        }

        void OnFolderPanelDragHighlightBegin(BaseHandle handle, HandleEventData eventData)
        {
            m_FolderPanelDragging = true;
            m_ProjectUI.folderPanelHighlight.visible = true;
        }

        void OnFolderPanelDragHighlightEnd(BaseHandle handle, HandleEventData eventData)
        {
            m_FolderPanelDragging = false;
            m_ProjectUI.folderPanelHighlight.visible = false;
        }

        void OnFolderPanelHoverHighlightBegin(BaseHandle handle, HandleEventData eventData)
        {
            m_ProjectUI.folderPanelHighlight.visible = true;
        }

        void OnFolderPanelHoverHighlightEnd(BaseHandle handle, HandleEventData eventData)
        {
            if (!m_FolderPanelDragging)
                m_ProjectUI.folderPanelHighlight.visible = false;
        }

        void Scale(float value)
        {
            m_ProjectUI.assetGridView.scaleFactor = Mathf.Pow(10, value);
        }

        void UpdateZoomSliderValue()
        {
            m_ZoomSliderUI.zoomSlider.value = Mathf.Log10(m_ProjectUI.assetGridView.scaleFactor);
        }
    }
}
#endif
