using H3VR.Converter;

if (args.Length != 2 || args[0] != "provision")
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/ShaderExporter -- provision <new-output-directory>");
    return 2;
}

try
{
    var executable = await ShaderExporter.ProvisionAsync(args[1]);
    Console.WriteLine($"AssetRipper shader exporter ready: {executable}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"AssetRipper provisioning failed: {exception.Message}");
    return 1;
}
