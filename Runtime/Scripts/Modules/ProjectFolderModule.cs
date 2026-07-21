using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.EditorXR.Core;
using Unity.EditorXR.Data;
using Unity.XRTools.ModuleLoader;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;

namespace Unity.EditorXR.Modules
{
#if UNITY_EDITOR
    sealed class ProjectFolderModule : MonoBehaviour, IDelayedInitializationModule, IInterfaceConnector
    {
        const float k_ResultProcessingBudget = 0.004f;
        const string k_ProjectSearchProvider = "asset";

        static readonly string[] k_ProjectSearchQueries =
        {
            "a:assets",
            "a:assets t:folder",
            "a:assets t:Material",
            "a:assets t:Prefab",
            "a:assets t:Model",
            "a:assets t:Texture",
            "a:assets t:Shader",
            "a:assets t:Scene",
            "a:assets t:Script",
            "a:assets t:AnimationClip",
            "a:assets t:AudioClip",
            "a:assets t:VideoClip",
            "a:assets t:Font",
            "a:assets ext:asset",
            "a:assets ext:controller"
        };

        readonly List<IFilterUI> m_FilterUIs = new List<IFilterUI>();
        readonly List<IUsesProjectFolderData> m_ProjectFolderLists = new List<IUsesProjectFolderData>();
        readonly HashSet<string> m_AssetTypes = new HashSet<string>();
        readonly Queue<string> m_PendingSearchIds = new Queue<string>();
        readonly object m_SearchLock = new object();

        List<FolderData> m_FolderData;
        readonly List<SearchContext> m_SearchContexts = new List<SearchContext>();
        Coroutine m_ResultProcessor;
        int m_SearchGeneration;
        int m_PendingSearches;
        bool m_SearchCompleted;
        bool m_RefreshScheduled;

        public int initializationOrder { get { return 0; } }
        public int shutdownOrder { get { return 0; } }
        public int connectInterfaceOrder { get { return 0; } }

        public void Initialize()
        {
            EditorApplication.projectChanged += ScheduleProjectFolderRefresh;
            UpdateProjectFolders();
        }

        public void Shutdown()
        {
            EditorApplication.projectChanged -= ScheduleProjectFolderRefresh;
            EditorApplication.delayCall -= UpdateProjectFolders;
            CancelSearch();
        }

        public void AddConsumer(IUsesProjectFolderData consumer)
        {
            consumer.folderData = GetFolderData();
            m_ProjectFolderLists.Add(consumer);
        }

        public void RemoveConsumer(IUsesProjectFolderData consumer)
        {
            m_ProjectFolderLists.Remove(consumer);
        }

        public void AddConsumer(IFilterUI consumer)
        {
            consumer.filterList = GetFilterList();
            m_FilterUIs.Add(consumer);
        }

        public void RemoveConsumer(IFilterUI consumer)
        {
            m_FilterUIs.Remove(consumer);
        }

        List<string> GetFilterList()
        {
            return m_AssetTypes.ToList();
        }

        List<FolderData> GetFolderData()
        {
            if (m_FolderData == null)
                m_FolderData = new List<FolderData>();

            return m_FolderData;
        }

        void ScheduleProjectFolderRefresh()
        {
            if (m_RefreshScheduled)
                return;

            m_RefreshScheduled = true;
            EditorApplication.delayCall += UpdateProjectFolders;
        }

        void UpdateProjectFolders()
        {
            EditorApplication.delayCall -= UpdateProjectFolders;
            m_RefreshScheduled = false;
            CancelSearch();

            m_AssetTypes.Clear();
            var generation = m_SearchGeneration;
            m_PendingSearches = k_ProjectSearchQueries.Length;
            foreach (var query in k_ProjectSearchQueries)
            {
                var context = SearchService.CreateContext(k_ProjectSearchProvider, query,
                    SearchFlags.Default | SearchFlags.WantsMore);
                context.wantsMore = true;
                m_SearchContexts.Add(context);
                SearchService.Request(context,
                    (searchContext, items) => QueueSearchItems(generation, items),
                    searchContext => CompleteSearch(generation), SearchFlags.Default | SearchFlags.WantsMore);
            }

            m_ResultProcessor = StartCoroutine(ProcessSearchResults(generation));
        }

