using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NativeH3VR.ShaderOverlay;

/// <summary>Rebind the exact Unity 5 instanced matrix layout to Unity 2021's engine buffers.</summary>
internal static partial class InstancingBindingRepair
{
    private const string Binding = "CBUFFER_START(UnityDrawCallInfo)";
    private const string InverseArray = "NATIVEH3VR_INSTANCE_INVERSE_ARRAY";

    internal sealed record Audit(int VariantCount, int InstanceIdReads, int ObjectToWorldReads,
        int WorldToObjectReads, string VariantGuardsSha256);
    internal sealed record Result(string Source, Audit Bindings);
    private sealed record BranchEdit(string Source, int InstanceIdReads, int ObjectToWorldReads, int WorldToObjectReads);

    [GeneratedRegex(@"\A\s*#pragma vertex vert[ \t]*\r?\n[ \t]*#pragma fragment frag[ \t]*\r?\n\s*#include ""UnityCG\.cginc""[ \t]*\r?\n")]
    private static partial Regex UnityProgramPrefix();

    [GeneratedRegex(@"(?m)^[ \t]*CBUFFER_START\(UnityDrawCallInfo\)[ \t]*\r?\n[ \t]*int unity_BaseInstanceID;[ \t]*\r?\n[ \t]*CBUFFER_END[ \t]*\r?\n[ \t]*CBUFFER_START\(UnityInstancing_PerDraw0\)[ \t]*\r?\n[ \t]*float4x4 unity_ObjectToWorldArray\[500\];[ \t]*\r?\n(?<inverse>[ \t]*float4x4 unity_WorldToObjectArray\[500\];[ \t]*\r?\n)?[ \t]*CBUFFER_END[ \t]*\r?\n")]
    private static partial Regex LegacyBuffers();

    [GeneratedRegex(@"(?m)^[ \t]*CBUFFER_START\(Unity(?:DrawCallInfo|Instancing_PerDraw0)\)[ \t]*\r?\n[ \t]*CBUFFER_END[ \t]*\r?\n")]
    private static partial Regex EmptyEngineBuffers();

    [GeneratedRegex(@"(?m)^[ \t]*CBUFFER_START\(Unity(?:DrawCallInfo|Instancing_PerDraw[01])\)")]
    private static partial Regex RemainingEngineBuffers();

    [GeneratedRegex(@"(?ms)^(?<indent>[ \t]*)v2f vert\(appdata_full v\)[ \t]*\r?\n\k<indent>\{[ \t]*\r?\n(?<body>.*?)^\k<indent>\}[ \t]*\r?$")]
    private static partial Regex VertexFunction();

    [GeneratedRegex(@"\bv\.sv_instanceid\b")]
    private static partial Regex LegacyInput();

    [GeneratedRegex(@"\bv\.sv_instanceid\.x\b")]
    private static partial Regex InstanceIdRead();

    [GeneratedRegex(@"(?m)^[ \t]*tmp\d+\.[xyzw] = v\.sv_instanceid\.x \+ unity_BaseInstanceID;[ \t]*\r?$")]
    private static partial Regex BaseInstanceRead();

    [GeneratedRegex(@"\bunity_(?:ObjectToWorld|WorldToObject)Array\b")]
    private static partial Regex MatrixNames();

    [GeneratedRegex(@"\b(?<matrix>unity_(?:ObjectToWorld|WorldToObject)Array)(?<index>\[\(\(int\)\(tmp\d+\.[xyzw]\) \+ [0-3]\) / 4\])(?=\._m)")]
    private static partial Regex MatrixReads();

    internal static Result? Rewrite(string source, int start, ShaderProgramSplitter.Program program,
        Dictionary<string, ulong> positions)
    {
        if (!positions.TryGetValue("INSTANCING_ON", out ulong instancing)) return null;

        StringBuilder? rewritten = null;
        using var guards = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int cursor = start;
        int variants = 0, instanceReads = 0, objectReads = 0, worldReads = 0;
        foreach (var branch in program.Branches)
        {
            if ((branch.Mask & instancing) == 0) continue;
            var original = source.AsSpan(branch.Start, branch.End - branch.Start);
            if (!original.Contains(Binding, StringComparison.Ordinal)) continue;
            var repair = RewriteBranch(original.ToString());
            if (repair is null) continue;

            rewritten ??= new StringBuilder();
            rewritten.Append(source.AsSpan(cursor, branch.Start - cursor));
            rewritten.Append(repair.Source);
            cursor = branch.End;
            variants++;
            instanceReads += repair.InstanceIdReads;
            objectReads += repair.ObjectToWorldReads;
            worldReads += repair.WorldToObjectReads;
            guards.AppendData(Encoding.UTF8.GetBytes(branch.Condition + "\n"));
        }
        if (rewritten is null) return null;
        rewritten.Append(source.AsSpan(cursor, program.End - cursor));
        return new(rewritten.ToString(), new(variants, instanceReads, objectReads, worldReads,
            Convert.ToHexStringLower(guards.GetHashAndReset())));
    }

    private static BranchEdit? RewriteBranch(string source)
    {
        var prefix = UnityProgramPrefix().Match(source);
        var buffers = LegacyBuffers().Matches(source);
        var vertex = VertexFunction().Matches(source);
        if (!prefix.Success || buffers.Count != 1 || vertex.Count != 1 || source.Contains(InverseArray, StringComparison.Ordinal))
            return null;
        var legacy = buffers[0];
        var code = vertex[0].Groups["body"];
        if (legacy.Index < prefix.Length || legacy.Index + legacy.Length > vertex[0].Index || !BaseInstanceRead().IsMatch(code.Value))
            return null;

        int inputReads = InstanceIdRead().Count(code.Value);
        if (LegacyInput().Count(code.Value) != inputReads) return null;
        var withoutBuffers = string.Concat(source.AsSpan(0, legacy.Index), source.AsSpan(legacy.Index + legacy.Length,
            code.Index - legacy.Index - legacy.Length), InstanceIdRead().Replace(code.Value, "UNITY_GET_INSTANCE_ID(v)"),
            source.AsSpan(code.Index + code.Length));
        withoutBuffers = EmptyEngineBuffers().Replace(withoutBuffers, "");
        if (RemainingEngineBuffers().IsMatch(withoutBuffers)) return null;

        var reads = MatrixReads().Matches(withoutBuffers);
        if (reads.Count != MatrixNames().Count(withoutBuffers)) return null;
        int worldReads = reads.Count(read => read.Groups["matrix"].Value == "unity_WorldToObjectArray");
        int objectReads = reads.Count - worldReads;
        if (objectReads == 0 || worldReads > 0 && !legacy.Groups["inverse"].Success) return null;

        var rewritten = MatrixReads().Replace(withoutBuffers, read =>
        {
            var matrix = read.Groups["matrix"].Value;
            var array = matrix == "unity_ObjectToWorldArray" ? "unity_Builtins0Array" : InverseArray;
            return array + read.Groups["index"].Value + "." + matrix;
        });
        if (worldReads > 0)
        {
            const string selection = "#if UNITY_WORLDTOOBJECTARRAY_CB == 0\n" +
                "#define " + InverseArray + " unity_Builtins0Array\n" +
                "#else\n#define " + InverseArray + " unity_Builtins1Array\n#endif\n";
            rewritten = rewritten.Insert(prefix.Length, selection);
        }
        return new(rewritten, inputReads, objectReads, worldReads);
    }
}
