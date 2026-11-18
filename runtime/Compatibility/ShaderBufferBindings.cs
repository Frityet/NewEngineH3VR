using System.Collections.Concurrent;
using UnityEngine;

namespace NativeH3VR
{
    public static class ShaderBufferBindings
    {
        private const string CountPrefix = "_NativeH3VRBufferCount";
        private static readonly ConcurrentDictionary<int, string> PropertyNames =
            new ConcurrentDictionary<int, string>();
        private static readonly ConcurrentDictionary<int, int> CountIds =
            new ConcurrentDictionary<int, int>();

        public static int PropertyToID(string name)
        {
            var id = Shader.PropertyToID(name);
            if (!string.IsNullOrEmpty(name))
                PropertyNames.TryAdd(id, name);
            return id;
        }

        public static void SetBuffer(Material material, string name, ComputeBuffer buffer) =>
            SetBuffer(material, PropertyToID(name), buffer);

        public static void SetBuffer(Material material, int nameId, ComputeBuffer buffer)
        {
            material.SetBuffer(nameId, buffer);
            if (TryGetCountId(nameId, out var countId))
                material.SetInteger(countId, buffer?.count ?? 0);
        }

        public static void SetBuffer(MaterialPropertyBlock properties, string name, ComputeBuffer buffer) =>
            SetBuffer(properties, PropertyToID(name), buffer);

        public static void SetBuffer(MaterialPropertyBlock properties, int nameId, ComputeBuffer buffer)
        {
            properties.SetBuffer(nameId, buffer);
            if (TryGetCountId(nameId, out var countId))
                properties.SetInteger(countId, buffer?.count ?? 0);
        }

        private static bool TryGetCountId(int nameId, out int countId)
        {
            if (CountIds.TryGetValue(nameId, out countId))
                return true;
            if (!PropertyNames.TryGetValue(nameId, out var name))
                return false;
            countId = Shader.PropertyToID(CountPrefix + name);
            CountIds.TryAdd(nameId, countId);
            return true;
        }
    }
}
