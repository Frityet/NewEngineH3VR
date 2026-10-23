using System.Buffers.Binary;
using System.Numerics;

namespace H3VR.Converter;

/// <summary>Unity's MonoScript local file IDs are the signed little-endian prefix of MD4("s\0\0\0" + type).</summary>
public static class Md4
{
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var length = input.Length;
        var paddedLength = ((length + 9 + 63) / 64) * 64;
        var data = new byte[paddedLength]; input.CopyTo(data); data[length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(paddedLength - 8), (ulong)length * 8);
        uint[] state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
        Span<uint> x = stackalloc uint[16];
        for (var off = 0; off < data.Length; off += 64)
        {
            for (var i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + i * 4));
            var (a, b, c, d) = (state[0], state[1], state[2], state[3]);
            for (var j = 0; j < 48; j++)
            {
                int k, s; uint f, add;
                if (j < 16) { k = j; s = new[] { 3, 7, 11, 19 }[j % 4]; f = (b & c) | (~b & d); add = 0; }
                else if (j < 32) { var q = j - 16; k = q % 4 * 4 + q / 4; s = new[] { 3, 5, 9, 13 }[j % 4]; f = (b & c) | (b & d) | (c & d); add = 0x5a827999; }
                else { k = new[] { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 }[j - 32]; s = new[] { 3, 9, 11, 15 }[j % 4]; f = b ^ c ^ d; add = 0x6ed9eba1; }
                a = BitOperations.RotateLeft(unchecked(a + f + x[k] + add), s);
                (a, b, c, d) = (d, a, b, c);
            }
            state[0] = unchecked(state[0] + a); state[1] = unchecked(state[1] + b);
            state[2] = unchecked(state[2] + c); state[3] = unchecked(state[3] + d);
        }
        var result = new byte[16]; for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), state[i]);
        return result;
    }
    public static int UnityFileId(string ns, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("s\0\0\0" + ns + name);
        return BinaryPrimitives.ReadInt32LittleEndian(Hash(bytes));
    }
}
