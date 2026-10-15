using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Reflection;

namespace SharedModels.Utilities
{
    /// <summary>
    /// Lightweight replacement for UnityEngine.JsonUtility that does not depend on Unity.
    /// Uses Newtonsoft.Json and is configured to include non-public instance members so it
    /// can serialize the same private fields that Unity's JsonUtility would.
    /// </summary>
    public static class JsonUtilities
    {
        static readonly JsonSerializerSettings Settings;

        static JsonUtilities()
        {
            var resolver = new DefaultContractResolver
            {
                DefaultMembersSearchFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            };

            Settings = new JsonSerializerSettings
            {
                ContractResolver = resolver,
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public static string ToJson(object obj)
        {
            return JsonConvert.SerializeObject(obj, Settings);
        }

        public static T FromJson<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public static object FromJson(string json, Type type)
        {
            return JsonConvert.DeserializeObject(json, type, Settings);
        }
    }
}
