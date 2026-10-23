using H3VR.Converter;

if (args.Length < 3 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
    Raw H3VR shader recovery bootstrap

    dotnet run --project tools/ShaderRecovery -- <h3vr_Data> <output-dir> <AssetRipper.GUI.Free>

    Requires AssetRipper 1.0.15 with the shader exporter repairs. The output is explicitly
    unvalidated; see docs/shader-recovery.md before using it in a Unity project.
    """);
    return args.Length < 3 ? 2 : 0;
}

try
{
    var options = new ShaderRecoveryOptions(args[0], args[1], args[2]);
    var result = await ShaderRecovery.RecoverAsync(options);
    Console.WriteLine($"Raw shader export complete: {result.ExportedShaders} sources from {result.ShaderObjects} serialized objects.");
    Console.WriteLine($"Output: {result.OutputDirectory}");
    Console.WriteLine($"State: {result.ValidationState}; this is not a validated ShaderSources overlay.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Shader recovery stopped: " + error);
    return 1;
}
