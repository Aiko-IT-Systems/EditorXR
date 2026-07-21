using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Unity.EditorXR.Tools
{
    struct OrientedSelectionBounds
    {
        public Vector3 center;
        public Quaternion rotation;
        public Vector3 extents;

        public Vector3 size { get { return extents * 2f; } }
    }

    struct ScaleTransformState
    {
        public Transform transform;
        public Vector3 worldPosition;
        public Vector3 localScale;
        public Vector3 worldAxisX;
        public Vector3 worldAxisY;
        public Vector3 worldAxisZ;
    }

    static class ScaleTransformUtility
    {
        const float k_MinDimension = 0.001f;
        const float k_Epsilon = 0.000001f;

        static readonly Vector3[] k_BoundsCorners = new Vector3[8];

        public static Transform[] GetSelectionRoots(IEnumerable<Transform> transforms)
        {
            var selected = new HashSet<Transform>(transforms.Where(t => t));
            return selected.Where(transform =>
            {
                var parent = transform.parent;
                while (parent)
                {
                    if (selected.Contains(parent))
                        return false;
                    parent = parent.parent;
                }

                return true;
            }).ToArray();
        }

        public static OrientedSelectionBounds CalculateBounds(Transform[] roots, Quaternion rotation)
        {
            var inverseRotation = Quaternion.Inverse(rotation);
            var points = new List<Vector3>();
            foreach (var root in roots)
                AddRootBoundsPoints(root, points);

            if (points.Count == 0)
                points.AddRange(roots.Where(root => root).Select(root => root.position));

            if (points.Count == 0)
                return new OrientedSelectionBounds { rotation = rotation, extents = Vector3.one * 0.5f };

            var first = inverseRotation * points[0];
            var min = first;
            var max = first;
            for (var i = 1; i < points.Count; ++i)
            {
                var local = inverseRotation * points[i];
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            var localCenter = (min + max) * 0.5f;
            return new OrientedSelectionBounds
            {
                center = rotation * localCenter,
                rotation = rotation,
                extents = (max - min) * 0.5f,
            };
        }

        static void AddRootBoundsPoints(Transform root, List<Vector3> points)
        {
            if (!root)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length > 0)
            {
                foreach (var renderer in renderers)
                    AddLocalBounds(renderer.localBounds, renderer.transform.localToWorldMatrix, points);
                return;
            }

            var rectTransforms = root.GetComponentsInChildren<RectTransform>(false);
            if (rectTransforms.Length > 0)
            {
                var corners = new Vector3[4];
                foreach (var rectTransform in rectTransforms)
                {
                    rectTransform.GetWorldCorners(corners);
                    points.AddRange(corners);
                }
                return;
            }

#if INCLUDE_PHYSICS_MODULE
            var colliders = root.GetComponentsInChildren<Collider>(false);
            if (colliders.Length > 0)
            {
                foreach (var collider in colliders)
                    AddWorldBounds(collider.bounds, points);
                return;
            }
#endif

            foreach (var child in root.GetComponentsInChildren<Transform>(false))
                points.Add(child.position);
        }

        static void AddLocalBounds(Bounds bounds, Matrix4x4 matrix, List<Vector3> points)
        {
            FillCorners(bounds, k_BoundsCorners);
            for (var i = 0; i < k_BoundsCorners.Length; ++i)
                points.Add(matrix.MultiplyPoint3x4(k_BoundsCorners[i]));
        }

        static void AddWorldBounds(Bounds bounds, List<Vector3> points)
        {
            FillCorners(bounds, k_BoundsCorners);
            points.AddRange(k_BoundsCorners);
        }

        static void FillCorners(Bounds bounds, Vector3[] corners)
        {
            var min = bounds.min;
            var max = bounds.max;
            var index = 0;
            for (var x = 0; x < 2; ++x)
            for (var y = 0; y < 2; ++y)
            for (var z = 0; z < 2; ++z)
                corners[index++] = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);
        }

        public static ScaleTransformState[] CaptureStates(Transform[] roots)
        {
            return roots.Where(root => root).Select(root => new ScaleTransformState
            {
                transform = root,
                worldPosition = root.position,
                localScale = root.localScale,
                worldAxisX = root.TransformVector(Vector3.right),
                worldAxisY = root.TransformVector(Vector3.up),
                worldAxisZ = root.TransformVector(Vector3.forward),
            }).ToArray();
        }

        public static void ApplyScale(ScaleTransformState[] states, Vector3 anchor, Quaternion frameRotation,
            Vector3 factors)
        {
            if (!IsFinite(factors.x) || !IsFinite(factors.y) || !IsFinite(factors.z))
                return;

            factors.x = Mathf.Max(k_MinDimension, factors.x);
            factors.y = Mathf.Max(k_MinDimension, factors.y);
            factors.z = Mathf.Max(k_MinDimension, factors.z);
            var inverseFrame = Quaternion.Inverse(frameRotation);

            foreach (var state in states)
            {
                var transform = state.transform;
                if (!transform)
                    continue;

                var offset = inverseFrame * (state.worldPosition - anchor);
                transform.position = anchor + frameRotation * Vector3.Scale(offset, factors);

                var scale = state.localScale;
                scale.x *= GetAxisScale(state.worldAxisX, frameRotation, inverseFrame, factors);
                scale.y *= GetAxisScale(state.worldAxisY, frameRotation, inverseFrame, factors);
                scale.z *= GetAxisScale(state.worldAxisZ, frameRotation, inverseFrame, factors);
                transform.localScale = scale;
            }
        }

        static float GetAxisScale(Vector3 worldAxis, Quaternion frameRotation, Quaternion inverseFrame, Vector3 factors)
        {
            var length = worldAxis.magnitude;
            if (length < k_Epsilon)
                return 1f;

            var scaled = frameRotation * Vector3.Scale(inverseFrame * worldAxis, factors);
            return Mathf.Max(k_MinDimension, scaled.magnitude / length);
        }

        public static float FaceScaleFactor(float dimension, float outwardDistance, bool symmetric)
        {
            if (dimension < k_Epsilon)
                return 1f;

            var targetDimension = dimension + outwardDistance * (symmetric ? 2f : 1f);
            return Mathf.Max(k_MinDimension, targetDimension) / dimension;
        }

        public static float CornerScaleFactor(Vector3 extents, float outwardDistance, bool symmetric)
        {
            var baseDistance = extents.magnitude * (symmetric ? 1f : 2f);
            if (baseDistance < k_Epsilon)
                return 1f;

            return Mathf.Max(k_MinDimension, 1f + outwardDistance / baseDistance);
        }

        public static Vector3 AxisFactors(Vector3 direction, float factor)
        {
            var factors = Vector3.one;
            if (Mathf.Abs(direction.x) > 0.5f)
                factors.x = factor;
            else if (Mathf.Abs(direction.y) > 0.5f)
                factors.y = factor;
            else
                factors.z = factor;
            return factors;
        }

        public static Vector3 OppositeAnchor(OrientedSelectionBounds bounds, Vector3 direction)
        {
            return bounds.center - bounds.rotation * Vector3.Scale(direction, bounds.extents);
        }

        public static float LongestDimension(Vector3 size)
        {
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        }

        public static float ExponentialLerpFactor(float deltaTime, float timeConstant)
        {
            if (timeConstant <= 0f)
                return 1f;
            return 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / timeConstant);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
