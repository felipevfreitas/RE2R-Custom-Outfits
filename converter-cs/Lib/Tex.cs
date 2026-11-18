using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CustomOutfits
{
    /// <summary>
    /// Writes RE Engine textures (.tex) from images: used for the thumbnails shown in the game's costume menu.
    /// One mip, BC1 (the most common format in the game files; an uncompressed RGBA texture showed up white),
    /// power-of-two size: the image goes in the top-left corner and the script crops it (thumbnail_size).
    /// Header: "TEX\0", version, width, height, depth, then
    ///   version 10 (RE2, DX11): mip count (u8), image count (u8) ... 0x20 bytes;
    ///   version 34 (RE2 RT): image count (u8), mip header size (u8) ... and 8 more bytes (0x28).
    /// followed by one entry per mip: offset (u64), pitch (u32), size (u32), then the pixels.
    /// Version 34 follows the known RE Engine layout but was not checked against a game file.
    /// </summary>
    public static class Tex
    {
        public const int FormatBc1Srgb = 72; // DXGI_FORMAT_BC1_UNORM_SRGB (opaque pictures)
        public const int FormatBc3Srgb = 78; // DXGI_FORMAT_BC3_UNORM_SRGB (with transparency: the placeholder)

        public static int Pow2(int n) { int p = 4; while (p < n) p <<= 1; return p; }

        /// <summary>rgba: w x h pixels, both powers of two (at least 4).
        /// alpha = BC3 (keeps transparency), else BC1.</summary>
        public static byte[] Build(byte[] rgba, int w, int h, int version, bool alpha = false)
        {
            bool rt = version > 27 && version != 190820018;
            int header = rt ? 0x28 : 0x20;
            byte[] data = alpha ? Bc3(rgba, w, h) : Bc1(rgba, w, h);
            int pitch = (w / 4) * (alpha ? 16 : 8), size = data.Length;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[] { (byte)'T', (byte)'E', (byte)'X', 0 });
                bw.Write(version);
                bw.Write((ushort)w);
                bw.Write((ushort)h);
                bw.Write((ushort)1);           // depth
                if (rt) { bw.Write((byte)1); bw.Write((byte)16); } // image count, mip header size (16 per mip)
                else { bw.Write((byte)1); bw.Write((byte)1); }     // mip count, image count
                bw.Write(alpha ? FormatBc3Srgb : FormatBc1Srgb);
                bw.Write(-1);                  // swizzle control
                bw.Write(0);                   // cubemap marker
                bw.Write((ushort)1);           // unknown (1 in the game's files)
                bw.Write((ushort)0);
                if (rt)
                {
                    bw.Write((byte)0); bw.Write((byte)0); // swizzle height/depth, width
                    bw.Write((ushort)0);
                    bw.Write((ushort)7);
                    bw.Write((ushort)1);
                }
                bw.Write((long)(header + 16)); // mip 0
                bw.Write(pitch);
                bw.Write(size);
                bw.Write(data);
                return ms.ToArray();
            }
        }

        // Simple BC1 encoder: per 4x4 block, the two endpoints are the extremes of the block along its widest
        // color axis, each pixel takes the nearest of the 4 colors (good enough for a menu picture).
        private static byte[] Bc1(byte[] rgba, int w, int h)
        {
            var outp = new byte[(w / 4) * (h / 4) * 8];
            int o = 0;
            var px = new int[16 * 3];
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4)
                {
                    int[] mn = { 255, 255, 255 }, mx = { 0, 0, 0 };
                    for (int i = 0; i < 16; i++)
                    {
                        int p = ((by + i / 4) * w + bx + i % 4) * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            int v = rgba[p + c];
                            px[i * 3 + c] = v;
                            if (v < mn[c]) mn[c] = v;
                            if (v > mx[c]) mx[c] = v;
                        }
                    }
                    // widest axis: project on (max - min) and take the extreme pixels
                    int dr = mx[0] - mn[0], dg = mx[1] - mn[1], db = mx[2] - mn[2];
                    int lo = 0, hi = 0, plo = int.MaxValue, phi = int.MinValue;
                    for (int i = 0; i < 16; i++)
                    {
                        int d = px[i * 3] * dr + px[i * 3 + 1] * dg + px[i * 3 + 2] * db;
                        if (d < plo) { plo = d; lo = i; }
                        if (d > phi) { phi = d; hi = i; }
                    }
                    ushort c0 = To565(px[hi * 3], px[hi * 3 + 1], px[hi * 3 + 2]);
                    ushort c1 = To565(px[lo * 3], px[lo * 3 + 1], px[lo * 3 + 2]);
                    uint idx = 0;
                    if (c0 == c1) { } // flat block: every pixel uses color 0 (idx = 0)
                    else
                    {
                        if (c0 < c1) { var t = c0; c0 = c1; c1 = t; }
                        var pal = new int[4, 3];
                        From565(c0, out pal[0, 0], out pal[0, 1], out pal[0, 2]);
                        From565(c1, out pal[1, 0], out pal[1, 1], out pal[1, 2]);
                        for (int c = 0; c < 3; c++)
                        {
                            pal[2, c] = (2 * pal[0, c] + pal[1, c]) / 3;
                            pal[3, c] = (pal[0, c] + 2 * pal[1, c]) / 3;
                        }
                        for (int i = 0; i < 16; i++)
                        {
                            int best = 0, bd = int.MaxValue;
                            for (int k = 0; k < 4; k++)
                            {
                                int e0 = px[i * 3] - pal[k, 0], e1 = px[i * 3 + 1] - pal[k, 1], e2 = px[i * 3 + 2] - pal[k, 2];
                                int d = e0 * e0 + e1 * e1 + e2 * e2;
                                if (d < bd) { bd = d; best = k; }
                            }
                            idx |= (uint)best << (i * 2);
                        }
                    }
                    outp[o++] = (byte)c0; outp[o++] = (byte)(c0 >> 8);
                    outp[o++] = (byte)c1; outp[o++] = (byte)(c1 >> 8);
                    outp[o++] = (byte)idx; outp[o++] = (byte)(idx >> 8); outp[o++] = (byte)(idx >> 16); outp[o++] = (byte)(idx >> 24);
                }
            return outp;
        }

        // BC3 = per block, 8 bytes of alpha (two endpoints + 3-bit indices, 8-value mode) and then a BC1 color block
        private static byte[] Bc3(byte[] rgba, int w, int h)
        {
            byte[] color = Bc1(rgba, w, h);
            var outp = new byte[color.Length * 2];
            int blk = 0;
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4, blk++)
                {
                    var a = new int[16];
                    int mn = 255, mx = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        a[i] = rgba[((by + i / 4) * w + bx + i % 4) * 4 + 3];
                        if (a[i] < mn) mn = a[i];
                        if (a[i] > mx) mx = a[i];
                    }
                    int o = blk * 16;
                    outp[o] = (byte)mx; outp[o + 1] = (byte)mn;
                    ulong idx = 0;
                    if (mx > mn)
                    {
                        // palette: 0 = mx, 1 = mn, 2..7 = from mx to mn
                        var pal = new int[8];
                        pal[0] = mx; pal[1] = mn;
                        for (int k = 1; k <= 6; k++) pal[k + 1] = ((7 - k) * mx + k * mn) / 7;
                        for (int i = 0; i < 16; i++)
                        {
                            int best = 0, bd = int.MaxValue;
                            for (int k = 0; k < 8; k++) { int d = Math.Abs(a[i] - pal[k]); if (d < bd) { bd = d; best = k; } }
                            idx |= (ulong)best << (i * 3);
                        }
                    }
                    for (int k = 0; k < 6; k++) outp[o + 2 + k] = (byte)(idx >> (8 * k));
                    Array.Copy(color, blk * 8, outp, o + 8, 8);
                }
            return outp;
        }

        private static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255));
        private static void From565(ushort c, out int r, out int g, out int b)
        {
            r = ((c >> 11) & 31) * 255 / 31; g = ((c >> 5) & 63) * 255 / 63; b = (c & 31) * 255 / 31;
        }

        /// <summary>Loads an image (png/jpg/bmp/gif), shrunk so the longest side is at most maxSide, on a
        /// power-of-two canvas (top-left). w/h = size of the image itself. RGBA pixels, or null if unreadable.</summary>
        public static byte[] LoadRgba(string path, int maxSide, out int w, out int h, bool keepAlpha = false)
        {
            w = h = 0;
            try
            {
                using (var src = Image.FromFile(path))
                {
                    double k = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
                    w = Math.Max(1, (int)Math.Round(src.Width * k));
                    h = Math.Max(1, (int)Math.Round(src.Height * k));
                    using (var bmp = new Bitmap(Pow2(w), Pow2(h), PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(keepAlpha ? Color.Transparent : Color.Black);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        return ToRgba(bmp, keepAlpha);
                    }
                }
            }
            catch { return null; }
        }

        public static byte[] ToRgba(Bitmap bmp, bool keepAlpha = false)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bgra = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    byte b = bgra[i];
                    bgra[i] = bgra[i + 2];
                    bgra[i + 2] = b;
                    if (!keepAlpha) bgra[i + 3] = 255; // thumbnails are opaque
                }
                return bgra;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>Thumbnail of a mod: image → .tex in the given version. False if the image can't be read.</summary>
        /// <param name="alpha">keep transparency (BC3): drawn outfit pictures with a transparent background</param>
        public static bool WriteThumbnail(string image, string outFile, int version, out int w, out int h, int maxSide = 1024, bool alpha = false)
        {
            var rgba = LoadRgba(image, maxSide, out w, out h, alpha);
            if (rgba == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllBytes(outFile, Build(rgba, Pow2(w), Pow2(h), version, alpha));
            return true;
        }
    }
}
