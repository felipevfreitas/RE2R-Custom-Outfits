// Mdf2.cs - reads and rewrites texture paths in .mdf2 files (RE Engine, RE2R).
// Header (same in both builds): "MDF\0", u16 version, u16 matCount, 8 B padding. Then:
//   non-RT (.mdf2.10): 0x40 B materials (texCount @0x14, texHdrOff @0x28, mmtrOff @0x38),
//                      0x18 B texture headers (typeOff @0, pathOff @0x10)
//   RT (.mdf2.21):     0x50 B materials (texCount @0x14, texHdrOff @0x30, mmtrOff @0x48),
//                      0x20 B texture headers (typeOff @0, pathOff @0x10)
//   Strings are null-terminated UTF-16LE. The build is detected from the file itself.
// Rewrite: new strings are APPENDED at the end and the pointers updated (the old strings stay orphaned).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CustomOutfits
{
    public sealed class MdfTexture
    {
        public string Type;
        public string Path;
        public int PathPtrPos;
    }

    public sealed class MdfMaterial
    {
        public string Name;
        public string Mmtr;
        public List<MdfTexture> Textures = new List<MdfTexture>();
    }

    public static class Mdf2
    {
        private static string ReadStr16(byte[] b, long off)
        {
            long end = off;
            while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
            return Encoding.Unicode.GetString(b, (int)off, (int)(end - off));
        }

        private static long U64(byte[] b, int pos) => (long)BitConverter.ToUInt64(b, pos);

        private sealed class Layout { public int Mat, TexHdr, Mmtr, Tex; }
        private static readonly Layout NonRT = new Layout { Mat = 0x40, TexHdr = 0x28, Mmtr = 0x38, Tex = 0x18 };
        private static readonly Layout RT = new Layout { Mat = 0x50, TexHdr = 0x30, Mmtr = 0x48, Tex = 0x20 };

        private static Layout Detect(byte[] b, int matCount)
        {
            if (matCount == 0) return NonRT;
            foreach (var L in new[] { NonRT, RT })
            {
                long end = 0x10 + (long)matCount * L.Mat;
                if (end + 8 <= b.Length && 0x10 + L.Mmtr + 8 <= b.Length && U64(b, 0x10 + L.TexHdr) == end) return L;
            }
            throw new InvalidDataException("unknown mdf2 layout");
        }

        /// <summary>true = material from the RT build (DX12, .mdf2.21).</summary>
        public static bool IsRT(byte[] b) => Detect(b, BitConverter.ToUInt16(b, 6)) == RT;

        public static List<MdfMaterial> Parse(byte[] b)
        {
            if (b.Length < 16 || b[0] != 'M' || b[1] != 'D' || b[2] != 'F') throw new InvalidDataException("not an mdf2 file");
            int matCount = BitConverter.ToUInt16(b, 6);
            var L = Detect(b, matCount);
            var mats = new List<MdfMaterial>();
            for (int i = 0; i < matCount; i++)
            {
                int o = 0x10 + i * L.Mat;
                int texCount = (int)BitConverter.ToUInt32(b, o + 0x14);
                long texHdrOff = U64(b, o + L.TexHdr);
                var m = new MdfMaterial { Name = ReadStr16(b, U64(b, o)), Mmtr = ReadStr16(b, U64(b, o + L.Mmtr)) };
                for (int t = 0; t < texCount; t++)
                {
                    int hp = (int)(texHdrOff + t * L.Tex);
                    m.Textures.Add(new MdfTexture
                    {
                        Type = ReadStr16(b, U64(b, hp)),
                        Path = ReadStr16(b, U64(b, hp + 0x10)),
                        PathPtrPos = hp + 0x10,
                    });
                }
                mats.Add(m);
            }
            return mats;
        }

        /// <summary>map(path) returns the new path, or null to keep it.</summary>
        public static byte[] RewriteTexturePaths(byte[] buf, Func<string, string> map, out int changed)
        {
            var mats = Parse(buf);
            var output = (byte[])buf.Clone();
            var added = new Dictionary<string, long>();
            var extra = new List<byte[]>();
            changed = 0;
            long size = buf.Length;
            if (size % 2 != 0) { extra.Add(new byte[1]); size += 1; } // UTF-16 strings at even offsets
            foreach (var m in mats)
            {
                foreach (var t in m.Textures)
                {
                    string np = map(t.Path);
                    if (np == null || np == t.Path) continue;
                    if (!added.TryGetValue(np, out long off))
                    {
                        off = size;
                        byte[] s = Encoding.Unicode.GetBytes(np + "\0");
                        extra.Add(s);
                        size += s.Length;
                        added[np] = off;
                    }
                    Array.Copy(BitConverter.GetBytes((ulong)off), 0, output, t.PathPtrPos, 8);
                    changed++;
                }
            }
            using (var ms = new MemoryStream((int)size))
            {
                ms.Write(output, 0, output.Length);
                foreach (var e in extra) ms.Write(e, 0, e.Length);
                return ms.ToArray();
            }
        }
    }
}
