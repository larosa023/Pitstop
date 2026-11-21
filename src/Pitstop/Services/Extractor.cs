using System.Buffers.Binary;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace Pitstop.Services;

/// <summary>
/// Unpacks NVIDIA's driver package. The .exe is a 7-Zip self-extractor: Pitstop finds the 7z archive
/// inside it and reads it directly (no NVIDIA code runs), writing only the parts it's asked for.
/// </summary>
public static class Extractor
{
    private static readonly byte[] Signature = { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C };

    /// <param name="include">Called with each entry's path ("Display.Driver/nvlddmkm.sys"); true to write it.</param>
    /// <param name="progress">0..1</param>
    public static void Extract(string package, string dest, Func<string, bool> include, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        Exception? first = null;
        try
        {
            ExtractManaged(package, dest, include, progress, ct);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            first = ex;
            Log.Warn("managed extract failed, trying 7-Zip/tar: " + ex);
        }

        // Fallbacks: 7-Zip if it's installed, then Windows' own tar (libarchive reads 7z).
        Files.TryDeleteDirectory(dest);
        Directory.CreateDirectory(dest);
        progress?.Invoke(-1);
        foreach (var tool in Tools())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (code, output) = Shell.Run(tool.exe, tool.args(package, dest), TimeSpan.FromMinutes(20));
                if (code == 0 && File.Exists(Path.Combine(dest, "setup.exe")))
                {
                    RemoveExcluded(dest, include);
                    return;
                }
                Log.Warn($"{tool.exe} exit {code}: {output}");
            }
            catch (Exception ex)
            {
                Log.Warn($"{tool.exe}: {ex.Message}");
            }
            Files.TryDeleteDirectory(dest);
            Directory.CreateDirectory(dest);
        }
        throw new IOException("Pitstop couldn't unpack the driver package: " + first?.Message, first);
    }

    private static IEnumerable<(string exe, Func<string, string, string> args)> Tools()
    {
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var sz = Path.Combine(pf, "7-Zip", "7z.exe");
            if (File.Exists(sz)) { yield return (sz, (pkg, dst) => $"x -y -bd \"-o{dst}\" \"{pkg}\""); break; }
        }
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (File.Exists(tar)) yield return (tar, (pkg, dst) => $"-xf \"{pkg}\" -C \"{dst}\"");
    }

    private static void RemoveExcluded(string dest, Func<string, bool> include)
    {
        foreach (var dir in Directory.GetDirectories(dest))
        {
            var name = Path.GetFileName(dir);
            if (!include(name + "/")) Files.TryDeleteDirectory(dir);
        }
    }

    private static void ExtractManaged(string package, string dest, Func<string, bool> include, Action<double>? progress, CancellationToken ct)
    {
        using var fs = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long offset = FindArchive(fs);
        if (offset < 0) throw new InvalidDataException("No 7-Zip archive found inside the package.");

        using var sub = new OffsetStream(fs, offset);
        using var archive = SevenZipArchive.Open(sub);
        long total = Math.Max(1, archive.TotalUncompressSize);
        long done = 0;
        var root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        int written = 0;

        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            var key = (entry.Key ?? "").Replace('\\', '/').TrimStart('/');
            if (entry.IsDirectory || key.Length == 0) continue;
            done += Math.Max(0, entry.Size);

            if (include(key))
            {
                var target = Path.GetFullPath(Path.Combine(dest, key.Replace('/', '\\')));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The package contains an unsafe path: " + key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    reader.WriteEntryTo(output);
                written++;
            }
            progress?.Invoke(Math.Min(1.0, (double)done / total));
        }
        if (written == 0) throw new InvalidDataException("The package was empty.");
    }

    /// <summary>Finds the 7z signature whose start header checks out (CRC), so a stray match is never used.</summary>
    internal static long FindArchive(Stream s)
    {
        const int chunk = 1 << 20;
        var buf = new byte[chunk + 32];
        long limit = Math.Min(s.Length, 256L << 20);
        long pos = 0;
        while (pos < limit)
        {
            s.Position = pos;
            int n = s.Read(buf, 0, buf.Length);
            if (n < 32) break;
            for (int i = 0; i + 32 <= n; i++)
            {
                if (buf[i] != Signature[0] || buf[i + 1] != Signature[1]) continue;
                if (!buf.AsSpan(i, 6).SequenceEqual(Signature)) continue;
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 8, 4));
                if (Crc32(buf.AsSpan(i + 12, 20)) == crc && buf[i + 6] == 0) return pos + i;
            }
            pos += chunk;
        }
        return -1;
    }

    private static uint[]? _table;

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var t = _table ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>A read-only view of a stream starting at an offset (the archive inside the .exe).</summary>
    private sealed class OffsetStream(Stream inner, long offset) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length - offset;

        public override long Position
        {
            get => _pos;
            set => _pos = value;
        }

        public override int Read(byte[] buffer, int off, int count)
        {
            inner.Position = offset + _pos;
            int n = inner.Read(buffer, off, (int)Math.Min(count, Math.Max(0, Length - _pos)));
            _pos += n;
            return n;
        }

        public override long Seek(long o, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => o,
                SeekOrigin.Current => _pos + o,
                _ => Length + o,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
    }
}
