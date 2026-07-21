using System;
using System.Collections.Generic;
using Unity.ListViewFramework;

namespace Unity.EditorXR.Data
{
    sealed class FolderData : NestedListViewItemData<FolderData, int>
    {
        const string k_TemplateName = "FolderListItem";

        List<AssetData> m_Assets;
        readonly int m_Index;
        readonly int m_Depth;

        public string name { get; private set; }
        public string path { get; private set; }
        public List<AssetData> assets { get { return m_Assets; } }
        public override int index { get { return m_Index; } }
        public override string template { get { return k_TemplateName; } }

        public FolderData(string name, int guid, int depth, string path = null)
        {
            this.name = name;
            this.path = path;
            m_Index = guid;
            m_Depth = depth;
        }

        internal FolderData AddFolder(string folderName, string folderPath, int guid)
        {
            if (m_Children == null)
                m_Children = new List<FolderData>();

            var folder = new FolderData(folderName, guid, m_Depth + 1, folderPath);
            m_Children.Add(folder);
            return folder;
        }

        internal void AddAsset(AssetData asset)
        {
            if (m_Assets == null)
                m_Assets = new List<AssetData>();

            m_Assets.Add(asset);
        }

        internal void SortRecursively()
        {
            if (m_Assets != null)
                m_Assets.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            if (m_Children == null)
                return;

            m_Children.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            foreach (var child in m_Children)
                child.SortRecursively();
        }
    }
}
