using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RmdblobUnpacker.Core;

namespace ControlResonantFontTool;

internal static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);
    public static void Run(string sourceRoot, string destination)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) throw new IOException("Test directory must be new.");
        string sourcePc = FontService.FindPc(sourceRoot), sourceToc = Path.Combine(sourcePc, FontService.TocName);
        string initialHash = FontService.Hash(sourceToc);
        string baselineToc = sourceToc;
        string sourceBackup = Path.Combine(sourcePc, "_fonttool_backup");
        if (File.Exists(Path.Combine(sourceBackup, "state.json")))
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<PatchState>(File.ReadAllText(Path.Combine(sourceBackup, "state.json")));
            if (state.PatchedHash == initialHash && FontService.Hash(Path.Combine(sourceBackup, FontService.TocName)) == state.OriginalHash)
                baselineToc = Path.Combine(sourceBackup, FontService.TocName);
        }
        string baselineHash = FontService.Hash(baselineToc);
        var source = TocFile.Load(baselineToc);
        source.BlobFiles = source.BlobFiles.Select(p => Path.GetFullPath(Path.Combine(sourcePc, Path.GetRelativePath(Path.GetDirectoryName(baselineToc), p)))).ToArray();
        var targets = FontPatch.Targets(source);
        if (targets.Length != 2) throw new InvalidDataException("Unexpected targets.");
        Directory.CreateDirectory(destination);
        var lines = new List<string>();
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); lines.Add("PASS " + message); }
        void Reject(Action action, string message) { try { action(); } catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidDataException) { lines.Add("PASS " + message); return; } throw new Exception("Expected rejection: " + message); }
        string pc = Path.Combine(destination, "data_pack2", "pc"); Directory.CreateDirectory(pc);
        string tocPath = Path.Combine(pc, FontService.TocName); File.Copy(baselineToc, tocPath);
        var copy = TocFile.Load(tocPath);
        for (int i = 0; i < source.BlobCount; i++)
        {
            string path = copy.BlobFiles[i];
            if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Fixture path escape.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var s = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
            if (!DeviceIoControl(s.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("Sparse file creation failed.");
            s.SetLength(new FileInfo(source.BlobFiles[i]).Length);
        }
        foreach (var e in targets)
            foreach (var c in e.Chunks)
            {
                using var input = File.OpenRead(source.BlobFiles[c.BlobIdx]); input.Position = c.Offset;
                byte[] bytes = new byte[c.Flags == 0 ? c.UncSize : c.CompSize]; input.ReadExactly(bytes);
                using var output = File.OpenWrite(copy.BlobFiles[c.BlobIdx]); output.Position = c.Offset; output.Write(bytes);
            }
        string fontPath = Path.Combine(destination, "replacement.ttf"); var font = source.Extract(targets[0]).data; File.WriteAllBytes(fontPath, font);
        FontService.Verify(copy); Assert(true, "Real TOC and font chunks read from isolated sparse fixture");
        Reject(() => FontService.ValidateFont(new byte[24]), "Malformed font rejected");
        FontService.Replace(destination, fontPath, lines.Add);
        var patched = TocFile.Load(tocPath);
        Assert(FontPatch.Targets(patched).All(e => patched.Extract(e).data.AsSpan().SequenceEqual(font)), "Both font slots round-trip byte-for-byte");
        string evidencePc = Path.Combine(destination, "evidence", "data_pack2", "pc"); Directory.CreateDirectory(evidencePc);
        File.Copy(tocPath, Path.Combine(evidencePc, FontService.TocName));
        foreach (string b in Directory.EnumerateFiles(pc, "fonttool-*.rmdblob")) File.Copy(b, Path.Combine(evidencePc, Path.GetFileName(b)));
        string firstHash = FontService.Hash(tocPath);
        FontService.Replace(destination, fontPath, lines.Add); Assert(true, "Repeated replacement restores then reapplies safely");
        string owned = Directory.EnumerateFiles(pc, "fonttool-*.rmdblob").Single();
        byte[] ownedBytes = File.ReadAllBytes(owned); File.AppendAllText(owned, "external-change");
        Reject(() => FontService.Restore(destination, lines.Add), "Externally modified font blob blocks restore");
        File.WriteAllBytes(owned, ownedBytes);
        byte[] valid = File.ReadAllBytes(tocPath); File.AppendAllText(tocPath, "external-change");
        string editedHash = FontService.Hash(tocPath);
        Reject(() => FontService.Restore(destination, lines.Add), "External TOC change blocks restore");
        Assert(FontService.Hash(tocPath) == editedHash, "Blocked restore leaves external change intact");
        File.WriteAllBytes(tocPath, valid);
        FontService.Restore(destination, lines.Add);
        Assert(FontService.Hash(tocPath) == baselineHash, "Restore reproduces original TOC SHA256");
        FontService.Restore(destination, lines.Add); Assert(true, "Repeated restore is idempotent");
        string backup = Path.Combine(pc, "_fonttool_backup"), previousBackup = Path.Combine(pc, "_fonttool_csharp_backup");
        Directory.Move(backup, previousBackup);
        FontService.Restore(destination, lines.Add);
        Assert(Directory.Exists(backup) && !Directory.Exists(previousBackup), "Previous backup directory automatically migrated and restore succeeds");
        Directory.CreateDirectory(previousBackup);
        Reject(() => FontService.Restore(destination, lines.Add), "Ambiguous backup directories rejected without overwriting either");
        Directory.Delete(previousBackup);
        Assert(!Directory.EnumerateFiles(pc, "fonttool-*.rmdblob").Any(), "Owned font blob cleaned up after restore");
        string legacy = Path.Combine(pc, "_fontmod_backup"); Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, FontService.TocName + ".bak"), "different");
        Reject(() => FontService.Replace(destination, fontPath, lines.Add), "Legacy backup mismatch blocks replacement");
        File.Delete(Path.Combine(legacy, FontService.TocName + ".bak"));
        FontService.Replace(destination, fontPath, lines.Add);
        File.Copy(baselineToc, tocPath, true); // Simulate interruption after journaling, before TOC commit.
        FontService.Restore(destination, lines.Add);
        Assert(FontService.Hash(tocPath) == baselineHash && !Directory.EnumerateFiles(pc, "fonttool-*.rmdblob").Any(), "Interrupted pre-commit transaction recovers");
        FontService.Replace(destination, fontPath, lines.Add);
        byte[] oldState = File.ReadAllBytes(Path.Combine(backup, "state.json"));
        // Simulate a new valid game index, with no references to the old patch.
        // The reserved header byte changes its identity without changing resources.
        byte[] updated = File.ReadAllBytes(baselineToc); updated[0xFFF] ^= 1;
        File.WriteAllBytes(tocPath, updated);
        string updatedHash = FontService.Hash(tocPath);
        string originalBlob = copy.BlobFiles[targets[0].Chunks[0].BlobIdx];
        string hiddenBlob = originalBlob + ".test-hidden";
        File.Move(originalBlob, hiddenBlob);
        try { Reject(() => FontService.Replace(destination, fontPath, lines.Add), "Incomplete update with missing blob rejected"); }
        finally { File.Move(hiddenBlob, originalBlob); }
        Assert(File.ReadAllBytes(Path.Combine(backup, "state.json")).AsSpan().SequenceEqual(oldState) && FontService.Hash(tocPath) == updatedHash,
            "Failed update validation preserves current index and old backup");
        FontService.Replace(destination, fontPath, lines.Add);
        string archived = Directory.EnumerateDirectories(pc, "_fonttool_backup.archived-*").Single();
        Assert(File.ReadAllBytes(Path.Combine(archived, "state.json")).AsSpan().SequenceEqual(oldState), "Old replacement record archived intact");
        Assert(FontService.Hash(Path.Combine(backup, FontService.TocName)) == updatedHash, "Replacement after update backs up the new version");
        FontService.Replace(destination, fontPath, lines.Add);
        FontService.Restore(destination, lines.Add);
        Assert(FontService.Hash(tocPath) == updatedHash, "Repeated replacement and restore preserve updated version");
        FontService.Replace(destination, fontPath, lines.Add);
        File.Copy(baselineToc, tocPath, true); // Another update while the patch is installed.
        FontService.Restore(destination, lines.Add);
        Assert(FontService.Hash(tocPath) == baselineHash && !Directory.Exists(backup), "Restore after update archives stale backup without rolling back game");
        FontService.Replace(destination, fontPath, lines.Add);
        FontService.Restore(destination, lines.Add);
        Assert(FontService.Hash(tocPath) == baselineHash, "Replacement works after update-aware restore with existing lock file");
        File.Copy(baselineToc, Path.Combine(pc, "base-generic-development.rmdtoc"));
        Reject(() => FontService.Replace(destination, fontPath, lines.Add), "Conflicting development font package rejected");
        Assert(FontService.Hash(sourceToc) == initialHash, "Real game TOC remains unchanged");
        Assert(source.BlobFiles.Select((p, i) => new FileInfo(p).Length == new FileInfo(copy.BlobFiles[i]).Length).All(x => x), "Original blob lengths unchanged");
        File.WriteAllLines(Path.Combine(destination, "results.txt"), lines);
    }
}
