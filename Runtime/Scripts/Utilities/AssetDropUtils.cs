using System;
using System.Collections.Generic;
using Unity.EditorXR.Data;
using Unity.EditorXR.Interfaces;
using Unity.XRTools.Utils;
using UnityEngine;
using UnityEngine.Video;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.EditorXR.Utilities
{
    static class AssetDropUtils
    {
        public static List<Material> activeMaterialClones = new List<Material>();

        public static readonly Dictionary<string, List<Type>> AssignmentDependencies
            = new Dictionary<string, List<Type>>
        {
#if INCLUDE_AUDIO_MODULE
            { "AudioClip", new List<Type> { typeof(AudioSource) } },
#endif

#if INCLUDE_VIDEO_MODULE
            { "VideoClip", new List<Type> { typeof(VideoPlayer) } },
#endif

            { "AnimationClip", new List<Type> { typeof(Animation) } },
            { "Material", new List<Type> { typeof(Renderer) } },
            { "Shader", new List<Type> { typeof(Material) } },
            { "PhysicMaterial", new List<Type> {typeof(Collider) } }
        };

        // dependency types to ignore when previewing asset assignment validity
        public static List<Type> AutoFillTypes = new List<Type>
        {
#if INCLUDE_AUDIO_MODULE
            typeof(AudioSource),
#endif

#if INCLUDE_VIDEO_MODULE
            typeof(VideoPlayer),
#endif

            typeof(Animation)
        };

        const string k_AssignFontUndo = "Assign Font";
        const string k_AssignMaterialUndo = "Assign Material";
        const string k_AssignPhysicMaterialUndo = "Assign Physic Material";
        const string k_AssignAnimationClipUndo = "Assign Animation Clip";

#if INCLUDE_AUDIO_MODULE
        const string k_AssignAudioClipUndo = "Assign Audio Clip";
#endif

#if INCLUDE_VIDEO_MODULE
        const string k_AssignVideoClipUndo = "Assign Video Clip";
#endif

        // TODO - make all these booleans options in the settings menu
        static bool s_CreatePlayerForClips = true;
        static bool s_AssignMultipleAnimationClips = true;

        internal static void AssignAnimationClip(Animation animation, AnimationClip clipAsset)
        {
#if UNITY_EDITOR
            RecordObject(animation, k_AssignAnimationClipUndo);
#endif

            if (animation.GetClipCount() > 0 && s_AssignMultipleAnimationClips)
                animation.AddClip(clipAsset, clipAsset.name);
            else
                animation.clip = clipAsset;
        }

        internal static Animation AssignAnimationClip(GameObject go, AssetData data)
        {
            var animation = ComponentUtils.GetOrAddIf<Animation>(go, s_CreatePlayerForClips);
            if (animation != null)
                AssignAnimationClip(animation, (AnimationClip)data.asset);

            return animation;
        }

        internal static void AssignAnimationClipAction(GameObject go, AssetData data)
        {
            AssignAnimationClip(go, data);
        }

#if INCLUDE_AUDIO_MODULE
        internal static AudioSource AttachAudioClip(GameObject go, AssetData data)
        {
            var source = ComponentUtils.GetOrAddIf<AudioSource>(go, s_CreatePlayerForClips);
            if (source != null)
            {
#if UNITY_EDITOR
                RecordObject(source, k_AssignAudioClipUndo);
#endif
                source.clip = (AudioClip)data.asset;
            }

            return source;
        }

        internal static void AudioClipAction(GameObject go, AssetData data)
        {
            AttachAudioClip(go, data);
        }
#endif

#if INCLUDE_VIDEO_MODULE
        internal static VideoPlayer AttachVideoClip(GameObject go, AssetData data)
        {
            var player = ComponentUtils.GetOrAddIf<VideoPlayer>(go, s_CreatePlayerForClips);
            if (player != null)
            {
#if UNITY_EDITOR
                RecordObject(player, k_AssignVideoClipUndo);
#endif
                player.clip = (VideoClip)data.asset;
            }

            return player;
        }

        internal static void VideoClipAction(GameObject go, AssetData data)
        {
            AttachVideoClip(go, data);
        }
#endif

        internal static GameObject AttachScript(GameObject go, AssetData data)
        {
#if UNITY_EDITOR
            var script = (MonoScript)data.asset;
            var type = script.GetClass();
            UnityEditor.Undo.AddComponent(go, type);
#endif
            return go;
        }

        internal static void AttachScriptAction(GameObject go, AssetData data)
        {
            AttachScript(go, data);
        }

        internal static Renderer AssignMaterial(GameObject go, AssetData data)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
#if UNITY_EDITOR
                RecordObject(renderer, k_AssignMaterialUndo);
#endif
                renderer.sharedMaterial = (Material)data.asset;
            }

            return renderers.Length > 0 ? renderers[0] : null;
        }

        internal static void AssignMaterialAction(GameObject go, AssetData data)
        {
            AssignMaterial(go, data);
        }

        internal static Material AssignShader(GameObject go, AssetData data)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
#if UNITY_EDITOR
                RecordObject(renderer, k_AssignMaterialUndo);
#endif

                // copy the material before applying shader to the instance
                // this prevents the warning about leaking materials
                var materialCopy = MaterialUtils.CloneMaterials(renderer)[0];
                var shader = (Shader)data.asset;
                materialCopy.shader = shader;
                renderer.sharedMaterial = materialCopy;

                activeMaterialClones.Add(materialCopy);
                return materialCopy;
            }

            return null;
        }

        internal static void AssignShaderAction(GameObject go, AssetData data)
        {
            AssignShader(go, data);
        }

        internal static PhysicMaterial AssignPhysicMaterial(GameObject go, AssetData data)
        {
            var colliders = go.GetComponentsInChildren<Collider>(true);
            var material = (PhysicMaterial)data.asset;
            foreach (var collider in colliders)
            {
                AssignPhysicMaterial(collider, material);
            }

            return colliders.Length > 0 ? colliders[0].sharedMaterial : null;
        }

        internal static void AssignPhysicMaterialAction(GameObject go, AssetData data)
        {
            AssignPhysicMaterial(go, data);
        }

        internal static void AssignPhysicMaterial(Collider collider, PhysicMaterial material)
        {
#if UNITY_EDITOR
            RecordObject(collider, k_AssignPhysicMaterialUndo);
#endif
            collider.sharedMaterial = material;
        }

        internal static Font AssignFontOnChildren(GameObject go, AssetData data)
        {
            var text = go.GetComponentInChildren<TextMesh>();

            if (text != null)
            {
                var font = (Font)data.asset;

#if UNITY_EDITOR
                RecordObject(text, k_AssignFontUndo);
#endif

                text.font = font;

                return font;
            }

            return null;
        }

        internal static void AssignFontAction(GameObject go, AssetData data)
        {
            AssignFontOnChildren(go, data);
        }

#if UNITY_EDITOR
        static void RecordObject(UnityEngine.Object target, string label)
        {
            if (AuthoringSessionMethods.isSessionActive())
                AuthoringSessionMethods.recordObject(target);
            else
                UnityEditor.Undo.RecordObject(target, label);
        }
#endif
    }
}
