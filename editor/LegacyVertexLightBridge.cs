#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace H3VRConverter
{
    internal static class LegacyVertexLightBridge
    {
        private const string Declarations = @"
float4 _NativeH3VRVertexLightColor[8];
float4 _NativeH3VRVertexLightPosition[8];
float4 _NativeH3VRVertexLightAtten[8];
float4 _NativeH3VRVertexSpotDirection[8];
";
        private const string ShadowBridge =
            "\n#pragma dynamic_branch _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE" +
            "\n#include \"Assets/NativeH3VR/GraphicsUpgrade/Shaders/LegacyDirectionalShadow.hlsl\"\n";

        private static readonly HashSet<string> Receivers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Partly Lit/SulfurLitDiffuse", "Partly Lit/SulfurLitDiffuse Transparent",
            "Particles/Vertex Lit Smooth Blended H3VR", "Unlit/PIPScope",
            "Reflex Sight/Reflex Sight Occluded (Fiber Optic)"
        };
        private static readonly Regex VertexLight = new Regex(
            @"\bunity_(?<field>LightColor|LightPosition|LightAtten|SpotDirection)\[\(0 \+ (?<index>[0-7])\) / 1\]",
            RegexOptions.CultureInvariant);
        private static readonly Regex ShadowBlock = new Regex(
            @"(?<guard>tmp\d+\.[xyzw]) = _VertexShadowMapProvided > 0\.5;\s+if \(\k<guard>\) \{(?<body>[^{}]+)\}" +
            @"(?: else \{\s*(?<result>tmp\d+\.[xyzw]) = 1\.0;\s*\})?", RegexOptions.CultureInvariant);
        private static readonly Regex CascadePosition = new Regex(
            @"^\s*tmp\d+\.[xyzw]{3} = (?<world>[\w.]+) - unity_ShadowSplitSpheres\[\(0 \+ 0\) / 1\]\.xyz;");
        private static readonly Regex FiberAttenuation = new Regex(
            @"(?<map>tmp\d+\.[xyzw]) = _VertexLight_ShadowMap\.SampleCmpLevelZero\([^;]+\);\s*" +
            @"(?<light>tmp\d+\.[xyzw]) = (?:\k<map> \* \k<light>|\k<light> \* \k<map>);\s*$");

        internal static string Apply(string source, string migrated, string shaderGuid, ISet<string> changed)
        {
            var name = Regex.Match(source, "^Shader \"([^\"\r\n]+)\"");
            if (!name.Success || !Receivers.Contains(name.Groups[1].Value)) return migrated;
            int start = migrated.IndexOf("CGPROGRAM", StringComparison.Ordinal);
            if (start < 0) return migrated;
            start += "CGPROGRAM".Length;
            int end = migrated.IndexOf("ENDCG", start, StringComparison.Ordinal);
            if (end < 0) return migrated;
            string program = migrated.Substring(start, end - start);
            if (program.Contains(ShadowBridge)) return migrated;

            // Large shader exports keep vertex/fragment variant pairs in per-pass includes.
            // The shadow-caster pass is separate and retains its original uniforms.
            string prefix = "Assets/NativeH3VRShaderPrograms/" + shaderGuid + "/pass-0-";
            bool hasShadows = false, hasLights = false;
            foreach (Match include in Regex.Matches(program, "#include \"(" + Regex.Escape(prefix) + "[0-9]+\\.cginc)\""))
            {
                string path = include.Groups[1].Value;
                string original = File.ReadAllText(path);
                string adapted = Rebind(original, ref hasLights, ref hasShadows);
                if (adapted == original) continue;
                File.WriteAllText(path, adapted);
                AssetDatabase.ImportAsset(path);
                changed.Add(path);
            }
            string inline = Rebind(program, ref hasLights, ref hasShadows);
            if (!hasLights && !hasShadows) return migrated;
            return migrated.Substring(0, start) + (hasShadows ? ShadowBridge : "") +
                (hasLights ? Declarations : "") + inline + migrated.Substring(end);
        }

        private static string Rebind(string program, ref bool hasLights, ref bool hasShadows)
        {
            string shadowed = ShadowBlock.Replace(program, match =>
            {
                string body = match.Groups["body"].Value;
                if (!body.Contains("_VertexLight_ShadowMap.SampleCmpLevelZero")) return match.Value;
                var world = CascadePosition.Match(body);
                if (world.Success && match.Groups["result"].Success)
                    return match.Groups["result"].Value + " = StockShadowStrength(" + world.Groups["world"].Value + ", true);";

                // This stock variant tests the fiber's object-space anchor in its own shadow branch.
                var fiber = FiberAttenuation.Match(body);
                if (fiber.Success && body.Contains("_FiberOpticPos") && body.Contains("unity_ObjectToWorld"))
                    return fiber.Groups["light"].Value + " *= StockShadowStrength(mul(unity_ObjectToWorld, float4(_FiberOpticPos.xyz, 1.0)).xyz, true);";
                return match.Value;
            });
            hasShadows |= shadowed.Contains("StockShadowStrength(");
            string rebound = VertexLight.Replace(shadowed, match =>
            {
                string field = match.Groups["field"].Value;
                string value = "_NativeH3VRVertex" + field + "[" + match.Groups["index"].Value + "]";
                // Recovered lighting uses view space. Transform the shared world-space values per eye.
                return field == "LightPosition" || field == "SpotDirection" ? "mul(UNITY_MATRIX_V, " + value + ")" : value;
            });
            hasLights |= rebound.Contains("_NativeH3VRVertexLightColor");
            return rebound;
        }
    }
}
#endif
