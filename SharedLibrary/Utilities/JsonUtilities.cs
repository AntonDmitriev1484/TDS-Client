using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SharedLibrary.Game.Actions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SharedLibrary.Utilities
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
                NullValueHandling = NullValueHandling.Ignore,
                Converters = { new SingleActionConverter() }
            };
        }

        public static string ToJson(object obj)
        {
            // Invoke OnBeforeSerialize on the object graph so classes that prepare
            // private fields for serialization (like CubeState) can populate their
            // helper lists before Newtonsoft serializes them.
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            try
            {
                InvokeOnBeforeSerialize(obj, visited);
            }
            catch
            {
                // Ignore errors in pre-serialization callbacks to avoid breaking serialization
            }

            return JsonConvert.SerializeObject(obj, Settings);
        }

        public static T FromJson<T>(string json)
        {
            // Use the non-generic path so OnAfterDeserialize is invoked for the object graph.
            return (T)FromJson(json, typeof(T));
        }

        public static object FromJson(string json, Type type)
        {
            var result = JsonConvert.DeserializeObject(json, type, Settings);

            // Invoke OnAfterDeserialize on the object graph so classes can rebuild
            // runtime-only structures (like dictionaries) from serialized lists.
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            try
            {
                InvokeOnAfterDeserialize(result, visited);
            }
            catch
            {
                // ignore
            }

            return result;
        }

        static void InvokeOnBeforeSerialize(object? obj, HashSet<object> visited)
        {
            if (obj == null) return;

            var type = obj.GetType();

            if (!type.IsClass || obj is string) return;

            if (!visited.Add(obj)) return;

            var method = type.GetMethod("OnBeforeSerialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(obj, null);

            // Traverse fields to reach nested objects
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(obj);
                if (value == null) continue;

                if (value is IEnumerable enumerable && !(value is string))
                {
                    foreach (var item in enumerable)
                        InvokeOnBeforeSerialize(item, visited);
                }
                else
                {
                    InvokeOnBeforeSerialize(value, visited);
                }
            }
        }

        static void InvokeOnAfterDeserialize(object? obj, HashSet<object> visited)
        {
            if (obj == null) return;

            var type = obj.GetType();

            if (!type.IsClass || obj is string) return;

            if (!visited.Add(obj)) return;

            // Traverse fields first so sub-objects are ready when parent callback runs
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(obj);
                if (value == null) continue;

                if (value is IEnumerable enumerable && !(value is string))
                {
                    foreach (var item in enumerable)
                        InvokeOnAfterDeserialize(item, visited);
                }
                else
                {
                    InvokeOnAfterDeserialize(value, visited);
                }
            }

            var method = type.GetMethod("OnAfterDeserialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(obj, null);
        }

        sealed class SingleActionConverter : JsonConverter
        {
            public override bool CanWrite => false;

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(SingleAction);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                    return null;

                var actionObject = JObject.Load(reader);
                var actionTypeToken = actionObject[nameof(SingleAction.SingleActionType)];

                if (actionTypeToken == null)
                    throw new JsonSerializationException("SingleActionType is required to deserialize a SingleAction.");

                var actionType = actionTypeToken.ToObject<SingleActionType>();
                SingleAction action;

                switch (actionType)
                {
                    case SingleActionType.GrabCube:
                        action = new GrabCubeAction();
                        break;
                    case SingleActionType.ReleaseCube:
                        action = new ReleaseCubeAction();
                        break;
                    case SingleActionType.MoveCube:
                        action = new MoveCubeAction();
                        break;
                    default:
                        throw new JsonSerializationException($"Unsupported SingleActionType '{actionType}'.");
                }

                using (var actionReader = actionObject.CreateReader())
                {
                    serializer.Populate(actionReader, action);
                }

                return action;
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }
        }

        // Reference equality comparer for visited hash sets
        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
