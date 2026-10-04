using System.Text;
using RmdblobUnpacker.Core;

namespace ControlResonantFontTool;

// Standalone base-TOC patch. The original blobs are never overwritten or truncated.
public static class FontPatch
{
    static void U(byte[] b, int o, uint v) => BitConverter.GetBytes(v).CopyTo(b, o);
    static void Q(byte[] b, int o, ulong v) => BitConverter.GetBytes(v).CopyTo(b, o);
    static uint Read(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    public static ResourceEntry[] Targets(TocFile toc) => toc.Entries.Where(e =>
        e.Path.Contains("fonts/", StringComparison.OrdinalIgnoreCase) &&
        (e.Name.Equals("notosanssc-regular.ttf", StringComparison.OrdinalIgnoreCase) ||
         e.Name.Equals("notosanssc-black.ttf", StringComparison.OrdinalIgnoreCase))).ToArray();

    public static byte[] Build(TocFile toc, byte[] font, string blobName, string blobPath)
    {
        var entries = Targets(toc);
        if (entries.Length != 2 || toc.BlobCount >= 65535) throw new InvalidDataException("未找到预期的两个简体中文字体槽位。");
        byte[] path = Encoding.UTF8.GetBytes("../pc/" + blobName);
        int delta = (path.Length + 7) & ~7;
        int insert = toc.BlobCount * 24, poolEnd = toc.StrEnd + 24;
        byte[] body = new byte[checked(toc.Body.Length + 24 + delta + entries.Length * 16)];
        toc.Body.AsSpan(0, insert).CopyTo(body);
        toc.Body.AsSpan(insert, toc.StrEnd - insert).CopyTo(body.AsSpan(insert + 24));
        path.CopyTo(body, poolEnd);
        toc.Body.AsSpan(toc.StrEnd).CopyTo(body.AsSpan(poolEnd + delta));
        toc.Body.AsSpan(entries[0].Chunks[0].BlobIdx * 24, 24).CopyTo(body.AsSpan(insert));
        U(body, insert, (uint)(toc.StrEnd - toc.StrStart)); U(body, insert + 4, (uint)path.Length);
        int info = toc.InfoStart + 24, records = toc.EntOff + 24 + delta, idx = toc.IdxOff + 24 + delta;
        byte[] header = (byte[])toc.Header.Clone();
        U(header, 20, (uint)(toc.BlobCount + 1)); U(header, 24, (uint)((toc.BlobCount + 1) * 24));
        U(header, 32, (uint)info); U(header, 40, (uint)(toc.StrStart + 24));
        U(header, 44, (uint)(toc.StrEnd - toc.StrStart + delta)); U(header, 48, (uint)(poolEnd + delta));
        U(header, 56, (uint)records); U(header, 80, (uint)idx); U(header, 84, (uint)(toc.IdxSize + entries.Length * 16));
        using (var blob = new FileStream(blobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Both slots can safely share the same immutable font payload.
            byte[] encoded = Literal(font); blob.Write(encoded); blob.Flush(true);
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i]; int desc = toc.IdxSize + i * 16, io = info + e.EntryIndex * 32;
                U(body, io, (uint)desc); U(body, io + 4, 16); U(body, io + 20, (uint)font.Length);
                Q(body, idx + desc, ((ulong)toc.BlobCount << 8) | 0x10);
                U(body, idx + desc + 8, (uint)font.Length); U(body, idx + desc + 12, (uint)encoded.Length);
                int record = records + e.RecOff, metadata = checked(8 + (int)Read(body, record + 4) * 8);
                if (metadata + 16 > e.RecLen || Read(body, record + metadata) != 1 ||
                    BitConverter.ToUInt64(body, record + metadata + 4) != (ulong)e.Size)
                    throw new InvalidDataException("不支持的字体元数据结构。");
                Q(body, record + metadata + 4, (ulong)font.Length); U(body, record + metadata + 12, Crc32.Compute(font));
            }
            Q(body, insert + 16, (ulong)encoded.Length);
        }
        return Rebuild(header, body);
    }

    public static byte[] Literal(byte[] data)
    {
        using var s = new MemoryStream(); s.WriteByte((byte)(Math.Min(15, data.Length) << 4));
        if (data.Length >= 15) { int n = data.Length - 15; while (n >= 255) { s.WriteByte(255); n -= 255; } s.WriteByte((byte)n); }
        s.Write(data); return s.ToArray();
    }
    static byte[] Rebuild(byte[] header, byte[] body)
    {
        int count = checked((int)Read(header, 12) / 16), position = 4096, offset = 0;
        if (count < 1 || 96 + count * 16 > 4096) throw new InvalidDataException("无效 TOC 块表。");
        var blocks = new List<(int Start, int Size, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            int size = i == count - 1 ? body.Length - offset : checked((int)Read(header, 96 + i * 16));
            var data = Literal(body.AsSpan(offset, size).ToArray()); blocks.Add((position, size, data));
            offset += size; position = checked((position + data.Length + 15) & ~15);
        }
        byte[] result = new byte[blocks[^1].Start + blocks[^1].Data.Length];
        for (int i = 0; i < count; i++)
        {
            int o = 96 + i * 16; var b = blocks[i]; uint next = i + 1 < count ? (uint)blocks[i + 1].Start : 0;
            U(header, o, (uint)b.Size); U(header, o + 4, (uint)b.Data.Length);
            U(header, o + 8, (Read(header, o + 8) & 0xFFFFFF) | ((next & 255) << 24)); U(header, o + 12, next >> 8);
            b.Data.CopyTo(result, b.Start);
        }
        header.CopyTo(result, 0); return result;
    }
}
