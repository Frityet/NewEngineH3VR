using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.Audio;

namespace NativeH3VR
{
    public static class LegacyReverbParameters
    {
        private static readonly ConditionalWeakTable<AudioMixer, Dictionary<string, bool>> Availability =
            new ConditionalWeakTable<AudioMixer, Dictionary<string, bool>>();
        private static readonly ConditionalWeakTable<AudioMixer, Dictionary<string, bool>>.CreateValueCallback Create =
            _ => new Dictionary<string, bool>(StringComparer.Ordinal);

        public static bool SetFloat(AudioMixer mixer, string name, float value)
        {
            // Probe once per actual mixer and name. Unity also warns for a missing
            // GetFloat, but repeating the probe during interpolation floods the log.
            if (ReferenceEquals(mixer, null))
                return mixer.SetFloat(name, value);
            var names = Availability.GetValue(mixer, Create);
            if (!names.TryGetValue(name, out bool available))
            {
                available = mixer.GetFloat(name, out _);
                names.Add(name, available);
            }
            return available && mixer.SetFloat(name, value);
        }
    }
}
