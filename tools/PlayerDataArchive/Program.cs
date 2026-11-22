using System.Security.Cryptography;
using AssetsTools.NET;
using H3VR.Converter;
using K4os.Compression.LZ4;

if (args.Length != 3)
    throw new ArgumentException("Supply the unsigned player Data directory, a new scratch directory, and the receipt path.");
PlayerDataArchive.Compact(SafeFiles.Canonical(args[0]), SafeFiles.Canonical(args[1]), Path.GetFullPath(args[2]));

internal static class PlayerDataArchive
{
    private const string ArchiveName = "data.unity3d";
    private const int BlockSize = 128 * 1024;

    internal static void Compact(string data, string scratch, string receipt)
    {
        SafeFiles.RequireSeparate(data, scratch);
        SafeFiles.RequireSeparate(data, receipt);
        SafeFiles.RequireSeparate(scratch, receipt);
        if (File.Exists(receipt) || Directory.Exists(receipt))
            throw new IOException("Choose a new player compaction receipt path.");
        string destination = SafeFiles.Inside(data, ArchiveName);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The player data is already packed: " + destination);
        if (Directory.Exists(scratch) || File.Exists(scratch))
            throw new IOException("Choose a new player compaction scratch directory.");

        var files = PlayerDataFiles.Gather(data);
        string packed = Path.Combine(scratch, ArchiveName);
        Directory.CreateDirectory(scratch);
        var (sourceBytes, hashes) = WriteArchive(data, files, packed);
        long archiveBytes = new FileInfo(packed).Length;
        VerifyArchive(packed, hashes);
        if (archiveBytes >= sourceBytes)
        {
            Directory.Delete(scratch, recursive: true);
            SafeFiles.AtomicJson(receipt, new { schemaVersion = 1, applied = false, sourceBytes, archiveBytes });
            return;
        }

        Install(data, scratch, files, packed, destination);
        Directory.Delete(scratch, recursive: true);
        SafeFiles.AtomicJson(receipt, new
        {
            schemaVersion = 1,
            applied = true,
            sourceBytes,
            archiveBytes,
            archiveSha256 = SafeFiles.Hash(destination),
            files = hashes
        });
        Console.WriteLine($"Packed {files.Count} player files into {ArchiveName}, saving {sourceBytes - archiveBytes:N0} bytes.");
    }

    private static (long Bytes, Dictionary<string, string> Hashes) WriteArchive(
        string data, List<string> files, string destination)
    {
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = 8,
            GenerationVersion = "5.x.x",
            EngineVersion = ConversionConfig.TargetVersion,
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = AssetBundleFSHeaderFlags.LZ4HCCompressed |
                        AssetBundleFSHeaderFlags.HasDirectoryInfo |
                        AssetBundleFSHeaderFlags.BlockAndDirAtEnd |
                        AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart
            }
        };

        // Use 128 KiB LZ4 blocks for the standalone data archive.
        // A file may span blocks, and a block may cover parts of several files.
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new AssetsFileWriter(stream) { BigEndian = true };
        header.Write(writer);
        writer.Align16();

        var metadata = new AssetBundleBlockAndDirInfo { Hash = new Hash128(), DirectoryInfos = [] };
        var blocks = new List<AssetBundleBlockInfo>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] block = new byte[BlockSize];
        byte[] encoded = new byte[LZ4Codec.MaximumOutputSize(BlockSize)];
        int buffered = 0;
        long offset = 0;
        foreach (string relative in files)
        {
            using var input = File.OpenRead(Path.Combine(data, relative));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = input.Length;
            metadata.DirectoryInfos.Add(new AssetBundleDirectoryInfo
            {
                Name = relative,
                Offset = offset,
                DecompressedSize = length,
                Flags = relative.StartsWith("GI/", StringComparison.Ordinal) ||
                    relative.EndsWith(".resS", StringComparison.Ordinal) ? 0u : 4u
            });
            long remaining = length;
            while (remaining != 0)
            {
                int count = (int)Math.Min(remaining, block.Length - buffered);
                var buffer = block.AsSpan(buffered, count);
                input.ReadExactly(buffer);
                hash.AppendData(buffer);
                remaining -= count;
                buffered += count;
                if (buffered == block.Length) WriteBlock();
            }
            if (input.ReadByte() != -1)
                throw new IOException("A player file changed while being packed: " + relative);
            offset = checked(offset + length);
            hashes.Add(relative, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        if (buffered != 0) WriteBlock();
        metadata.BlockInfos = blocks.ToArray();
        WriteMetadata(writer, header, metadata);
        return (offset, hashes);

        void WriteBlock()
        {
            int compressed = LZ4Codec.Encode(block.AsSpan(0, buffered), encoded);
            bool useCompressed = compressed > 0 && compressed < buffered;
            int written = useCompressed ? compressed : buffered;
            writer.Write(useCompressed ? encoded : block, 0, written);
            blocks.Add(new AssetBundleBlockInfo
            {
                DecompressedSize = (uint)buffered,
                CompressedSize = (uint)written,
                Flags = useCompressed ? (ushort)2 : (ushort)0
            });
            buffered = 0;
        }
    }

    private static void WriteMetadata(AssetsFileWriter writer, AssetBundleHeader header, AssetBundleBlockAndDirInfo metadata)
    {
        using var bytes = new MemoryStream();
        using var metadataWriter = new AssetsFileWriter(bytes) { BigEndian = true };
        metadata.Write(metadataWriter);
        byte[] flat = bytes.ToArray();
        byte[] encoded = new byte[LZ4Codec.MaximumOutputSize(flat.Length)];
        int written = LZ4Codec.Encode(flat, encoded, LZ4Level.L03_HC);
        if (written <= 0) throw new InvalidDataException("Failed to encode the player archive's directory.");
        writer.Write(encoded, 0, written);
        header.FileStreamHeader.TotalFileSize = writer.Position;
        header.FileStreamHeader.CompressedSize = (uint)written;
        header.FileStreamHeader.DecompressedSize = (uint)flat.Length;
        writer.Position = 0;
        header.Write(writer);
    }

    private static void VerifyArchive(string path, Dictionary<string, string> expected)
    {
        using var reader = new AssetsFileReader(path);
        var bundle = new AssetBundleFile();
        bundle.Read(reader);
        var entries = bundle.BlockAndDirInfo.DirectoryInfos;
        if (entries.Count != expected.Count || !entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
            throw new InvalidDataException("Unexpected player archive inventory.");
        byte[] buffer = new byte[2 * BlockSize];
        foreach (var entry in entries)
        {
            bundle.DataReader.Position = entry.Offset;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = entry.DecompressedSize;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, buffer.Length);
                var data = buffer.AsSpan(0, count);
                bundle.DataReader.BaseStream.ReadExactly(data);
                hash.AppendData(data);
                remaining -= count;
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected[entry.Name])
                throw new InvalidDataException("Player archive roundtrip differed: " + entry.Name);
        }
    }

    private static void Install(string data, string scratch, List<string> files, string packed, string destination)
    {
        string backup = Path.Combine(scratch, "loose");
        var moved = new List<(string Loose, string Backup)>(files.Count);
        try
        {
            foreach (string relative in files)
            {
                string loose = Path.Combine(data, relative);
                string saved = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Move(loose, saved);
                moved.Add((loose, saved));
            }
            File.Move(packed, destination);
        }
        catch
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                File.Move(moved[i].Backup, moved[i].Loose);
            throw;
        }
    }
}
