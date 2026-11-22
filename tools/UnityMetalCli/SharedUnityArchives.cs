using System.Runtime.InteropServices;
using System.Security.Cryptography;
using H3VR.Converter;
using K4os.Compression.LZ4;

namespace UnityMetalCli;

internal static class SharedUnityArchives
{
    private const int MinimumChunk = 32 * 1024;
    private const int MaximumChunk = 128 * 1024;
    private const int PageSize = 4096;

    private sealed record Group(string Relative, string[] Data, string? Engine = null);
    private sealed record Input(string Path, UnityArchive Archive, List<Chunk> Blocks, List<string>? Loose)
    {
        internal List<Slot> Slots { get; } = new();
    }

    internal static async Task<long> RepackAsync(string[] data, string engine, string scratch, CancellationToken cancellation)
    {
        IEnumerable<Group> groups = Directory.EnumerateFiles(Path.Combine(data[0], "StreamingAssets"))
            .Where(file => new FileInfo(file).Length >= 512 * 1024)
            .Select(file => Path.Combine("StreamingAssets", Path.GetFileName(file)))
            .Where(relative => data.All(root => UnityArchive.IsArchive(Path.Combine(root, relative))))
            .Select(relative => new Group(relative, data));
        groups = groups.Prepend(new Group(UnityArchive.PlayerFile, data, engine));

        long savings = 0;
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation },
            (group, token) =>
            {
                long saved = RepackGroup(group, Path.Combine(scratch, Path.GetFileName(group.Relative)), token);
                Interlocked.Add(ref savings, saved);
                return ValueTask.CompletedTask;
            });
        return savings;
    }

    private static long RepackGroup(Group group, string scratch, CancellationToken cancellation)
    {
        Directory.CreateDirectory(scratch);
        try
        {
            using var cache = new FileStream(Path.Combine(scratch, "chunks"), FileMode.CreateNew, FileAccess.ReadWrite);
            var chunks = new ChunkStore(cache);
            var inputs = new List<Input>(group.Data.Length);
            long before = 0;
            foreach (string data in group.Data)
            {
                string path = Path.Combine(data, group.Relative);
                var blocks = new List<Chunk>();
                var chunker = new Chunker(piece => blocks.Add(chunks.Add(piece)));
                List<string>? loose = null;
                UnityArchive archive;
                if (group.Engine is { } unity && !File.Exists(path))
                {
                    var names = PlayerDataFiles.Gather(data);
                    archive = UnityArchive.ReadLoose(data, names, unity, cancellation, chunker.Append);
                    loose = names.Select(name => Path.Combine(data, name)).ToList();
                    before += loose.Sum(file => new FileInfo(file).Length);
                }
                else
                {
                    archive = UnityArchive.Read(path, cancellation, chunker.Append);
                    before += new FileInfo(path).Length;
                }
                chunker.Finish();
                inputs.Add(new(path, archive, blocks, loose));
            }

            // Align the closest pair first. Their shared order gives the original build
            // a reference that includes both newer renderers' unique chunks.
            var layout = new List<Slot>();
            foreach (var input in inputs.Skip(1).Append(inputs[0]))
                layout = MergeLayout(layout, input);
            if (!layout.Any(slot => slot.Shared)) return 0;

            string template = Path.Combine(scratch, "common");
            long layoutEnd = PageSize;
            using (var common = new FileStream(template, FileMode.CreateNew, FileAccess.Write))
            {
                bool previousShared = false;
                foreach (var slot in layout)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Separate shared runs from writes into the sparse areas in each clone.
                    int alignment = slot.Shared == previousShared ? 16 : PageSize;
                    slot.Offset = Align(layoutEnd, alignment);
                    layoutEnd = slot.Offset + slot.Chunk.StoredSize;
                    previousShared = slot.Shared;
                    if (!slot.Shared) continue;
                    common.Position = slot.Offset;
                    chunks.Copy(slot.Chunk, common);
                }
                common.SetLength(layoutEnd);
            }

            long after = Align(layoutEnd, PageSize) - PageSize;
            var staged = new List<string>(inputs.Count);
            foreach (var input in inputs)
            {
                string packed = Path.Combine(scratch, "archive-" + staged.Count);
                if (CloneFile(template, packed, 0) != 0)
                    throw new IOException("Sharing Unity archives requires APFS file cloning on the output volume.");
                WriteProfile(input, packed, chunks, layoutEnd, cancellation);
                after += PageSize + Align(new FileInfo(packed).Length - layoutEnd, PageSize);
                staged.Add(packed);
            }
            if (after >= before) return 0;

            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var verify = UnityArchive.Read(staged[i], cancellation);
                if (verify.ContentSha256 != input.Archive.ContentSha256 ||
                    !verify.Directory.AsSpan().SequenceEqual(input.Archive.Directory) ||
                    !verify.Hash.AsSpan().SequenceEqual(input.Archive.Hash))
                    throw new InvalidDataException("Repacked Unity archive did not match its source: " + input.Path);
            }
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                File.Move(staged[i], input.Path, overwrite: true);
                if (input.Loose is not null)
                    foreach (var file in input.Loose) File.Delete(file);
            }
            Console.WriteLine($"Shared {Path.GetFileName(group.Relative)} across {inputs.Count} renderers, saving {before - after:N0} bytes.");
            return before - after;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static List<Slot> MergeLayout(List<Slot> layout, Input input)
    {
        // Unity requires compressed block offsets to increase in logical order. Find an
        // ordered set of matching occurrences, and allocate gaps for unmatched chunks.
        var index = new Dictionary<Chunk, int>();
        for (int i = 0; i < layout.Count; i++)
            if (!index.TryAdd(layout[i].Chunk, i)) index[layout[i].Chunk] = -1;

        int[] longest = new int[input.Blocks.Count];
        int[] inputs = new int[longest.Length];
        int[] predecessors = new int[longest.Length];
        int count = 0;
        for (int i = 0; i < input.Blocks.Count; i++)
        {
            if (!index.TryGetValue(input.Blocks[i], out int slot) || slot < 0) continue;
            int depth = Array.BinarySearch(longest, 0, count, slot);
            if (depth < 0) depth = ~depth;
            longest[depth] = slot;
            inputs[depth] = i;
            predecessors[i] = depth == 0 ? -1 : inputs[depth - 1];
            if (depth == count) count++;
        }

        var anchors = new Stack<int>(count + 1);
        anchors.Push(input.Blocks.Count);
        if (count > 0)
            for (int i = inputs[count - 1]; i >= 0; i = predecessors[i]) anchors.Push(i);

        var merged = new List<Slot>(layout.Count + input.Blocks.Count - count);
        int left = 0;
        int right = 0;
        foreach (int anchor in anchors)
        {
            bool match = anchor < input.Blocks.Count;
            int stop = match ? index[input.Blocks[anchor]] : layout.Count;
            while (left < stop) merged.Add(layout[left++]);
            while (right < anchor)
            {
                var slot = new Slot(input.Blocks[right++]);
                merged.Add(slot);
                input.Slots.Add(slot);
            }
            if (match)
            {
                var slot = layout[left++];
                slot.Shared = true;
                merged.Add(slot);
                input.Slots.Add(slot);
                right++;
            }
        }
        return merged;
    }

    private static void WriteProfile(Input input, string path, ChunkStore chunks, long layoutEnd, CancellationToken cancellation)
    {
        using var output = new FileStream(path, FileMode.Open, FileAccess.Write);
        var blocks = new List<UnityArchive.Block>(input.Slots.Count);
        foreach (var slot in input.Slots)
        {
            cancellation.ThrowIfCancellationRequested();
            var chunk = slot.Chunk;
            if (!slot.Shared)
            {
                output.Position = slot.Offset;
                chunks.Copy(chunk, output);
            }
            blocks.Add(new(chunk.Size, chunk.StoredSize, chunk.Codec, slot.Offset));
        }
        output.Position = layoutEnd;
        input.Archive.WriteIndex(output, blocks);
    }

    private static long Align(long offset, int alignment) => (offset + alignment - 1) & ~(alignment - 1L);

    [DllImport("libc", EntryPoint = "clonefile")]
    private static extern int CloneFile(string source, string destination, int flags);

    private sealed class Chunk(int size, int storedSize, ushort codec, long offset)
    {
        internal int Size { get; } = size;
        internal int StoredSize { get; } = storedSize;
        internal ushort Codec { get; } = codec;
        internal long Offset { get; } = offset;
    }

    private sealed class Slot(Chunk chunk)
    {
        internal Chunk Chunk { get; } = chunk;
        internal bool Shared { get; set; }
        internal long Offset { get; set; }
    }

    private readonly record struct Checksum(ulong A, ulong B, ulong C, ulong D);
    private delegate void ChunkConsumer(ReadOnlySpan<byte> chunk);

    private sealed class ChunkStore(FileStream spool)
    {
        private readonly Dictionary<Checksum, Chunk> entries = new();
        private readonly byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(MaximumChunk)];
        private long end;

        internal Chunk Add(ReadOnlySpan<byte> piece)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(piece, hash);
            var checksum = MemoryMarshal.Read<Checksum>(hash);
            if (!entries.TryGetValue(checksum, out var chunk))
            {
                int encoded = LZ4Codec.Encode(piece, compressed);
                bool useCompressed = encoded > 0 && encoded < piece.Length;
                var payload = useCompressed ? compressed.AsSpan(0, encoded) : piece;
                spool.Position = end;
                spool.Write(payload);
                chunk = new(piece.Length, payload.Length, useCompressed ? (ushort)2 : (ushort)0, end);
                entries.Add(checksum, chunk);
                end += payload.Length;
            }
            return chunk;
        }

        internal void Copy(Chunk chunk, Stream output)
        {
            var payload = compressed.AsSpan(0, chunk.StoredSize);
            spool.Position = chunk.Offset;
            spool.ReadExactly(payload);
            output.Write(payload);
        }
    }

    private sealed class Chunker(ChunkConsumer consume)
    {
        private static readonly ulong[] Weights = Enumerable.Range(0, 256).Select(index =>
        {
            ulong value = ((ulong)index + 1) * 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }).ToArray();

        private readonly byte[] pending = new byte[MaximumChunk];
        private int size;
        private ulong rolling;

        internal void Append(byte[] bytes, int length)
        {
            // Content-defined boundaries find the same bytes again after an asset shifts
            // position in a different build. Limit block size for Unity's streaming reads.
            var source = bytes.AsSpan(0, length);
            while (!source.IsEmpty)
            {
                int end = Math.Min(source.Length, MaximumChunk - size);
                int take = Math.Min(end, Math.Max(0, MinimumChunk - size));
                bool boundary = false;
                while (take < end)
                {
                    rolling = (rolling << 1) + Weights[source[take++]];
                    boundary = (rolling & 0x7fff) == 0;
                    if (boundary) break;
                }
                source[..take].CopyTo(pending.AsSpan(size));
                size += take;
                source = source[take..];
                if (boundary || size == MaximumChunk) Finish();
            }
        }

        internal void Finish()
        {
            if (size == 0) return;
            consume(pending.AsSpan(0, size));
            size = 0;
            rolling = 0;
        }
    }
}
