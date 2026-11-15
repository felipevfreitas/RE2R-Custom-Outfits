// Pak.cs - reads original files from inside RE2R's .pak files (KPKA v4 format, no encryption).
// Header 16 B: "KPKA", u8 major(4), u8 minor, u16 feature, u32 fileCount, u32 fingerprint.
// Entry 48 B: u32 hashLower, u32 hashUpper, u64 offset, u64 compSize, u64 size, u64 attr, u64 checksum.
// attr & 0xF: 0 = uncompressed, 1 = deflate (raw), 2 = zstd.
// hash = murmur3_x86_32(path as UTF-16LE, seed 0xFFFFFFFF), of the lowercase and uppercase path.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Compressors.ZStandard;

namespace CustomOutfits
{
    public sealed class PakEntry
    {
        public long Offset, CSize, Size;
        public int Attr;
    }

    public sealed class Pak : IDisposable
    {
        public readonly string File;
        private readonly FileStream _fs;
        public readonly Dictionary<string, PakEntry> Entries = new Dictionary<string, PakEntry>();

        public Pak(string file)
        {
            File = file;
            _fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hdr = new byte[16];
            ReadExact(hdr, 0, 16, 0);
            if (Encoding.ASCII.GetString(hdr, 0, 4) != "KPKA") throw new InvalidDataException(file + ": not KPKA");
            if (hdr[4] != 4) throw new InvalidDataException(file + ": pak version " + hdr[4] + " not supported");
            if (BitConverter.ToUInt16(hdr, 6) != 0) throw new InvalidDataException(file + ": encrypted pak not supported");
            int count = (int)BitConverter.ToUInt32(hdr, 8);
            var tbl = new byte[count * 48];
            ReadExact(tbl, 0, tbl.Length, 16);
            for (int i = 0; i < count; i++)
            {
                int o = i * 48;
                string key = BitConverter.ToUInt32(tbl, o) + ":" + BitConverter.ToUInt32(tbl, o + 4);
                Entries[key] = new PakEntry
                {
                    Offset = (long)BitConverter.ToUInt64(tbl, o + 8),
                    CSize = (long)BitConverter.ToUInt64(tbl, o + 16),
                    Size = (long)BitConverter.ToUInt64(tbl, o + 24),
                    Attr = (int)(BitConverter.ToUInt64(tbl, o + 32) & 0xffff),
                };
            }
        }

        private void ReadExact(byte[] buf, int index, int count, long position)
        {
            lock (_fs)
            {
                _fs.Position = position;
                int done = 0;
                while (done < count)
                {
                    int n = _fs.Read(buf, index + done, count - done);
                    if (n <= 0) throw new EndOfStreamException(File);
                    done += n;
                }
            }
        }

        public byte[] Read(PakEntry e)
        {
            var raw = new byte[e.CSize];
            ReadExact(raw, 0, raw.Length, e.Offset);
            switch (e.Attr & 0xf)
            {
                case 0: return raw;
                case 1: return Inflate(new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress), e.Size);
                case 2: return Inflate(new DecompressionStream(new MemoryStream(raw)), e.Size);
                default: throw new InvalidDataException("unknown compression " + e.Attr);
            }
        }

        private static byte[] Inflate(Stream s, long size)
        {
            using (s)
            using (var ms = new MemoryStream(size > 0 && size < int.MaxValue ? (int)size : 0))
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public void Dispose() => _fs.Dispose();
    }

    /// <summary>Every pak of the game; newer patches take priority, DLCs last.</summary>
    public sealed class GamePaks
    {
        private readonly List<Pak> _paks = new List<Pak>();

        public GamePaks(string gameDir)
        {
            var re = new Regex(@"^re_chunk_000\.pak(\.patch_(\d+)\.pak)?$", RegexOptions.IgnoreCase);
            var names = Directory.GetFiles(gameDir).Select(Path.GetFileName).Where(n => re.IsMatch(n))
                .OrderByDescending(n => { var m = re.Match(n); return m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1; });
            foreach (var n in names) _paks.Add(new Pak(Path.Combine(gameDir, n)));
            foreach (var d in Directory.GetDirectories(gameDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(Path.GetFileName(d), @"^\d+$")) continue;
                string f = Path.Combine(d, "re_dlc_000.pak");
                if (System.IO.File.Exists(f) && new FileInfo(f).Length > 16)
                {
                    try { _paks.Add(new Pak(f)); } catch { /* unreadable DLC pak: skip it */ }
                }
            }
        }

        public static uint Murmur3(byte[] data, uint seed = 0xffffffff)
        {
            const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
            uint h = seed;
            int n = data.Length & ~3;
            for (int i = 0; i < n; i += 4)
            {
                uint k = BitConverter.ToUInt32(data, i);
                k *= c1; k = (k << 15) | (k >> 17); k *= c2;
                h ^= k; h = (h << 13) | (h >> 19); h = h * 5 + 0xe6546b64;
            }
            uint t = 0;
            switch (data.Length & 3)
            {
                case 3: t ^= (uint)data[n + 2] << 16; goto case 2;
                case 2: t ^= (uint)data[n + 1] << 8; goto case 1;
                case 1:
                    t ^= data[n];
                    t *= c1; t = (t << 15) | (t >> 17); t *= c2; h ^= t;
                    break;
            }
            h ^= (uint)data.Length;
            h ^= h >> 16; h *= 0x85ebca6b;
            h ^= h >> 13; h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }

        private static uint HashPath(string p) => Murmur3(Encoding.Unicode.GetBytes(p));

        public static string KeyOf(string p)
        {
            p = p.Replace('\\', '/');
            return HashPath(p.ToLowerInvariant()) + ":" + HashPath(p.ToUpperInvariant());
        }

        /// <summary>Full path with "natives/x64/" and numeric extension.</summary>
        public bool Exists(string p) => FindEntry(p, out _, out _);

        private bool FindEntry(string p, out Pak pak, out PakEntry entry)
        {
            string k = KeyOf(p);
            foreach (var pk in _paks)
            {
                if (pk.Entries.TryGetValue(k, out entry)) { pak = pk; return true; }
            }
            pak = null; entry = null;
            return false;
        }

        public byte[] Read(string p) => FindEntry(p, out var pak, out var e) ? pak.Read(e) : null;
    }
}
