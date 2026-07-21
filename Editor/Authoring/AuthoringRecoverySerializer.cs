#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Unity.EditorXR.Authoring
{
    internal static class AuthoringRecoverySerializer
    {
        static readonly Dictionary<Type, HashSet<string>> k_UnityValueMembers =
            new Dictionary<Type, HashSet<string>>
            {
                { typeof(Color), Members("r", "g", "b", "a") },
                { typeof(Vector2), Members("x", "y") },
                { typeof(Vector3), Members("x", "y", "z") },
                { typeof(Vector4), Members("x", "y", "z", "w") },
                { typeof(Quaternion), Members("x", "y", "z", "w") },
                { typeof(Rect), Members("x", "y", "width", "height") },
                { typeof(Bounds), Members("center", "extents") },
                { typeof(Vector2Int), Members("x", "y") },
                { typeof(Vector3Int), Members("x", "y", "z") },
                { typeof(RectInt), Members("x", "y", "width", "height") },
                { typeof(BoundsInt), Members("position", "size") }
            };

        static readonly JsonSerializerSettings k_Settings = new JsonSerializerSettings
        {
            MaxDepth = 256,
            TypeNameHandling = TypeNameHandling.None,
            ContractResolver = new RecoveryContractResolver()
        };

        public static string ToJson(AuthoringRecoveryData data)
        {
            return JsonConvert.SerializeObject(data, Formatting.Indented, k_Settings);
        }

        public static AuthoringRecoveryData FromJson(string json)
        {
            return JsonConvert.DeserializeObject<AuthoringRecoveryData>(json, k_Settings);
        }

        static HashSet<string> Members(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        sealed class RecoveryContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type,
                MemberSerialization memberSerialization)
            {
                var properties = base.CreateProperties(type, memberSerialization);
                HashSet<string> members;
                if (k_UnityValueMembers.TryGetValue(type, out members))
                    return properties.Where(property => members.Contains(property.UnderlyingName)).ToList();

                if (type.Namespace == typeof(AuthoringRecoveryData).Namespace)
                {
                    members = new HashSet<string>(type
                        .GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Select(field => field.Name), StringComparer.Ordinal);
                    return properties.Where(property => members.Contains(property.UnderlyingName)).ToList();
                }

                return properties;
            }
        }
    }
}
#endif
