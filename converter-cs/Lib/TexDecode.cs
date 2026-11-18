using System;

namespace CustomOutfits
{
    /// <summary>
    /// Reads the largest image (mip 0) of an RE Engine texture (.tex) as RGBA: BC1 (71/72), BC3 (77/78),
    /// BC7 (98/99) and R8G8B8A8 (28/29). Used to draw outfits for the costume-menu thumbnail.
    /// </summary>
    public static class TexDecode
    {
        public static byte[] Mip0(byte[] tex, out int w, out int h, out int format)
        {
            w = BitConverter.ToUInt16(tex, 8);
            h = BitConverter.ToUInt16(tex, 10);
            int version = BitConverter.ToInt32(tex, 4);
            format = BitConverter.ToInt32(tex, 0x10);
            bool rt = version > 27 && version != 190820018;
            int table = rt ? 0x28 : 0x20;
            long off = BitConverter.ToInt64(tex, table);
            var outp = new byte[w * h * 4];
            switch (format)
            {
                case 71: case 72: Blocks(tex, (int)off, w, h, 8, outp, (s, o, px) => DecodeBc1(s, o, px, false)); break;
                case 77: case 78: Blocks(tex, (int)off, w, h, 16, outp, DecodeBc3); break;
                case 98: case 99: Blocks(tex, (int)off, w, h, 16, outp, DecodeBc7); break;
                case 28: case 29: Array.Copy(tex, off, outp, 0, Math.Min(outp.Length, tex.Length - off)); break;
                default: throw new NotSupportedException("texture format " + format);
            }
            return outp;
        }

        private delegate void BlockFn(byte[] src, int off, byte[] px16);

