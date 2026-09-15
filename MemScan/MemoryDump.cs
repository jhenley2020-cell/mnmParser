using System.Text;

namespace MnmDamageParser.MemScan;

/// <summary>Formats a byte range as hex+ASCII, one row per 16 bytes, plus a
/// second interpreted row showing each 8-byte word as a possible pointer,
/// int64/int32/float -- eyeballing candidate offsets (a plausible HP int,
/// a moving-position float triple) is the whole point of "dump".</summary>
public static class MemoryDump
{
    public static string Format(long baseAddress, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var chunk = data.Slice(offset, Math.Min(16, data.Length - offset));
            var addr = baseAddress + offset;
            sb.Append($"0x{addr:X}  ");

            for (var i = 0; i < 16; i++)
            {
                if (i < chunk.Length) sb.Append(chunk[i].ToString("x2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            foreach (var b in chunk)
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.Append('|');
            sb.AppendLine();

            // Every 4-byte-aligned offset gets an i32/f32 reading -- NOT just
            // the lower half of each 8-byte word, which silently hid
            // whatever sat in the upper 4 bytes of every word. 8-byte-aligned
            // offsets additionally get i64/f64 and a pointer-shaped check.
            for (var i = 0; i + 4 <= chunk.Length; i += 4)
            {
                var word4 = chunk.Slice(i, 4);
                var i32 = BitConverter.ToInt32(word4);
                var f32 = BitConverter.ToSingle(word4);
                sb.Append($"    +0x{i:x2} (abs 0x{addr + i:X}): i32={i32,-12} f32={f32,-14:0.###}");

                if (i % 8 == 0 && i + 8 <= chunk.Length)
                {
                    var word8 = chunk.Slice(i, 8);
                    var i64 = BitConverter.ToInt64(word8);
                    var f64 = BitConverter.ToDouble(word8);
                    var looksLikePointer = i64 is > 0x10000 and < 0x7FFFFFFFFFFF;
                    sb.Append($"  i64={i64,-20} f64={f64,-14:0.###}{(looksLikePointer ? "  (looks like a pointer)" : "")}");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
