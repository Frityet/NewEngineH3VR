using System.Text;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyReverbMixer
{
    private const string Mixer = "Assets/Resources/audiomixers/Master.mixer";
    private const string Manager = "Assets/Resources/prefabs/_managers/_GameManager.prefab";
    private static readonly (int Slot, string Parameter, string StockName)[] Parameters =
    [
        (1, "Room", "Room"), (2, "Room HF", "RoomHF"), (3, "Decay Time", "DecayTime"),
        (4, "Decay HF Ratio", "DecayHFRatio"), (5, "Reflections", "Reflections"),
        (6, "Reflect Delay", "ReflectionsDelay"), (7, "Reverb", "Reverb"),
        (8, "Reverb Delay", "ReverbDelay"), (9, "Diffusion", "Diffusion"),
        (10, "Density", "Density"), (11, "HF Reference", "HFReference"), (12, "Room LF", "RoomLF")
    ];

    internal static int Repair(string project)
    {
        string path = Path.Combine(project, Mixer), manager = Path.Combine(project, Manager);
        if (!File.Exists(path) || !File.Exists(path + ".meta") || !File.Exists(manager)) return 0;
        var guid = Regex.Match(File.ReadAllText(path + ".meta"), @"(?m)^guid: ([a-f0-9]{32})\r?$");
        if (!guid.Success || !Regex.IsMatch(File.ReadAllText(manager),
                @"(?m)^  MasterMixer: \{fileID: 24100000, guid: " + guid.Groups[1].Value + @", type: 2\}\r?$"))
            return 0;

        string text = File.ReadAllText(path);
        var mixer = Regex.Match(text, @"(?ms)^--- !u!241 &24100000\r?\nAudioMixerController:\r?\n(?<body>.*?)(?=^--- |\z)");
        if (!mixer.Success || !Regex.IsMatch(mixer.Groups["body"].Value, @"(?m)^  m_Name: Master\r?$"))
            return 0;
        var exposures = Regex.Match(mixer.Groups["body"].Value,
            @"(?m)^  m_ExposedParameters:\r?\n(?<rows>(?:  - guid: [a-f0-9]{32}\r?\n    name: [^\r\n]+\r?\n)+)");
        var effects = Regex.Matches(text, @"(?ms)^--- !u!244 &\d+\r?\nAudioMixerEffectController:\r?\n(?<body>.*?)(?=^--- |\z)")
            .Where(section => Regex.IsMatch(section.Groups["body"].Value, @"(?m)^  m_EffectName: SFX Reverb\r?$")).ToArray();
        if (!exposures.Success || effects.Length != 1) return 0;

        var effect = effects[0].Groups["body"];
        var slots = Regex.Matches(effect.Value, @"(?m)^  - m_ParameterName: (?<name>Param_\d+)\r?\n    m_GUID: (?<id>[a-f0-9]{32})\r?$");
        if (slots.Count != 14 || slots.Where((slot, i) => slot.Groups["name"].Value != "Param_" + i).Any())
            return 0;

        var rows = exposures.Groups["rows"];
        var changes = new List<(int Start, int Length, string Value)>();
        // AssetRipper preserves each PI binding as a GUID, but reverses the build's
        // CRC into an arbitrary name. Restore only names with proven CRC and slot.
        foreach (var (index, parameter, stockName) in Parameters)
        {
            var slot = slots[index];
            var names = Regex.Matches(rows.Value, @"(?m)^  - guid: " + slot.Groups["id"].Value + @"\r?\n    name: (?<name>[^\r\n]+)\r?$");
            if (names.Count != 1) continue;
            var exposed = names[0].Groups["name"];
            string original = "MReverb_" + stockName;
            if (Crc32(exposed.Value) != Crc32(original) ||
                Regex.IsMatch(rows.Value, @"(?m)^    name: " + original + @"\r?$"))
                continue;
            var label = slot.Groups["name"];
            changes.Add((effect.Index + label.Index, label.Length, parameter));
            changes.Add((mixer.Groups["body"].Index + rows.Index + exposed.Index, exposed.Length, original));
        }
        if (changes.Count == 0) return 0;
        int repaired = changes.Count / 2;
        // The native effect also needs its original endpoint labels to expose the
        // high-frequency reference and low-frequency room fields. Keep the LF
        // Reference exposure unchanged because the native setter cannot access it.
        var dryLevel = slots[0].Groups["id"].Value;
        var lfReference = slots[13].Groups["id"].Value;
        var dryExposure = Regex.Matches(rows.Value, @"(?m)^  - guid: " + dryLevel + @"\r?\n    name: (?<name>[^\r\n]+)\r?$");
        var lfExposure = Regex.Matches(rows.Value, @"(?m)^  - guid: " + lfReference + @"\r?\n    name: (?<name>[^\r\n]+)\r?$");
        if (repaired == Parameters.Length &&
            slots.Select(slot => slot.Groups["id"].Value).Distinct(StringComparer.Ordinal).Count() == slots.Count &&
            dryExposure.Count == 1 && Crc32(dryExposure[0].Groups["name"].Value) == Crc32("MReverb_DryLevel") &&
            lfExposure.Count == 1 && Crc32(lfExposure[0].Groups["name"].Value) == Crc32("MReverb_LFReference"))
        {
            var dryLabel = slots[0].Groups["name"];
            var lfLabel = slots[13].Groups["name"];
            changes.Add((effect.Index + dryLabel.Index, dryLabel.Length, "Dry Level"));
            changes.Add((effect.Index + lfLabel.Index, lfLabel.Length, "LF Reference"));
        }
        foreach (var change in changes.OrderByDescending(change => change.Start))
            text = text.Remove(change.Start, change.Length).Insert(change.Start, change.Value);
        File.WriteAllText(path, text);
        return repaired;
    }

    private static uint Crc32(string value)
    {
        uint crc = uint.MaxValue;
        foreach (byte character in Encoding.UTF8.GetBytes(value))
        {
            crc ^= character;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
