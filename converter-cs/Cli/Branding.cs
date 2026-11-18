// co-cli branding <logo.png> <project_root>
// Makes every image that shows the logo from one transparent PNG:
//   converter-cs/App/Assets: logo-source.png (copy), logo.png (256), app.ico (16-256, PNG entries)
//   publish/images: logo-512.png, thumbnail-1280x720.png (dark background)
//   mods/RE2|RE3/Wardrobe (...)/screenshot.png (256, the Fluffy picture)
//   mods/RE2/.../natives/{x64,stm}/custom_outfits/_custom_outfits/placeholder.tex.{10,34}
//     (game menu picture of outfits without an image: the logo on a transparent background, BC3)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CustomOutfits.Cli
{
    internal static class Branding
    {
        public static void Run(string logoPath, string root)
        {
            using (var src = new Bitmap(logoPath))
            using (var logo = Trim(src))
            {
                string assets = Path.Combine(root, "converter-cs", "App", "Assets");
                File.Copy(logoPath, Path.Combine(assets, "logo-source.png"), true);
                Save(Square(logo, 256, 0.04), Path.Combine(assets, "logo.png"));
                WriteIco(logo, Path.Combine(assets, "app.ico"));

                string images = Path.Combine(root, "publish", "images");
                Save(Square(logo, 512, 0.06), Path.Combine(images, "logo-512.png"));
                Save(Thumbnail(logo), Path.Combine(images, "thumbnail-1280x720.png"));

                foreach (var pkg in new[] { Path.Combine(root, "mods", "RE2", "Wardrobe (RE2)"), Path.Combine(root, "mods", "RE3", "Wardrobe (RE3)") })
                    if (Directory.Exists(pkg)) Save(Square(logo, 256, 0.04), Path.Combine(pkg, "screenshot.png"));

                const int S = 1024;
                using (var ph = Square(logo, S, 0.2))
                {
                    byte[] rgba = Tex.ToRgba(ph, keepAlpha: true);
                    foreach (var (dir, ver) in new[] { ("x64", 10), ("stm", 34) })
                    {
                        string f = Path.Combine(root, "mods", "RE2", "Wardrobe (RE2)", "natives", dir, "custom_outfits", "_custom_outfits", "placeholder.tex." + ver);
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        File.WriteAllBytes(f, Tex.Build(rgba, S, S, ver, alpha: true));
                        Console.WriteLine("ok: " + f);
                    }
                }
            }
        }

        // cut the transparent margins
        private static Bitmap Trim(Bitmap b)
        {
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 8)
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
            if (x1 < 0) return new Bitmap(b);
            return b.Clone(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1), PixelFormat.Format32bppArgb);
        }

        // logo centered in a transparent square, with a margin (fraction of the side)
        private static Bitmap Square(Bitmap logo, int side, double margin)
        {
            var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, logo, new RectangleF((float)(side * margin), (float)(side * margin), (float)(side * (1 - 2 * margin)), (float)(side * (1 - 2 * margin))));
            }
            return bmp;
        }

        private static Bitmap Thumbnail(Bitmap logo)
        {
            var bmp = new Bitmap(1280, 720, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(35, 37, 43), Color.FromArgb(21, 22, 26), 90f))
            {
                g.FillRectangle(bg, 0, 0, 1280, 720);
                Draw(g, logo, new RectangleF(340, 110, 600, 500));
            }
            return bmp;
        }

        // draws the logo as large as it fits in the box, keeping its proportions
        private static void Draw(Graphics g, Bitmap logo, RectangleF box)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            float k = Math.Min(box.Width / logo.Width, box.Height / logo.Height);
            float w = logo.Width * k, h = logo.Height * k;
            g.DrawImage(logo, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        private static void Save(Bitmap b, string path)
        {
            using (b) b.Save(path, ImageFormat.Png);
            Console.WriteLine("ok: " + path);
        }

        // .ico with PNG images (Windows Vista+)
        private static void WriteIco(Bitmap logo, string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var b = Square(logo, s, s <= 32 ? 0.0 : 0.03))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
            Console.WriteLine("ok: " + path);
        }
    }
}
