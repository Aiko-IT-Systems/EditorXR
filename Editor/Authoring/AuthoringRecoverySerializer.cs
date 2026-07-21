#if UNITY_2022_1_OR_NEWER
using Newtonsoft.Json;

namespace Unity.EditorXR.Authoring
{
    internal static class AuthoringRecoverySerializer
    {
        static readonly JsonSerializerSettings k_Settings = new JsonSerializerSettings
        {
            MaxDepth = 256,
            TypeNameHandling = TypeNameHandling.None
        };

        public static string ToJson(AuthoringRecoveryData data)
        {
            return JsonConvert.SerializeObject(data, Formatting.Indented, k_Settings);
        }

        public static AuthoringRecoveryData FromJson(string json)
        {
            return JsonConvert.DeserializeObject<AuthoringRecoveryData>(json, k_Settings);
        }
    }
}
#endif
