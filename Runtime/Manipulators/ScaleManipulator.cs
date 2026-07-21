using System;
using System.Collections.Generic;
using Unity.EditorXR.Handles;
using UnityEngine;

namespace Unity.EditorXR.Manipulators
{
    enum ScaleHandleKind
    {
        Face,
        Corner,
    }

    enum ScaleDragPhase
    {
        Started,
        Updated,
        Ended,
    }

    struct ScaleDragData
    {
        public ScaleDragPhase phase;
        public ScaleHandleKind kind;
        public Vector3 direction;
        public Transform rayOrigin;
        public float totalDistance;
    }

    sealed class ScaleManipulator : BaseManipulator
    {
        struct HandleDescriptor
        {
            public ScaleHandleKind kind;
            public Vector3 direction;
        }

#pragma warning disable 649
        [SerializeField]
        BaseHandle m_UniformHandle;
#pragma warning restore 649

        readonly Dictionary<BaseHandle, HandleDescriptor> m_HandleDescriptors =
            new Dictionary<BaseHandle, HandleDescriptor>();

        OrientedBoundsData m_Bounds;
        bool m_HasBounds;
        HandleDescriptor m_ActiveDescriptor;
        float m_TotalDistance;
        LineRenderer m_BoundsCage;

        struct OrientedBoundsData
        {
            public Vector3 extents;
        }

        public Action<ScaleDragData> scaleDrag { private get; set; }

        protected override void Awake()
        {
            BuildHandles();
            base.Awake();
        }

        void BuildHandles()
        {
            if (m_UniformHandle == null || m_AllHandles == null)
            {
                Debug.LogError("ScaleManipulator requires a uniform handle and axis handle list.", this);
                return;
            }

            var originalHandles = m_AllHandles.ToArray();
            foreach (var handle in originalHandles)
            {
                if (handle == m_UniformHandle)
                    continue;

                var direction = transform.InverseTransformDirection(handle.transform.forward).normalized;
                direction = Cardinalize(direction);
                m_HandleDescriptors[handle] = new HandleDescriptor { kind = ScaleHandleKind.Face, direction = direction };

                var negative = Instantiate(handle.gameObject, handle.transform.parent, false).GetComponent<BaseHandle>();
                negative.name = handle.name + "Negative";
                SetForward(negative.transform, -direction);
                m_AllHandles.Add(negative);
                m_HandleDescriptors[negative] = new HandleDescriptor
                {
                    kind = ScaleHandleKind.Face,
                    direction = -direction,
                };
            }

            var cornerDirections = new[]
            {
                new Vector3(1, 1, 1), new Vector3(1, 1, -1), new Vector3(1, -1, 1), new Vector3(1, -1, -1),
                new Vector3(-1, 1, 1), new Vector3(-1, 1, -1), new Vector3(-1, -1, 1), new Vector3(-1, -1, -1),
            };

            for (var i = 0; i < cornerDirections.Length; ++i)
            {
                var corner = i == 0
                    ? m_UniformHandle
                    : Instantiate(m_UniformHandle.gameObject, m_UniformHandle.transform.parent, false).GetComponent<BaseHandle>();
                corner.name = "UniformCorner" + i;
                SetForward(corner.transform, cornerDirections[i].normalized);
                if (i > 0)
                    m_AllHandles.Add(corner);
                m_HandleDescriptors[corner] = new HandleDescriptor
                {
                    kind = ScaleHandleKind.Corner,
                    direction = cornerDirections[i],
                };
            }

            var cageObject = new GameObject("Bounds Cage");
            cageObject.transform.SetParent(transform, false);
            m_BoundsCage = cageObject.AddComponent<LineRenderer>();
            m_BoundsCage.useWorldSpace = false;
            m_BoundsCage.loop = false;
            m_BoundsCage.positionCount = 16;
            m_BoundsCage.startWidth = 0.0025f;
            m_BoundsCage.endWidth = 0.0025f;
            var renderer = m_UniformHandle.GetComponent<Renderer>();
            if (renderer)
                m_BoundsCage.sharedMaterial = renderer.sharedMaterial;
        }

        static Vector3 Cardinalize(Vector3 direction)
        {
            var abs = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
            if (abs.x >= abs.y && abs.x >= abs.z)
                return Vector3.right * Mathf.Sign(direction.x);
            return abs.y >= abs.z ? Vector3.up * Mathf.Sign(direction.y) : Vector3.forward * Mathf.Sign(direction.z);
        }