        void QueueSearchItems(int generation, IEnumerable<SearchItem> items)
        {
            if (generation != m_SearchGeneration)
                return;

            lock (m_SearchLock)
            {
                foreach (var item in items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.id))
                        m_PendingSearchIds.Enqueue(item.id);
                }
            }
        }

        void CompleteSearch(int generation)
        {
            if (generation != m_SearchGeneration)
                return;

            lock (m_SearchLock)
            {
                m_PendingSearches--;
                m_SearchCompleted = m_PendingSearches <= 0;
            }
        }

        IEnumerator ProcessSearchResults(int generation)
        {
            var rootGuid = AssetDatabase.AssetPathToGUID("Assets");
            var root = new FolderData("Assets", GetStableIndex(rootGuid, "Assets"), 0, "Assets");
            var folders = new Dictionary<string, FolderData>(StringComparer.OrdinalIgnoreCase)
            {
                { "Assets", root }
            };
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (generation == m_SearchGeneration)
            {
                var frameStart = Time.realtimeSinceStartup;
                string searchId;
                while (TryDequeue(out searchId))
                {
                    AddSearchResult(searchId, root, folders, seenPaths);
                    if (Time.realtimeSinceStartup - frameStart >= k_ResultProcessingBudget)
                        break;
                }

                if (IsSearchDrained())
                    break;

                yield return null;
            }

            if (generation != m_SearchGeneration)
                yield break;

            root.SortRecursively();
            SetupFolderData(root);
            DisposeSearchContexts();
            m_ResultProcessor = null;
        }

        bool TryDequeue(out string searchId)
        {
            lock (m_SearchLock)
            {
                if (m_PendingSearchIds.Count > 0)
                {
                    searchId = m_PendingSearchIds.Dequeue();
                    return true;
                }
            }

            searchId = null;
            return false;
        }

        bool IsSearchDrained()
        {
            lock (m_SearchLock)
            {
                return m_SearchCompleted && m_PendingSearchIds.Count == 0;
            }
        }

        void AddSearchResult(string searchId, FolderData root, IDictionary<string, FolderData> folders,
            ISet<string> seenPaths)
        {
            string path;
            if (!ProjectSearchUtility.TryResolveAssetPath(searchId, out path)
                || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !seenPaths.Add(path))
                return;

            if (AssetDatabase.IsValidFolder(path))
            {
                GetOrCreateFolder(path, root, folders);
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                return;

            directory = directory.Replace('\\', '/');
            var folder = GetOrCreateFolder(directory, root, folders);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var typeName = ProjectSearchUtility.GetAssetTypeName(path);
            folder.AddAsset(new AssetData(Path.GetFileNameWithoutExtension(path), guid, typeName));
            m_AssetTypes.Add(typeName);
        }

        static FolderData GetOrCreateFolder(string path, FolderData root,
            IDictionary<string, FolderData> folders)
        {
            FolderData folder;
            if (folders.TryGetValue(path, out folder))
                return folder;

            var parentPath = Path.GetDirectoryName(path);
            parentPath = string.IsNullOrEmpty(parentPath) ? "Assets" : parentPath.Replace('\\', '/');
            var parent = string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase)
                ? root
                : GetOrCreateFolder(parentPath, root, folders);
            var guid = AssetDatabase.AssetPathToGUID(path);
            folder = parent.AddFolder(Path.GetFileName(path), path, GetStableIndex(guid, path));
            folders[path] = folder;
            return folder;
        }

        static int GetStableIndex(string guid, string fallback)
        {
            return string.IsNullOrEmpty(guid) ? fallback.GetHashCode() : guid.GetHashCode();
        }

        void SetupFolderData(FolderData folderData)
        {
            m_FolderData = new List<FolderData> { folderData };

            foreach (var list in m_ProjectFolderLists)
                list.folderData = GetFolderData();

            foreach (var filterUI in m_FilterUIs)
                filterUI.filterList = GetFilterList();
        }

        void CancelSearch()
        {
            m_SearchGeneration++;
            if (m_ResultProcessor != null)
            {
                StopCoroutine(m_ResultProcessor);
                m_ResultProcessor = null;
            }

            DisposeSearchContexts();
            lock (m_SearchLock)
            {
                m_PendingSearchIds.Clear();
                m_PendingSearches = 0;
                m_SearchCompleted = false;
            }
        }

        void DisposeSearchContexts()
        {
            foreach (var context in m_SearchContexts)
                context.Dispose();

            m_SearchContexts.Clear();
        }

        public void LoadModule() { }

        public void UnloadModule() { }

        public void ConnectInterface(object target, object userData = null)
        {
            var usesProjectFolderData = target as IUsesProjectFolderData;
            if (usesProjectFolderData == null)
                return;

            AddConsumer(usesProjectFolderData);
            var filterUI = target as IFilterUI;
            if (filterUI != null)
                AddConsumer(filterUI);
        }

        public void DisconnectInterface(object target, object userData = null)
        {
            var usesProjectFolderData = target as IUsesProjectFolderData;
            if (usesProjectFolderData == null)
                return;

            RemoveConsumer(usesProjectFolderData);
            var filterUI = target as IFilterUI;
            if (filterUI != null)
                RemoveConsumer(filterUI);
        }
    }

    static class ProjectSearchUtility
    {
        internal static bool TryResolveAssetPath(string searchId, out string path)
        {
            GlobalObjectId globalObjectId;
            if (GlobalObjectId.TryParse(searchId, out globalObjectId))
            {
                path = AssetDatabase.GUIDToAssetPath(globalObjectId.assetGUID.ToString());
                return !string.IsNullOrEmpty(path);
            }

            path = null;
            return false;
        }

        internal static string GetAssetTypeName(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".prefab":
                    return AssetData.PrefabTypeString;
                case ".fbx":
                case ".obj":
                case ".dae":
                case ".3ds":
                    return AssetData.ModelTypeString;
                case ".asset":
                    return "Asset";
            }

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null)
                return string.Empty;

            switch (type.Name)
            {
                case "MonoScript":
                    return "Script";
                case "SceneAsset":
                    return "Scene";
                case "AudioMixerController":
                    return "AudioMixer";
                default:
                    return type.Name;
            }
        }
    }
#else
    sealed class ProjectFolderModule : MonoBehaviour
    {
    }
#endif
}
