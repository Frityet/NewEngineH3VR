using System.Security.Cryptography;
using AssetsTools.NET;
using H3VR.Converter;
using K4os.Compression.LZ4;

namespace UnityMetalCli;

internal sealed record UnityArchive(string Generation, string Engine, byte[] Hash, byte[] Directory, string ContentSha256)
{
    internal const string PlayerFile = "data.unity3d";
    private const uint DirectoryFlag = 0x40;
    private const uint MetadataAtEnd = 0x80;
    private const uint AlignData = 0x200;
    private const int BlockLimit = 4 * 1024 * 1024;

    internal readonly record struct Block(int Size, int StoredSize, ushort Codec, long Offset);

    internal static bool IsArchive(string path)
    {
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        return input.ReadAtLeast(magic, magic.Length, false) == magic.Length &&
            magic.SequenceEqual("UnityFS\0"u8);
    }

    internal static UnityArchive Read(string path, CancellationToken cancellation, Action<byte[], int>? consume = null)
    {
        using var input = File.OpenRead(path);
        using var reader = new AssetsFileReader(input) { BigEndian = true };
        if (reader.ReadNullTerminated() != "UnityFS") throw Invalid(path);
        uint version = reader.ReadUInt32();
        if (version is not (8 or 9)) throw Invalid(path);
        string generation = reader.ReadNullTerminated();
        string engine = reader.ReadNullTerminated();
        long fileSize = checked((long)reader.ReadUInt64());
        int encodedSize = checked((int)reader.ReadUInt32());
        int metadataSize = checked((int)reader.ReadUInt32());
        uint flags = reader.ReadUInt32();
        long headerEnd = Align16(reader.Position);
        if (fileSize != input.Length || (flags & DirectoryFlag) == 0 ||
            encodedSize <= 0 || metadataSize is < 24 or > 64 * 1024 * 1024 || encodedSize > fileSize - headerEnd)
            throw Invalid(path);

        bool end = (flags & MetadataAtEnd) != 0;
        long metadataStart = end ? fileSize - encodedSize : headerEnd;
        input.Position = metadataStart;
        byte[] encodedMetadata = new byte[encodedSize];
        input.ReadExactly(encodedMetadata);
        byte[] metadata = new byte[metadataSize];
        Decode(encodedMetadata, metadata, flags, path);
        long dataStart = end ? headerEnd : input.Position;
        if ((flags & AlignData) != 0) dataStart = Align16(dataStart);
        long dataEnd = end ? metadataStart : fileSize;

        using var table = new AssetsFileReader(new MemoryStream(metadata, writable: false)) { BigEndian = true };
        byte[] hash = table.ReadBytes(16);
        int count = checked((int)table.ReadUInt32());
        if (count < 1 || count > (metadata.Length - 24) / (version == 9 ? 18 : 10)) throw Invalid(path);
        var blocks = new Block[count];
        long storedEnd = dataStart;
        long contentSize = 0;
        for (int i = 0; i < count; i++)
        {
            int size = checked((int)table.ReadUInt32());
            int stored = checked((int)table.ReadUInt32());
            ushort codec = table.ReadUInt16();
            long physical = version == 9 ? checked(dataStart + (long)table.ReadUInt64()) : storedEnd;
            if (physical < storedEnd) throw Invalid(path);
            storedEnd = checked(physical + stored);
            if (size is <= 0 or > BlockLimit || stored is <= 0 or > BlockLimit || physical < dataStart || storedEnd > dataEnd)
                throw Invalid(path);
            blocks[i] = new Block(size, stored, codec, physical);
            contentSize = checked(contentSize + size);
        }
        byte[] directory = metadata[(int)table.Position..];
        VerifyDirectory(table, metadata.Length, contentSize, path);

        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] encoded = new byte[blocks.Max(chunk => chunk.StoredSize)];
        byte[] plain = new byte[blocks.Max(chunk => chunk.Size)];
        foreach (var block in blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            input.Position = block.Offset;
            input.ReadExactly(encoded.AsSpan(0, block.StoredSize));
            Decode(encoded.AsSpan(0, block.StoredSize), plain.AsSpan(0, block.Size), block.Codec, path);
            contentHash.AppendData(plain, 0, block.Size);
            consume?.Invoke(plain, block.Size);
        }
        return new UnityArchive(generation, engine, hash, directory, Convert.ToHexStringLower(contentHash.GetHashAndReset()));
    }

    internal static UnityArchive ReadLoose(string root, List<string> names, string engine, CancellationToken cancellation,
        Action<byte[], int> consume)
    {
        using var directory = new MemoryStream();
        using var writer = new AssetsFileWriter(directory) { BigEndian = true };
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        writer.Write(names.Count);
        long offset = 0;
        foreach (string name in names)
        {
            using var file = File.OpenRead(Path.Combine(root, name));
            long remaining = file.Length;
            writer.Write(offset);
            writer.Write(remaining);
            writer.Write(name.StartsWith("GI/", StringComparison.Ordinal) || name.EndsWith(".resS", StringComparison.Ordinal) ? 0u : 4u);
            writer.WriteNullTerminated(name);
            offset += remaining;
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int size = (int)Math.Min(buffer.Length, remaining);
                file.ReadExactly(buffer.AsSpan(0, size));
                contentHash.AppendData(buffer, 0, size);
                consume(buffer, size);
                remaining -= size;
            }
        }
        return new UnityArchive("5.x.x", engine, new byte[16], directory.ToArray(), Convert.ToHexStringLower(contentHash.GetHashAndReset()));
    }

    internal void WriteIndex(Stream output, IReadOnlyList<Block> blocks)
    {
        // Version 9 has a physical file offset per block. Logical block order and
        // directory positions still describe the original byte stream.
        using var writer = new AssetsFileWriter(output) { BigEndian = true };
        long end = writer.Position;
        writer.Position = 0;
        writer.WriteNullTerminated("UnityFS");
        writer.Write(9u);
        writer.WriteNullTerminated(Generation);
        writer.WriteNullTerminated(Engine);
        long sizes = writer.Position;
        long dataStart = Align16(sizes + 20);

        using var metadata = new MemoryStream();
        using var table = new AssetsFileWriter(metadata) { BigEndian = true };
        table.Write(Hash);
        table.Write(blocks.Count);
        foreach (var block in blocks)
        {
            table.Write((uint)block.Size);
            table.Write((uint)block.StoredSize);
            table.Write(block.Codec);
            table.Write(checked((ulong)(block.Offset - dataStart)));
        }
        table.Write(Directory);
        byte[] plain = metadata.ToArray();
        byte[] encoded = new byte[LZ4Codec.MaximumOutputSize(plain.Length)];
        int encodedSize = LZ4Codec.Encode(plain, encoded, LZ4Level.L03_HC);
        if (encodedSize <= 0) throw new InvalidDataException("Could not encode the Unity archive directory.");
        writer.Position = sizes;
        writer.Write(end + encodedSize);
        writer.Write(encodedSize);
        writer.Write(plain.Length);
        writer.Write(DirectoryFlag | MetadataAtEnd | AlignData | 3u);
        writer.Position = end;
        writer.Write(encoded, 0, encodedSize);
    }

    private static void VerifyDirectory(AssetsFileReader table, int length, long contentSize, string path)
    {
        int members = checked((int)table.ReadUInt32());
        if (members < 1 || members > (length - table.Position) / 21) throw Invalid(path);
        for (int i = 0; i < members; i++)
        {
            long offset = checked((long)table.ReadUInt64());
            long size = checked((long)table.ReadUInt64());
            table.ReadUInt32();
            if (table.ReadNullTerminated().Length == 0 || offset > contentSize || size > contentSize - offset)
                throw Invalid(path);
        }
        if (table.Position != length) throw Invalid(path);
    }

    private static void Decode(ReadOnlySpan<byte> encoded, Span<byte> plain, uint flags, string path)
    {
        uint codec = flags & 0x3f;
        if (codec == 0 && encoded.Length == plain.Length)
            encoded.CopyTo(plain);
        else if (codec is not (2 or 3) || LZ4Codec.Decode(encoded, plain) != plain.Length)
            throw Invalid(path);
    }

    private static long Align16(long offset) => (offset + 15) & ~15L;
    private static InvalidDataException Invalid(string path) => new("Invalid or unsupported UnityFS archive: " + path);
}
