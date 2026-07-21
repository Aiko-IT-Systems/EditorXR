using UnityEngine;

namespace Unity.EditorXR.Tools
{
    static class WorldScaleUtility
    {
        public const float MinimumHandSeparation = 0.05f;
        public const float FilterTime = 0.03f;
        public const float ScaleDeadZone = 0.01f;
        public const float YawDeadZone = 2f;

        public static bool TryGetScale(float startScale, float startDistance, float distance, float minimum,
            float maximum, out float scale)
        {
            scale = startScale;
            if (!IsFinite(startScale) || !IsFinite(startDistance) || !IsFinite(distance)
                || startDistance < MinimumHandSeparation || distance < 0.000001f)
                return false;

            scale = Mathf.Clamp(startScale * startDistance / distance, minimum, maximum);
            if (Mathf.Abs(scale / startScale - 1f) < ScaleDeadZone)
                scale = startScale;
            return IsFinite(scale);
        }

        public static float FilterFactor(float deltaTime)
        {
            return 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / FilterTime);
        }

        public static float GetYaw(Vector3 startDirection, Vector3 direction)
        {
            startDirection.y = 0f;
            direction.y = 0f;
            if (startDirection.sqrMagnitude < 0.000001f || direction.sqrMagnitude < 0.000001f)
                return 0f;

            var yaw = Vector3.SignedAngle(startDirection, direction, Vector3.up);
            return Mathf.Abs(yaw) < YawDeadZone ? 0f : yaw;
        }

        public static Vector3 GetAnchoredPosition(Vector3 startPosition, Vector3 startMidpointAnchor,
            Vector3 currentMidpoint, Quaternion rotation, float scale)
        {
            return startPosition + startMidpointAnchor - rotation * currentMidpoint * scale;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
