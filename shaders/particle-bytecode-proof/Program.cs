using DXDecompiler;
foreach (var file in Directory.GetFiles(args[0], "*.dxbc")) {
    var code = new BytecodeContainer(File.ReadAllBytes(file));
    File.WriteAllText(Path.ChangeExtension(file, ".asm"), code.ToString());
    Console.WriteLine(Path.GetFileName(file));
}
