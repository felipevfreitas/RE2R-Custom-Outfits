// Mdf2.cs - reads and rewrites texture paths in .mdf2 files (RE Engine: RE2R and RE3R).
// Header (same in both builds): "MDF\0", u16 version, u16 matCount, 8 B padding. Then:
//   non-RT (.mdf2.10): 0x40 B materials (texCount @0x14, texHdrOff @0x28, mmtrOff @0x38),
//                      0x18 B texture headers (typeOff @0, pathOff @0x10)
//   RT (.mdf2.21):     0x50 B materials (texCount @0x14, texHdrOff @0x30, mmtrOff @0x48),
//                      0x20 B texture headers (typeOff @0, pathOff @0x10)
//   RE3 (.mdf2.13):    0x40 B materials like non-RT, 0x20 B texture headers like RT.
//   Strings are null-terminated UTF-16LE. The layout is detected from the file itself.
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
        // RE3 (.mdf2.13): materials like the RE2 non-RT build, texture headers like the RE2 RT build
        private static readonly Layout Re3 = new Layout { Mat = 0x40, TexHdr = 0x28, Mmtr = 0x38, Tex = 0x20 };

        // a string pointer that makes sense: inside the file, even, after the tables
        private static bool StrPtr(byte[] b, long p, long min) => p >= min && p < b.Length - 1 && (p & 1) == 0;

        // every texture header of every material points to valid strings (tells 0x18 from 0x20 headers apart)
        // loose = the texture headers may start further on (some tools write another block between the materials and them,
        // e.g. the pl2098/pl2050 materials of "ALL S.T.A.R.S Jill" for RE3); the string pointers are still checked one by one
        private static bool Valid(byte[] b, int matCount, Layout L, bool loose = false)
        {
            long end = 0x10 + (long)matCount * L.Mat;
            if (end + 8 > b.Length) return false;
            long first = U64(b, 0x10 + L.TexHdr);
            if (loose ? (first < end || first >= b.Length) : first != end) return false;
            for (int i = 0; i < matCount; i++)
            {
                int o = 0x10 + i * L.Mat;
                long n = BitConverter.ToUInt32(b, o + 0x14), h = U64(b, o + L.TexHdr);
                if (n > 4096 || h < end || h + n * L.Tex > b.Length) return false;
                for (long t = 0; t < n; t++)
                {
                    int hp = (int)(h + t * L.Tex);
                    if (!StrPtr(b, U64(b, hp), end) || !StrPtr(b, U64(b, hp + 0x10), end)) return false;
                }
            }
            return true;
        }

        private static Layout Detect(byte[] b, int matCount)
        {
            if (matCount == 0) return NonRT;
            foreach (var L in new[] { NonRT, Re3, RT })
                if (Valid(b, matCount, L)) return L;
            foreach (var L in new[] { NonRT, Re3, RT })
                if (Valid(b, matCount, L, true)) return L;
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