        static void SetForward(Transform target, Vector3 direction)
        {
            var up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            target.localRotation = Quaternion.LookRotation(direction.normalized, up);
        }

        public void SetBounds(Vector3 extents)
        {
            m_Bounds.extents = extents;
            m_HasBounds = true;
            UpdateHandleLayout();
        }

        void Update()
        {
            if (m_HasBounds && !dragging)
                UpdateHandleLayout();
        }

        void UpdateHandleLayout()
        {
            var rootScale = Mathf.Max(0.000001f, transform.lossyScale.x);
            foreach (var pair in m_HandleDescriptors)
                pair.Key.transform.localPosition = Vector3.Scale(pair.Value.direction, m_Bounds.extents) / rootScale;

            if (!m_BoundsCage)
                return;

            var e = m_Bounds.extents / rootScale;
            var corners = new[]
            {
                new Vector3(-e.x, -e.y, -e.z), new Vector3(e.x, -e.y, -e.z),
                new Vector3(-e.x, e.y, -e.z), new Vector3(e.x, e.y, -e.z),
                new Vector3(-e.x, -e.y, e.z), new Vector3(e.x, -e.y, e.z),
                new Vector3(-e.x, e.y, e.z), new Vector3(e.x, e.y, e.z),
            };
            var order = new[] { 0, 1, 3, 2, 0, 4, 5, 1, 5, 7, 3, 7, 6, 2, 6, 4 };
            for (var i = 0; i < order.Length; ++i)
                m_BoundsCage.SetPosition(i, corners[order[i]]);
        }

        protected override void OnHandlePointerDown(BaseHandle handle, HandleEventData eventData)
        {
            base.OnHandlePointerDown(handle, eventData);
            if (handle.IndexOfDragSource(eventData.rayOrigin) > 0)
                return;

            if (!m_HandleDescriptors.TryGetValue(handle, out m_ActiveDescriptor))
                return;
            m_TotalDistance = 0f;
            SendScaleDrag(ScaleDragPhase.Started, eventData.rayOrigin);
        }

        protected override void OnHandleDragging(BaseHandle handle, HandleEventData eventData)
        {
            base.OnHandleDragging(handle, eventData);
            if (handle.IndexOfDragSource(eventData.rayOrigin) != 0)
                return;

            if (handle == m_UniformHandle)
            {
                if (scaleDrag == null)
                    scale(Vector3.one * eventData.deltaPosition.y / transform.localScale.x);
            }
            else
            {
                if (scaleDrag == null)
                {
                    var handleTransform = handle.transform;
                    var inverseRotation = Quaternion.Inverse(handleTransform.rotation);
                    var localStartDragPosition = inverseRotation
                        * (handle.startDragPositions[eventData.rayOrigin] - handleTransform.position);
                    var delta = (inverseRotation * eventData.deltaPosition).z / localStartDragPosition.z;
                    scale(Quaternion.Inverse(transform.rotation) * handleTransform.forward * delta);
                }
            }

            if (scaleDrag != null)
            {
                m_TotalDistance += Vector3.Dot(eventData.deltaPosition, handle.transform.forward);
                SendScaleDrag(ScaleDragPhase.Updated, eventData.rayOrigin);
            }
        }

        protected override void OnHandlePointerUp(BaseHandle handle, HandleEventData eventData)
        {
            if (handle.IndexOfDragSource(eventData.rayOrigin) > 0)
                return;

            SendScaleDrag(ScaleDragPhase.Ended, eventData.rayOrigin);
            base.OnHandlePointerUp(handle, eventData);
        }

        void SendScaleDrag(ScaleDragPhase phase, Transform rayOrigin)
        {
            if (scaleDrag == null)
                return;

            scaleDrag(new ScaleDragData
            {
                phase = phase,
                kind = m_ActiveDescriptor.kind,
                direction = m_ActiveDescriptor.direction,
                rayOrigin = rayOrigin,
                totalDistance = m_TotalDistance,
            });
        }

        protected override void UpdateHandleTip(BaseHandle handle, HandleEventData eventData, bool active)
        {
            if (handle == m_UniformHandle)
                return;

            base.UpdateHandleTip(handle, eventData, active);
        }
    }
}
