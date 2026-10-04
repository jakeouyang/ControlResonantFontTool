using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RmdblobUnpacker.Core;

namespace ControlResonantFontTool;

public sealed class PatchState
{
    public string OriginalHash { get; set; }
    public string PatchedHash { get; set; }
    public string BlobName { get; set; }
    public string BlobHash { get; set; }
}

public static class FontService
{
    public const string TocName = "base-generic.rmdtoc";
    const string BackupName = "_fonttool_backup";
    static string BackupDirectory(string pc)
    {
        string current = Path.Combine(pc, BackupName);
        string previous = Path.Combine(pc, "_fonttool_csharp_backup");
        if (Directory.Exists(previous))
        {
            if (Directory.Exists(current))
                throw new IOException("检测到多份备份目录，无法确定应使用哪一份。请保留所有备份并检查后再操作。");
            if (File.Exists(Path.Combine(previous, "state.json"))) ReadState(previous);
            Directory.Move(previous, current);
        }
        return current;
    }
    public static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static string FindPc(string root)
    {
        root = Path.GetFullPath(root.Trim().Trim('"'));
        foreach (string p in new[] { root, Path.Combine(root, "data_pack2", "pc"), Path.Combine(root, "pc") })
            if (File.Exists(Path.Combine(p, TocName))) return p;
        throw new DirectoryNotFoundException("请选择包含 data_pack2/pc 的游戏目录。");
    }
    static void GameClosed()
    {
        var processes = Process.GetProcessesByName("CONTROLResonant");
        try { if (processes.Length > 0) throw new IOException("请先完全退出游戏。"); }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    public static void Verify(TocFile toc)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(toc.TocPath), "..")) + Path.DirectorySeparatorChar;
        for (int i = 0; i < toc.BlobCount; i++)
        {
            string p = toc.BlobFiles[i];
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源路径越界。");
            ulong size = BitConverter.ToUInt64(toc.Body, i * 24 + 16);
            if (!File.Exists(p) || (ulong)new FileInfo(p).Length != size) throw new InvalidDataException("资源缺失或版本不一致：" + p);
        }
        if (FontPatch.Targets(toc).Length != 2) throw new InvalidDataException("没有找到两个预期的简中字体槽位。");
        foreach (var e in FontPatch.Targets(toc))
        {
            var (data, raw) = toc.Extract(e);
            if (raw || Crc32.Compute(data) != e.Crc32) throw new InvalidDataException("原字体 CRC 校验失败：" + e.Path);
        }
    }
    public static void ValidateFont(byte[] data)
    {
        uint U(int o) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o));
        if (data.Length < 12 || data.Length > 128 * 1024 * 1024 || (U(0) != 0x10000 && U(0) != 0x4F54544F))
            throw new InvalidDataException("请选择有效的独立 TTF/OTF 字体（不支持 TTC 字体集合）。");
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        if (count == 0 || 12L + count * 16L > data.Length) throw new InvalidDataException("字体表目录损坏。");
        var tags = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            int o = 12 + i * 16; string tag = Encoding.ASCII.GetString(data, o, 4); uint start = U(o + 8), length = U(o + 12);
            if (!tags.Add(tag) || start < 12 + count * 16 || (ulong)start + length > (ulong)data.Length)
                throw new InvalidDataException("字体表越界或重复。");
            if (tag == "head" && (length < 54 || U((int)start + 12) != 0x5F0F3CF5)) throw new InvalidDataException("字体 head 表损坏。");
        }
        foreach (string tag in new[] { "head", "maxp", "cmap", "name", "hhea", "hmtx" })
            if (!tags.Contains(tag)) throw new InvalidDataException("字体缺少必要的表：" + tag);
        if (U(0) == 0x10000 ? !(tags.Contains("glyf") && tags.Contains("loca")) : !tags.Contains("CFF "))
            throw new InvalidDataException("不支持的字体轮廓格式。");
        using var fonts = new System.Drawing.Text.PrivateFontCollection();
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { fonts.AddMemoryFont(pin.AddrOfPinnedObject(), data.Length); if (fonts.Families.Length == 0) throw new InvalidDataException("Windows 无法识别此字体。"); }
        finally { pin.Free(); }
    }
    static void AtomicWrite(string path, byte[] data)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(data); s.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static PatchState ReadState(string backup)
    {
        var state = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(Path.Combine(backup, "state.json"))) ?? throw new InvalidDataException("备份状态损坏。");
        if (state.BlobName == null || Path.GetFileName(state.BlobName) != state.BlobName ||
            !System.Text.RegularExpressions.Regex.IsMatch(state.BlobName, "^fonttool-[0-9a-f]{32}\\.rmdblob$"))
            throw new InvalidDataException("备份文件名不合法。");
        if (Hash(Path.Combine(backup, TocName)) != state.OriginalHash) throw new InvalidDataException("备份 TOC 校验失败。");
        return state;
    }
    public static void Replace(string root, string fontFile, Action<string> log)
    {
        GameClosed(); string pc = FindPc(root), tocPath = Path.Combine(pc, TocName);
        using var operationLock = new FileStream(Path.Combine(pc, ".fonttool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string backup = BackupDirectory(pc);
        if (new FileInfo(fontFile).Length > 128 * 1024 * 1024) throw new InvalidDataException("字体超过 128 MB。");
        byte[] font = File.ReadAllBytes(fontFile); ValidateFont(font); log("字体结构校验通过。");
        foreach (string path in Directory.EnumerateFiles(pc, "*-development.rmdtoc"))
            if (FontPatch.Targets(TocFile.Load(path)).Length > 0)
                throw new IOException("发现包含简中字体的开发包，可能覆盖本工具的替换。请先停用该字体 MOD：" + Path.GetFileName(path));
        if (File.Exists(Path.Combine(backup, "state.json"))) RestoreCore(pc, backup, log);
        var toc = TocFile.Load(tocPath); Verify(toc);
        string originalHash = Hash(tocPath);
        string legacy = Path.Combine(pc, "_fontmod_backup", TocName + ".bak");
        if (File.Exists(legacy) && Hash(legacy) != originalHash)
            throw new IOException("当前游戏文件与已有历史备份不一致，无法安全替换。请先恢复此前的修改；若游戏已更新，请验证游戏文件，并将历史备份移到游戏目录外保留后再操作。");
        Directory.CreateDirectory(backup);
        string original = Path.Combine(backup, TocName);
        if (File.Exists(original) && Hash(original) != originalHash)
        {
            ArchiveSupersededBackup(pc, backup, originalHash, log);
            Directory.CreateDirectory(backup);
        }
        if (!File.Exists(original)) { File.Copy(tocPath, original); if (Hash(original) != originalHash) throw new IOException("备份校验失败。"); }
        log("已备份基础索引，正在生成字体资源。");
        string name = "fonttool-" + Guid.NewGuid().ToString("N") + ".rmdblob", blob = Path.Combine(pc, name);
        string candidate = Path.Combine(pc, ".fonttool-" + Guid.NewGuid().ToString("N") + ".rmdtoc");
        bool journaled = false;
        try
        {
            byte[] patched = FontPatch.Build(toc, font, name, blob); File.WriteAllBytes(candidate, patched);
            var check = TocFile.Load(candidate); Verify(check);
            foreach (var e in FontPatch.Targets(check))
                if (!check.Extract(e).data.AsSpan().SequenceEqual(font)) throw new InvalidDataException("字体回读不一致。");
            var changed = FontPatch.Targets(toc).Select(e => e.EntryIndex).ToHashSet();
            var byIndex = check.Entries.ToDictionary(e => e.EntryIndex);
            if (check.Entries.Count != toc.Entries.Count) throw new InvalidDataException("资源数量改变。");
            foreach (var e in toc.Entries)
            {
                var next = byIndex[e.EntryIndex];
                if (e.Path != next.Path || (!changed.Contains(e.EntryIndex) &&
                    (e.Size != next.Size || e.Crc32 != next.Crc32 || !e.Chunks.SequenceEqual(next.Chunks))))
                    throw new InvalidDataException("非目标资源改变。");
            }
            var state = new PatchState { OriginalHash = originalHash, PatchedHash = Hash(patched), BlobName = name, BlobHash = Hash(blob) };
            AtomicWrite(Path.Combine(backup, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state)); journaled = true;
            GameClosed();
            if (Hash(tocPath) != originalHash) throw new IOException("操作期间游戏索引已改变，已中止。");
            AtomicWrite(tocPath, patched);
            log("替换完成：两个简中字体槽位已更新。原始 blob 未改动；可随时还原。");
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            if (!journaled && File.Exists(blob)) File.Delete(blob);
        }
    }
    public static void Restore(string root, Action<string> log)
    {
        GameClosed(); string pc = FindPc(root);
        using var operationLock = new FileStream(Path.Combine(pc, ".fonttool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RestoreCore(pc, BackupDirectory(pc), log);
    }
    static void RestoreCore(string pc, string backup, Action<string> log)
    {
        if (!File.Exists(Path.Combine(backup, "state.json"))) throw new IOException("没有可用的替换记录，无法还原。请确认已在当前游戏目录执行过替换，并保留了完整备份。");
        var state = ReadState(backup); string tocPath = Path.Combine(pc, TocName), current = Hash(tocPath);
        if (current != state.OriginalHash && current != state.PatchedHash)
        {
            ArchiveSupersededBackup(pc, backup, current, log);
            log("当前索引已不再引用本工具的字体补丁，无需还原；已保留当前游戏文件。");
            return;
        }
        // Resolve the original TOC's relative blob paths from pc, not the backup directory.
        string verify = Path.Combine(pc, ".fonttool-restore-" + Guid.NewGuid().ToString("N") + ".rmdtoc");
        try { File.Copy(Path.Combine(backup, TocName), verify); Verify(TocFile.Load(verify)); }
        finally { if (File.Exists(verify)) File.Delete(verify); }
        string blob = Path.Combine(pc, state.BlobName);
        if (File.Exists(blob) && Hash(blob) != state.BlobHash) throw new IOException("字体资源被外部修改，拒绝自动删除。");
        GameClosed();
        if (Hash(tocPath) != current) throw new IOException("还原期间索引已改变。");
        AtomicWrite(tocPath, File.ReadAllBytes(Path.Combine(backup, TocName)));
        if (File.Exists(blob)) File.Delete(blob);
        log("已还原替换前的基础索引；原始资源和备份保留。");
    }

    static void ArchiveSupersededBackup(string pc, string backup, string currentHash, Action<string> log)
    {
        string tocPath = Path.Combine(pc, TocName);
        var toc = TocFile.Load(tocPath);
        // A changed index is not proof of an official update. Only adopt a valid
        // current baseline once all references to our previous patches are gone.
        if (toc.BlobFiles.Any(p => Path.GetFileName(p).StartsWith("fonttool-", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("游戏索引已改变，但仍引用本工具的字体资源，无法安全切换备份。请先验证游戏文件后重试。");
        Verify(toc);
        GameClosed();
        if (Hash(tocPath) != currentHash) throw new IOException("检查期间游戏索引已改变，已中止。");
        string archive = backup + ".archived-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        Directory.Move(backup, archive);
        // Keep old blobs too: other indexes may still reference them.
        log("检测到游戏索引版本变化，当前资源校验通过。旧备份已归档：" + archive);
    }
}