        private static void Blocks(byte[] src, int off, int w, int h, int blockSize, byte[] outp, BlockFn fn)
        {
            var px = new byte[64];
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int o = off + (by * bw + bx) * blockSize;
                    if (o + blockSize > src.Length) return;
                    fn(src, o, px);
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                        if (x >= w || y >= h) continue;
                        Buffer.BlockCopy(px, i * 4, outp, (y * w + x) * 4, 4);
                    }
                }
        }

        // ---------------------------------------------------------------- BC1 / BC3
        private static void DecodeBc1(byte[] s, int o, byte[] px, bool forceFour)
        {
            int c0 = s[o] | s[o + 1] << 8, c1 = s[o + 2] | s[o + 3] << 8;
            uint idx = BitConverter.ToUInt32(s, o + 4);
            var pal = new int[4, 4];
            Rgb565(c0, pal, 0); Rgb565(c1, pal, 1);
            bool four = forceFour || c0 > c1;
            for (int c = 0; c < 3; c++)
            {
                pal[2, c] = four ? (2 * pal[0, c] + pal[1, c]) / 3 : (pal[0, c] + pal[1, c]) / 2;
                pal[3, c] = four ? (pal[0, c] + 2 * pal[1, c]) / 3 : 0;
            }
            pal[0, 3] = pal[1, 3] = pal[2, 3] = 255; pal[3, 3] = four ? 255 : 0;
            for (int i = 0; i < 16; i++)
            {
                int k = (int)(idx >> (2 * i)) & 3;
                for (int c = 0; c < 4; c++) px[i * 4 + c] = (byte)pal[k, c];
            }
        }

        private static void Rgb565(int c, int[,] pal, int i)
        {
            pal[i, 0] = ((c >> 11) & 31) * 255 / 31; pal[i, 1] = ((c >> 5) & 63) * 255 / 63; pal[i, 2] = (c & 31) * 255 / 31;
        }

        private static void DecodeBc3(byte[] s, int o, byte[] px)
        {
            DecodeBc1(s, o + 8, px, true);
            int a0 = s[o], a1 = s[o + 1];
            ulong bits = 0;
            for (int k = 0; k < 6; k++) bits |= (ulong)s[o + 2 + k] << (8 * k);
            var ap = new int[8];
            ap[0] = a0; ap[1] = a1;
            if (a0 > a1) for (int k = 1; k <= 6; k++) ap[k + 1] = ((7 - k) * a0 + k * a1) / 7;
            else { for (int k = 1; k <= 4; k++) ap[k + 1] = ((5 - k) * a0 + k * a1) / 5; ap[6] = 0; ap[7] = 255; }
            for (int i = 0; i < 16; i++) px[i * 4 + 3] = (byte)ap[(int)(bits >> (3 * i)) & 7];
        }

        // ---------------------------------------------------------------- BC7
        private sealed class Bits
        {
            private readonly ulong _lo, _hi; private int _pos;
            public Bits(byte[] s, int o) { _lo = BitConverter.ToUInt64(s, o); _hi = BitConverter.ToUInt64(s, o + 8); }
            public int Get(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++, _pos++)
                {
                    ulong word = _pos < 64 ? _lo : _hi;
                    v |= (int)((word >> (_pos & 63)) & 1) << i;
                }
                return v;
            }
        }

        //                                        NS PB RB ISB CB AB EPB SPB IB IB2
        private static readonly int[,] Modes = { { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 }, { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
                                                 { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 }, { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
                                                 { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 }, { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
                                                 { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 }, { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 } };

        private static readonly ushort[] P2 = {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22 };

        // 3 subsets: 2 bits per pixel (pixel 0 in the low bits)
        private static readonly uint[] P3 = {
            0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
            0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
            0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
            0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
            0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
            0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
            0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
            0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254 };

        private static readonly byte[] Anchor2 = {
            15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15, 15, 2, 8, 2, 2, 8, 8,15, 2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15, 2, 8, 2, 2, 2,15,15, 6, 6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15 };
        private static readonly byte[] Anchor3a = {
             3, 3,15,15, 8, 3,15,15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8,15, 3, 3, 6,10, 5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15,15, 3,15, 5,15,15,15,15, 3,15, 5, 5, 5, 8, 5,10, 5,10, 8,13,15,12, 3, 3 };
        private static readonly byte[] Anchor3b = {
            15, 8, 8, 3,15,15, 3, 8,15,15,15,15,15,15,15, 8,15, 8,15, 3,15, 8,15, 8, 3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10, 6,15, 8,15, 3, 6, 6, 8,15, 3,15,15,15,15,15,15,15,15,15,15, 3,15,15, 8 };

        private static readonly int[] W2 = { 0, 21, 43, 64 }, W3 = { 0, 9, 18, 27, 37, 46, 55, 64 },
            W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        private static int[] Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

        private static void DecodeBc7(byte[] s, int o, byte[] px)
        {
            int mode = 0;
            while (mode < 8 && (s[o] & (1 << mode)) == 0) mode++;
            if (mode == 8) { Array.Clear(px, 0, 64); return; }
            var r = new Bits(s, o);
            r.Get(mode + 1);
            int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3], cb = Modes[mode, 4],
                ab = Modes[mode, 5], epb = Modes[mode, 6], spb = Modes[mode, 7], ib = Modes[mode, 8], ib2 = Modes[mode, 9];
            int part = r.Get(pb), rot = r.Get(rb), isel = r.Get(isb);
            int ne = ns * 2;
            var ep = new int[ne, 4];
            for (int c = 0; c < 3; c++) for (int e = 0; e < ne; e++) ep[e, c] = r.Get(cb);
            for (int e = 0; e < ne; e++) ep[e, 3] = ab > 0 ? r.Get(ab) : 255;
            int cbits = cb, abits = ab;
            if (epb > 0)
            {
                for (int e = 0; e < ne; e++)
                {
                    int p = r.Get(1);
                    for (int c = 0; c < 3; c++) ep[e, c] = ep[e, c] << 1 | p;
                    if (ab > 0) ep[e, 3] = ep[e, 3] << 1 | p;
                }
                cbits++; if (ab > 0) abits++;
            }
            else if (spb > 0)
            {
                for (int sub = 0; sub < ns; sub++)
                {
                    int p = r.Get(1);
                    for (int k = 0; k < 2; k++)
                    {
                        int e = sub * 2 + k;
                        for (int c = 0; c < 3; c++) ep[e, c] = ep[e, c] << 1 | p;
                        if (ab > 0) ep[e, 3] = ep[e, 3] << 1 | p;
                    }
                }
                cbits++; if (ab > 0) abits++;
            }
            for (int e = 0; e < ne; e++)
            {
                for (int c = 0; c < 3; c++) ep[e, c] = Expand(ep[e, c], cbits);
                if (ab > 0) ep[e, 3] = Expand(ep[e, 3], abits);
            }

            // subset of each pixel and its anchors
            var subset = new int[16];
            int anchor1 = 0, anchor2 = 0;
            if (ns == 2) { for (int i = 0; i < 16; i++) subset[i] = (P2[part] >> i) & 1; anchor1 = Anchor2[part]; }
            else if (ns == 3) { for (int i = 0; i < 16; i++) subset[i] = (int)(P3[part] >> (2 * i)) & 3; anchor1 = Anchor3a[part]; anchor2 = Anchor3b[part]; }
            bool IsAnchor(int i) => i == 0 || (ns >= 2 && i == anchor1) || (ns == 3 && i == anchor2);

            var idx = new int[16];
            for (int i = 0; i < 16; i++) idx[i] = r.Get(IsAnchor(i) ? ib - 1 : ib);
            var idx2 = new int[16];
            if (ib2 > 0) for (int i = 0; i < 16; i++) idx2[i] = r.Get(i == 0 ? ib2 - 1 : ib2);

            for (int i = 0; i < 16; i++)
            {
                int e0 = subset[i] * 2, e1 = e0 + 1;
                int ci = idx[i], cbn = ib, ai = idx[i], abn = ib;
                if (ib2 > 0)
                {
                    if (isel == 0) { ai = idx2[i]; abn = ib2; }
                    else { ci = idx2[i]; cbn = ib2; ai = idx[i]; abn = ib; }
                }
                var wc = Weights(cbn); var wa = Weights(abn);
                int col0 = 0, col1 = 0, col2 = 0, al;
                for (int c = 0; c < 3; c++)
                {
                    int v = ((64 - wc[ci]) * ep[e0, c] + wc[ci] * ep[e1, c] + 32) >> 6;
                    if (c == 0) col0 = v; else if (c == 1) col1 = v; else col2 = v;
                }
                al = ab > 0 ? ((64 - wa[ai]) * ep[e0, 3] + wa[ai] * ep[e1, 3] + 32) >> 6 : 255;
                int t;
                switch (rot)
                {
                    case 1: t = al; al = col0; col0 = t; break;
                    case 2: t = al; al = col1; col1 = t; break;
                    case 3: t = al; al = col2; col2 = t; break;
                }
                px[i * 4] = (byte)col0; px[i * 4 + 1] = (byte)col1; px[i * 4 + 2] = (byte)col2; px[i * 4 + 3] = (byte)al;
            }
        }

        private static int Expand(int v, int bits) { if (bits >= 8) return v; int x = v << (8 - bits); return x | (x >> bits); }
    }
}
