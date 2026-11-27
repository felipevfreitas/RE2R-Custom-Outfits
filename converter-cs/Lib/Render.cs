// Render.cs - draws an outfit (body + face + hair of Resident Evil 2 or 3, non-RT files) for the picture in the game's
// costume menu, when the mod brings no image. Software renderer, nothing to install:
// - meshes .mesh.1808312334 (LOD 0), skinned to a pose made in Blender (Lib/Poses/<character>.json: for each moved
//   bone, its rotation from the rest pose, applied by bone name, so it fits every outfit of that character);
// - color texture of each material (BaseMetalMap, else BaseShiftMap, else BaseMap) times its BaseColor;
//   cut-outs from the AlphaTranslucentOcclusionSSS map (red), the color alpha on hair shaders and *_ALBA textures;
// - lighting like the game's costume pictures (cool key light from the front-left, dark shadows, soft rim),
//   3x supersampling, transparent background, framed from the head to the thighs.
// Same results as the prototype research/render/render_outfit.js.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CustomOutfits
{
    public sealed class RenderPart
    {
        public byte[] Mesh, Mdf;
        public Func<string, byte[]> Texture; // texture path from the mdf2 (".tex", no version) -> .tex.10 file, or null
    }

    /// <summary>What differs between the games: RE3 files (mdf2 v13), hair without a cut-out in the alpha, the face skin drawn
    /// from the front only (a turned head folds the throat and showed its inside), the picture on an opaque black
    /// background with a soft glow like the game's pictures (the RE3 menu ignores transparency).</summary>
    public sealed class RenderOptions
    {
        public bool Re3;
        public bool Hq; // experimental: normal maps, roughness/metal, occlusion, shadows, skin (off: the shading used so far)
        public double[] Camera; // camera matrix (4x4 row-major, game axes) from Blender, or null: automatic framing
        public double FovX;     // its horizontal field of view (radians)
        public bool Gpu;        // Hq: draw on the graphics card (falls back to the CPU)
        public bool Dark;       // Hq lighting: false = studio (promotional renders, with halo), true = strong shadows, no halo
        public double[] Key;    // direction of the key light in view space (x right, y up, z to the camera), or null: default
        public double Frame = 0.5, Fill = 0.92; // part of the height shown from the top; share of the picture the figure fills
        public static readonly RenderOptions Re2 = new RenderOptions();
        public static readonly RenderOptions Re3Jill = new RenderOptions { Re3 = true, Frame = 0.62, Fill = 0.86 };
    }

    public static partial class OutfitRender
    {
        /// <summary>Device used by the last Hq render on the GPU ("hardware: ...", "warp", "failed: ..."), for tests.</summary>
        public static string GpuDevice;

        private static readonly string[] ColorTypes = { "BaseMetalMap", "BaseShiftMap", "BaseMap" };
        private static readonly Dictionary<string, Dictionary<string, double[]>> PoseCache = new Dictionary<string, Dictionary<string, double[]>>();

        // ------------------------------------------------------------------ poses
        /// <summary>The thumbnail pose of a character (embedded Lib/Poses/<character>.json), or null.</summary>
        public static Dictionary<string, double[]> Pose(string character)
        {
            lock (PoseCache)
            {
                if (PoseCache.TryGetValue(character, out var p)) return p;
                string text = null;
                using (var s = typeof(OutfitRender).Assembly.GetManifestResourceStream("pose." + Resource(character)))
                    if (s != null) using (var r = new StreamReader(s)) text = r.ReadToEnd();
                if (text != null) p = PoseFromJson(text);
                PoseCache[character] = p;
                return p;
            }
        }

        /// <summary>A pose exported from Blender (blender_pose_tools.export_pose):
        /// { "bones": { "&lt;name&gt;": { "rot": [9 numbers], "rest": [...] }, ... } }</summary>
        public static Dictionary<string, double[]> PoseFromJson(string text)
        {
            var p = new Dictionary<string, double[]>();
            foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\\{\\s*\"rot\"\\s*:\\s*\\[([^\\]]*)\\]"))
                p[m.Groups[1].Value] = m.Groups[2].Value.Split(',').Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return p;
        }

        public static bool HasPose(string character) => Pose(character) != null;

        // characters that use another one's poses and cameras: the poses are world rotations of each bone, and the joints come
        // from the model itself, so Claire's poses fit Jill (same bone names) with her own proportions
        private static readonly Dictionary<string, string> PoseAlias = new Dictionary<string, string> { ["jill"] = "claire" };

        /// <summary>Resource name of a pose or camera: "jill_2" -> "claire_2", "jill_camera" -> "claire_camera".</summary>
        private static string Resource(string key)
        {
            foreach (var a in PoseAlias)
                if (key == a.Key || key.StartsWith(a.Key + "_")) return a.Value + key.Substring(a.Key.Length);
            return key;
        }

        /// <summary>How much an albedo colour (sRGB 0..1) looks like skin: 0..1 (light-to-tan skin, clean or dirty; not grey,
        /// black, saturated colours). Same formula in the GPU shader.</summary>
        private static double SkinTone(double r, double g, double b)
        {
            if (r < 1e-3) return 0;
            double gr = g / r, br = b / r;
            double S(double e0, double e1, double x) { double t = Math.Min(1, Math.Max(0, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); }
            return S(0.28, 0.42, r) * (1 - S(0.07, 0.14, Math.Abs(gr - 0.77))) * (1 - S(0.1, 0.2, Math.Abs(br - 0.66)));
        }

        /// <summary>Names of the thumbnail poses the user can pick (Lib/Poses/<character>.json, <character>_2.json...).</summary>
        public static readonly string[] PoseNames = { "Arms down", "Hand on hip" };

        /// <summary>Resource name of pose number n (0 = the first) of a character: "claire", "claire_2"...; null if it does not exist.</summary>
        public static string PoseKey(string character, int n)
        {
            string k = n <= 0 ? character : character + "_" + (n + 1);
            return Pose(k) != null ? k : null;
        }

        /// <summary>How many poses a character has.</summary>
        public static int PoseCount(string character) { int n = 0; while (n < 9 && PoseKey(character, n) != null) n++; return n; }

        /// <summary>The menu-picture camera of a character (embedded Lib/Poses/&lt;character&gt;_camera.json, exported from
        /// Blender: "matrix" 4x4 row-major in game axes, "fov_y"), or null: automatic framing. Square pictures keep the
        /// vertical field of view (head to thighs, as framed in Blender).</summary>
        public static bool Camera(string character, out double[] matrix, out double fovY)
        {
            matrix = null; fovY = 0;
            string text = null;
            using (var s = typeof(OutfitRender).Assembly.GetManifestResourceStream("pose." + Resource(character + "_camera")))
                if (s != null) using (var r = new StreamReader(s)) text = r.ReadToEnd();
            if (text == null) return false;
            double Num(string x) => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var m = Regex.Match(text, "\"matrix\"\\s*:\\s*\\[([^\\]]*)\\]");
            var f = Regex.Match(text, "\"fov_y\"\\s*:\\s*([-0-9.eE]+)");
            if (!m.Success || !f.Success) return false;
            matrix = m.Groups[1].Value.Split(',').Select(Num).ToArray();
            fovY = Num(f.Groups[1].Value);
            return matrix.Length == 16;
        }

        /// <summary>True if the figure touches the picture's border (cut off by a fixed camera).</summary>
        public static bool TouchesBorder(byte[] rgba, int w, int h)
        {
            if (rgba == null) return false;
            for (int i = 0; i < w; i++)
                if (rgba[i * 4 + 3] > 40) return true; // top row and sides (the bottom is cut on purpose)
            for (int y = 0; y < h * 9 / 10; y++)
                if (rgba[(y * w) * 4 + 3] > 40 || rgba[(y * w + w - 1) * 4 + 3] > 40) return true;
            return false;
        }

        // ------------------------------------------------------------------ small math (4x4 row-major, m[r*4+c])
        private static readonly double[] Ident = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        private static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                    r[i * 4 + j] = s;
                }
            return r;
        }

        private static double[] Trans(double x, double y, double z) => new double[] { 1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1 };

        private static double ToLin(double c) { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        private static double ToSrgb(double l) => 255 * (l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055);

        private static float Half(ushort h)
        {
            int s = (h >> 15) != 0 ? -1 : 1, e = (h >> 10) & 31, f = h & 1023;
            return (float)(e == 0 ? s * f * Math.Pow(2, -24) : s * (1 + f / 1024.0) * Math.Pow(2, e - 15));
        }

        // ------------------------------------------------------------------ textures
        private sealed class Tex
        {
            public int W, H;
            public bool Alpha; // *_ALBA: alpha is a cut-out
            public bool NoAlpha; // alpha almost all 255: the texture was saved without that channel (some mods)
            // Hq: the .tex file and the mip that is drawn (the GPU takes its compressed blocks as they are; the RGBA pixels
            // are decoded only if the CPU draws)
            public byte[] File;
            public int Level, Format;
            private byte[] _px;
            public byte[] Px
            {
                get { if (_px == null && File != null) _px = TexDecode.Mip(File, Level, out _, out _, out _); return _px; }
                set { _px = value; }
            }
        }

        // formats the GPU reads as they are (BC1, BC3, BC7, RGBA8); the sRGB ones are read as plain values, like the CPU
        private static readonly int[] RawFormats = { 71, 72, 77, 78, 98, 99, 28, 29 };

        /// <summary>Hq: a texture without decoding it: the largest mip not above MaxTex; the metal check reads a small mip.</summary>
        private static Tex LoadTexLazy(byte[] file, string path)
        {
            if (file == null) return null;
            try
            {
                int n = TexDecode.MipCount(file), level = 0;
                TexDecode.MipInfo(file, 0, out int w, out int h, out int format, out _, out _, out _);
                if (Array.IndexOf(RawFormats, format) < 0) return null;
                while (level + 1 < n && Math.Max(w >> level, h >> level) > MaxTex) level++;
                TexDecode.MipInfo(file, level, out w, out h, out _, out _, out _, out _);
                int small = level;
                while (small + 1 < n && Math.Max(w >> (small - level), h >> (small - level)) > 256) small++;
                var px = TexDecode.Mip(file, small, out _, out _, out _);
                int full = 0, seen = 0;
                for (int q = 3; q < px.Length; q += 4) { seen++; if (px[q] >= 250) full++; }
                return new Tex
                {
                    File = file, Level = level, Format = format, W = w, H = h, NoAlpha = full > seen * 0.9,
                    Alpha = Regex.IsMatch(Regex.Replace(path, @"\.tex$", "", RegexOptions.IgnoreCase), "_alba$", RegexOptions.IgnoreCase),
                };
            }
            catch { return null; }
        }

        private sealed class Mat
        {
            public string Name;
            public Tex Color, Atos, Nrm; // Nrm: NormalRoughnessMap (R,G normal; A roughness), only in Hq
            public double[] Tint;
            public double[] Emis; // glowing material (emissive shader): its light colour, brightest channel = 1 (linear); null = no glow
            public bool HairAlpha, Cull, Skin, Eye, Face; // Face: face material (GPU: its painted scalp is darkened) // Eye: eyeball (the key light shadow of the lids is softened, as the game eye shader does)
            public double[] Spec1, Spec2; // hair shader (Hq): primary / secondary strand highlight colors
            public double Sharp1, Sharp2, SpecInt, Shift; // Skin: soft light through the skin (Hq)
        }

        // textures above this size are reduced (the picture is 1024 pixels drawn at 3x: more detail is not visible)
        private const int MaxTex = 2048;

        private static Tex LoadTex(byte[] file, string path)
        {
            if (file == null) return null;
            try
            {
                var px = TexDecode.Mip0(file, out int w, out int h, out _);
                while (w > MaxTex || h > MaxTex)
                {
                    int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                    var half = new byte[nw * nh * 4];
                    for (int y = 0; y < nh; y++)
                        for (int x = 0; x < nw; x++)
                            for (int c = 0; c < 4; c++)
                            {
                                int s = 0;
                                for (int dy = 0; dy < 2; dy++)
                                    for (int dx = 0; dx < 2; dx++)
                                        s += px[((Math.Min(h - 1, y * 2 + dy)) * w + Math.Min(w - 1, x * 2 + dx)) * 4 + c];
                                half[(y * nw + x) * 4 + c] = (byte)(s / 4);
                            }
                    px = half; w = nw; h = nh;
                }
                int full = 0, seen = 0;
                for (int q = 3; q < px.Length; q += 4 * 97) { seen++; if (px[q] >= 250) full++; }
                return new Tex { W = w, H = h, Px = px, NoAlpha = full > seen * 0.9, Alpha = Regex.IsMatch(Regex.Replace(path, @"\.tex$", "", RegexOptions.IgnoreCase), "_alba$", RegexOptions.IgnoreCase) };
            }
            catch { return null; }
        }

        // bilinear sample (wraps)
        private static void Sample(Tex t, double u, double v, double[] o)
        {
            u = (u - Math.Floor(u)) * t.W - 0.5; v = (v - Math.Floor(v)) * t.H - 0.5;
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            double fx = u - x0, fy = v - y0;
            o[0] = o[1] = o[2] = o[3] = 0;
            for (int k = 0; k < 4; k++)
            {
                int dx = k & 1, dy = k >> 1;
                double wgt = (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy);
                int x = ((x0 + dx) % t.W + t.W) % t.W, y = ((y0 + dy) % t.H + t.H) % t.H, q = (y * t.W + x) * 4;
                for (int c = 0; c < 4; c++) o[c] += t.Px[q + c] * wgt;
            }
        }

        // BaseColor of every material of an mdf2 v10 (entries of 0x40 bytes; param count @0x10, param headers @0x20,
        // data @0x30; param header 0x18 bytes: name (UTF-16) @0, component count @0x10, offset in the data @0x14)
        private static double[][] BaseColors(byte[] b, bool re3 = false) => Params(b, "BaseColor", 3, re3);

        // a float parameter (at least minComp components) of every material, or null where it is missing
        private static double[][] Params(byte[] b, string param, int minComp, bool re3 = false)
        {
            int n = BitConverter.ToUInt16(b, 6);
            var res = new double[n][];
            for (int i = 0; i < n; i++)
            {
                int o = 0x10 + i * 0x40;
                int pc = (int)BitConverter.ToUInt32(b, o + 0x10);
                long ph = BitConverter.ToInt64(b, o + 0x20), pd = BitConverter.ToInt64(b, o + 0x30);
                for (int k = 0; k < pc; k++)
                {
                    int h = (int)(ph + k * 0x18);
                    int nameOff = (int)BitConverter.ToInt64(b, h);
                    int end = nameOff;
                    while (end + 1 < b.Length && (b[end] | b[end + 1]) != 0) end += 2;
                    if (Encoding.Unicode.GetString(b, nameOff, end - nameOff) != param) continue;
                    int comp = (int)BitConverter.ToUInt32(b, h + (re3 ? 0x14 : 0x10)), rel = (int)BitConverter.ToUInt32(b, h + (re3 ? 0x10 : 0x14)); // (v13: offset first, then the count)
                    if (comp < minComp) break;
                    res[i] = new double[Math.Max(minComp, 1)];
                    for (int c = 0; c < res[i].Length; c++) res[i][c] = BitConverter.ToSingle(b, (int)(pd + rel + c * 4));
                    break;
                }
            }
            return res;
        }

        // ------------------------------------------------------------------ skinning
        private sealed class Skin { public double[][] S; public int[] Map; }

        // Skin matrices from the pose. Bones are solved by name and through their parents in any order. The body
        // (first part) sets the movement of every bone it has; the other meshes (face, hair: their skeletons start at
        // the head) use the body's for those bones, and their own extra bones follow their parent.
        private static Skin PoseSkin(byte[] b, bool isBody, Dictionary<string, double[]> pose,
            Dictionary<string, double[]> bodyS, Dictionary<string, double[]> bodyQ)
        {
            long U64(long o) => BitConverter.ToInt64(b, (int)o);
            string Str(long o) { int e = (int)o; while (b[e] != 0) e++; return Encoding.ASCII.GetString(b, (int)o, e - (int)o); }
            long bo = U64(0x30);
            if (bo == 0) return null;
            int n = (int)BitConverter.ToUInt32(b, (int)bo), mapN = (int)BitConverter.ToUInt32(b, (int)bo + 4);
            long hier = U64(bo + 0x10), world = U64(bo + 0x20), bidx = U64(0x68), names = U64(0x78);
            var nm = new string[n];
            var par = new int[n];
            var W = new double[n][];
            for (int i = 0; i < n; i++)
            {
                nm[i] = Str(U64(names + BitConverter.ToUInt16(b, (int)(bidx + i * 2)) * 8));
                par[i] = BitConverter.ToInt16(b, (int)(hier + i * 16 + 2));
                var m = new double[16];
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++) m[r * 4 + c] = BitConverter.ToSingle(b, (int)(world + i * 64 + (c * 4 + r) * 4)); // stored transposed
                W[i] = m;
            }
            var S = new double[n][];
            var Q = new double[n][];
            var busy = new bool[n];
            void Solve(int i)
            {
                if (S[i] != null) return;
                // a joint the body also has (face, hair): it goes where the body's joint went and turns like it, but around its
                // OWN rest position. (Taking the body's whole matrix turned it around the body's rest joint: a mod body with
                // another neck height or proportions than the game's face bent the neck forward, while the game, like Blender,
                // puts the head on the body's joint.)
                if (!isBody && bodyS.TryGetValue(nm[i], out var bj))
                {
                    var bq = bodyQ[nm[i]] ?? Ident;
                    S[i] = Mul(Mul(Trans(bj[0], bj[1], bj[2]), bq), Trans(-W[i][3], -W[i][7], -W[i][11]));
                    Q[i] = bodyQ[nm[i]];
                    return;
                }
                int p = par[i] >= 0 && par[i] < n && !busy[par[i]] ? par[i] : -1;
                busy[i] = true; // only while solving its parents: guards against a loop in the hierarchy
                if (p >= 0) Solve(p);
                busy[i] = false;
                double[] q = null;
                if (pose.TryGetValue(nm[i], out var rot) && rot.Length >= 9)
                    q = new[] { rot[0], rot[1], rot[2], 0, rot[3], rot[4], rot[5], 0, rot[6], rot[7], rot[8], 0, 0, 0, 0, 1 };
                else if (p >= 0) q = Q[p];
                if (q == null && (p < 0 || ReferenceEquals(S[p], Ident))) { S[i] = Ident; Q[i] = null; return; }
                q = q ?? Ident; // (not turned, but its parent moved: it follows)
                // the joint goes where the parent's movement takes it; the bone turns around it
                double x0 = W[i][3], y0 = W[i][7], z0 = W[i][11];
                var Sp = p >= 0 ? S[p] : Ident;
                double x1 = Sp[0] * x0 + Sp[1] * y0 + Sp[2] * z0 + Sp[3];
                double y1 = Sp[4] * x0 + Sp[5] * y0 + Sp[6] * z0 + Sp[7];
                double z1 = Sp[8] * x0 + Sp[9] * y0 + Sp[10] * z0 + Sp[11];
                S[i] = Mul(Mul(Trans(x1, y1, z1), q), Trans(-x0, -y0, -z0));
                Q[i] = q;
            }
            for (int i = 0; i < n; i++) Solve(i);
            // the body's posed joints (position) and turns, for the other parts. The first bone of each name: a mod body can repeat
            // one (JILL ALL STARS has a second "head" under the necklace chain, at chest height; the face went there)
            if (isBody)
                for (int i = 0; i < n; i++)
                {
                    if (bodyS.ContainsKey(nm[i])) continue;
                    double x0 = W[i][3], y0 = W[i][7], z0 = W[i][11]; var s = S[i];
                    bodyS[nm[i]] = new[] { s[0] * x0 + s[1] * y0 + s[2] * z0 + s[3], s[4] * x0 + s[5] * y0 + s[6] * z0 + s[7], s[8] * x0 + s[9] * y0 + s[10] * z0 + s[11] };
                    bodyQ[nm[i]] = Q[i];
                }
            var map = new int[mapN];
            for (int i = 0; i < mapN; i++) map[i] = BitConverter.ToUInt16(b, (int)bo + 0x30 + i * 2);
            return new Skin { S = S, Map = map };
        }

        // ------------------------------------------------------------------ render
        private struct Tri
        {
            public float X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2;   // view space
            public float[] N;                                  // 9: normals of the corners (view space)
            public float[] T;                                  // 9: tangents of the corners (view space), Hq
            public float Sg0, Sg1, Sg2;                        // bitangent signs
            public float U0, V0, U1, V1, U2, V2;
            public Mat M;
        }

        /// <summary>Draws the parts (body first) and returns the picture as RGBA (w x h), transparent background.</summary>
        public static byte[] Render(List<RenderPart> parts, Dictionary<string, double[]> pose, int W = 1024, int H = 1024, RenderOptions opt = null)
        {
            opt = opt ?? RenderOptions.Re2;
            const int SS = 3;
            double yaw = -25 * Math.PI / 180, cy = Math.Cos(yaw), sy = Math.Sin(yaw);
            // view space: turned around Y (default), or the space of a camera placed in Blender (x right, y up, looking
            // down -z): v = R^T (p - camera position)
            var cam = opt.Camera;
            float[] View(double x, double y, double z, bool point)
            {
                if (cam == null) return new[] { (float)(x * cy + z * sy), (float)y, (float)(-x * sy + z * cy) };
                if (point) { x -= cam[3]; y -= cam[7]; z -= cam[11]; }
                return new[] { (float)(cam[0] * x + cam[4] * y + cam[8] * z), (float)(cam[1] * x + cam[5] * y + cam[9] * z), (float)(cam[2] * x + cam[6] * y + cam[10] * z) };
            }
            var bodyS = new Dictionary<string, double[]>();
            var bodyQ = new Dictionary<string, double[]>();
            var tris = new List<Tri>();
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var part = parts[pi];
                byte[] b = part.Mesh;
                long U64(long o) => BitConverter.ToInt64(b, (int)o);
                string Str(long o) { int e = (int)o; while (b[e] != 0) e++; return Encoding.ASCII.GetString(b, (int)o, e - (int)o); }
                long lodHdr = U64(0x18), bufHdr = U64(0x50), matIdx = U64(0x60), names = U64(0x78);
                int matCount = b[lodHdr + 1];
                var mats = Mdf2.Parse(part.Mdf);
                var colors = BaseColors(part.Mdf, opt.Re3);
                var emisC = Params(part.Mdf, "EmissiveColor", 3, opt.Re3); var emisR = Params(part.Mdf, "EmissiveRate", 1, opt.Re3);
                double[][] P(string name, int c) => opt.Hq ? Params(part.Mdf, name, c, opt.Re3) : null;
                var hs1 = P("PrimalySpecularColor", 3); var hs2 = P("SecondarySpecularColor", 3);
                var hsh1 = P("PrimalySpec_Sharpness", 1); var hsh2 = P("SecondSpec_Sharpness", 1);
                var hsi = P("SpecularIntensity", 1); var hshift = P("SpecularShiftIntensity", 1);
                var texCache = new Dictionary<string, Tex>(StringComparer.OrdinalIgnoreCase);
                Tex Texture(string p)
                {
                    if (!texCache.TryGetValue(p, out var t)) texCache[p] = t = opt.Hq ? LoadTexLazy(part.Texture(p), p) : LoadTex(part.Texture(p), p);
                    return t;
                }
                var matOf = new Mat[matCount]; // null = not drawn
                // materials by name; mesh slots whose name the mdf2 doesn't have take, in order, the mdf2 materials no slot names
                // (Reika: the mesh calls two slots pl2020_Skirt/Cardigan_Mat, the mdf2 has wp3100_Lightning_Lamp_Mat and another
                // instead; in game the lamp material lights the suit's indicators, which its add-ons recolour)
                var slotNames = Enumerable.Range(0, matCount).Select(i => Str(U64(names + BitConverter.ToUInt16(b, (int)(matIdx + i * 2)) * 8))).ToList();
                var spare = new Queue<int>(Enumerable.Range(0, mats.Count).Where(k => !slotNames.Contains(mats[k].Name)));
                for (int i = 0; i < matCount; i++)
                {
                    string name = slotNames[i];
                    int mi = mats.FindIndex(x => x.Name == name);
                    if (mi < 0 && spare.Count > 0) mi = spare.Dequeue();
                    if (mi < 0 || Regex.IsMatch(name, "sling", RegexOptions.IgnoreCase)) continue; // the weapon sling only shows with some weapons
                    var m = mats[mi];
                    var ct = ColorTypes.Select(ty => m.Textures.FirstOrDefault(x => x.Type == ty)).FirstOrDefault(x => x != null && !Regex.IsMatch(x.Path, "null", RegexOptions.IgnoreCase));
                    // a glowing material (emissive shader: lamps, the lights of a suit): its EmissiveColor, unless EmissiveRate is 0 or the
                    // emissive map is black (Reika's add-ons only change this colour: blue, red, or off)
                    double[] emis = null;
                    var ec = emisC[mi];
                    bool emissiveShader = Regex.IsMatch(m.Mmtr ?? "", "emissive", RegexOptions.IgnoreCase);
                    if (ec != null && (emisR[mi] == null || emisR[mi][0] > 0)
                        && !m.Textures.Any(x => x.Type == "EmissiveMap" && Regex.IsMatch(x.Path, "nullblack", RegexOptions.IgnoreCase)))
                    {
                        double mx = Math.Max(ec[0], Math.Max(ec[1], ec[2]));
                        if (mx > 1e-3) emis = new[] { Math.Max(0, ec[0]) / mx, Math.Max(0, ec[1]) / mx, Math.Max(0, ec[2]) / mx };
                    }
                    if (ct == null && emis == null && !emissiveShader) continue; // cornea, tear line... (a lamp with a plain colour is drawn)
                    var at = m.Textures.FirstOrDefault(x => x.Type == "AlphaTranslucentOcclusionSSSMap" && !Regex.IsMatch(x.Path, "null", RegexOptions.IgnoreCase));
                    var bc = colors[mi];
                    var nt = opt.Hq ? m.Textures.FirstOrDefault(x => x.Type == "NormalRoughnessMap" && !Regex.IsMatch(x.Path, "null", RegexOptions.IgnoreCase)) : null;
                    matOf[i] = new Mat
                    {
                        Name = name,
                        Face = Regex.IsMatch(name, "face", RegexOptions.IgnoreCase),
                        Color = ct != null ? Texture(ct.Path) : null,
                        Emis = emis,
                        Atos = at != null ? Texture(at.Path) : null,
                        Nrm = nt != null ? Texture(nt.Path) : null,
                        Spec1 = hs1?[mi], Spec2 = hs2?[mi],
                        Sharp1 = hsh1?[mi]?[0] ?? 0, Sharp2 = hsh2?[mi]?[0] ?? 0, SpecInt = hsi?[mi]?[0] ?? 0, Shift = hshift?[mi]?[0] ?? 0,
                        Skin = !Regex.IsMatch(m.Mmtr ?? "", "hair", RegexOptions.IgnoreCase) && Regex.IsMatch(name, @"arm|hand|neck|face|skin|leg|teeth|body", RegexOptions.IgnoreCase),
                        // white on most materials; the actual color on hair, beard and brows (their textures are a light base)
                        Tint = bc != null && bc.Any(v => Math.Abs(v - 1) > 0.001) ? bc : null,
                        // hair shaders: the alpha of the color texture is the coverage (elsewhere it is metalness)
                        HairAlpha = !opt.Re3 && Regex.IsMatch(m.Mmtr ?? "", "hair", RegexOptions.IgnoreCase), // (RE3: the alpha of the hair texture is not a cut-out)
                        Cull = opt.Re3 && Regex.IsMatch(name, "Face_Mat", RegexOptions.IgnoreCase) && Environment.GetEnvironmentVariable("CO_NOCULL") == null, // (CO_NOCULL: tests)
                        Eye = Regex.IsMatch(name, "eye", RegexOptions.IgnoreCase) && !Regex.IsMatch(name, "lash|brow|blow", RegexOptions.IgnoreCase),
                    };
                }
                long lod0 = U64(U64(lodHdr + 0x40));
                int groups = (int)BitConverter.ToUInt32(b, (int)lod0);
                long gptr = U64(lod0 + 8);
                long eo = U64(bufHdr), vb = U64(bufHdr + 8), ib = U64(bufHdr + 16);
                int ne = BitConverter.ToUInt16(b, (int)bufHdr + 0x20);
                var el = new Dictionary<int, long>();
                for (int i = 0; i < ne; i++) el[BitConverter.ToUInt16(b, (int)(eo + i * 8))] = BitConverter.ToUInt32(b, (int)(eo + i * 8 + 4));
                var skin = el.ContainsKey(4) ? PoseSkin(b, pi == 0, pose, bodyS, bodyQ) : null;
                var cache = new Dictionary<int, float[]>(); // vertex -> view-space position (3), normal (3), uv (2)
                var m16 = new double[16];
                float[] Vertex(int i)
                {
                    if (cache.TryGetValue(i, out var r)) return r;
                    int po = (int)(vb + el[0] + i * 12);
                    double px = BitConverter.ToSingle(b, po), py = BitConverter.ToSingle(b, po + 4), pz = BitConverter.ToSingle(b, po + 8);
                    int no = (int)(vb + el[1] + i * 8);
                    double nx = (sbyte)b[no] / 127.0, ny = (sbyte)b[no + 1] / 127.0, nz = (sbyte)b[no + 2] / 127.0;
                    double tx = (sbyte)b[no + 4] / 127.0, ty = (sbyte)b[no + 5] / 127.0, tz = (sbyte)b[no + 6] / 127.0;
                    float sg = (sbyte)b[no + 7] < 0 ? -1 : 1;
                    if (skin != null)
                    {
                        // blended skin matrix (8 bone indices + 8 weights /255); a weight whose bone is not known counts as
                        // "not moved" (skipping it pulled vertices to the origin); the total is normalized
                        int wo = (int)(vb + el[4] + i * 16);
                        Array.Clear(m16, 0, 16);
                        double tot = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            double w = b[wo + 8 + k] / 255.0;
                            if (w == 0) continue;
                            int bi = b[wo + k];
                            var S = bi < skin.Map.Length && skin.Map[bi] < skin.S.Length ? skin.S[skin.Map[bi]] ?? Ident : Ident;
                            for (int q = 0; q < 16; q++) m16[q] += w * S[q];
                            tot += w;
                        }
                        if (tot > 0)
                        {
                            for (int q = 0; q < 16; q++) m16[q] /= tot;
                            double ax = m16[0] * px + m16[1] * py + m16[2] * pz + m16[3];
                            double ay = m16[4] * px + m16[5] * py + m16[6] * pz + m16[7];
                            double az = m16[8] * px + m16[9] * py + m16[10] * pz + m16[11];
                            double bx = m16[0] * nx + m16[1] * ny + m16[2] * nz;
                            double by = m16[4] * nx + m16[5] * ny + m16[6] * nz;
                            double bz = m16[8] * nx + m16[9] * ny + m16[10] * nz;
                            double ux = m16[0] * tx + m16[1] * ty + m16[2] * tz;
                            double uy = m16[4] * tx + m16[5] * ty + m16[6] * tz;
                            double uz = m16[8] * tx + m16[9] * ty + m16[10] * tz;
                            px = ax; py = ay; pz = az; nx = bx; ny = by; nz = bz; tx = ux; ty = uy; tz = uz;
                        }
                    }
                    int uo = (int)(vb + el[2] + i * 4);
                    // view: turned around Y
                    var vp = View(px, py, pz, true); var vn = View(nx, ny, nz, false); var vt = View(tx, ty, tz, false);
                    r = new float[]
                    {
                        vp[0], vp[1], vp[2], vn[0], vn[1], vn[2],
                        Half(BitConverter.ToUInt16(b, uo)), Half(BitConverter.ToUInt16(b, uo + 2)),
                        vt[0], vt[1], vt[2], sg,
                    };
                    cache[i] = r;
                    return r;
                }
                for (int g = 0; g < groups; g++)
                {
                    long go = U64(gptr + g * 8);
                    for (int s = 0; s < b[go + 1]; s++)
                    {
                        int so = (int)(go + 16 + s * 16);
                        int mat = (int)BitConverter.ToUInt32(b, so), nIdx = (int)BitConverter.ToUInt32(b, so + 4);
                        int i0 = (int)BitConverter.ToUInt32(b, so + 8), v0 = (int)BitConverter.ToUInt32(b, so + 12);
                        if (mat >= matCount || matOf[mat] == null) continue;
                        for (int k = 0; k + 2 < nIdx; k += 3)
                        {
                            var a = Vertex(v0 + BitConverter.ToUInt16(b, (int)(ib + (i0 + k) * 2)));
                            var c1 = Vertex(v0 + BitConverter.ToUInt16(b, (int)(ib + (i0 + k + 1) * 2)));
                            var c2 = Vertex(v0 + BitConverter.ToUInt16(b, (int)(ib + (i0 + k + 2) * 2)));
                            tris.Add(new Tri
                            {
                                X0 = a[0], Y0 = a[1], Z0 = a[2], X1 = c1[0], Y1 = c1[1], Z1 = c1[2], X2 = c2[0], Y2 = c2[1], Z2 = c2[2],
                                N = new[] { a[3], a[4], a[5], c1[3], c1[4], c1[5], c2[3], c2[4], c2[5] },
                                U0 = a[6], V0 = a[7], U1 = c1[6], V1 = c1[7], U2 = c2[6], V2 = c2[7],
                                T = opt.Hq ? new[] { a[8], a[9], a[10], c1[8], c1[9], c1[10], c2[8], c2[9], c2[10] } : null,
                                Sg0 = a[11], Sg1 = c1[11], Sg2 = c2[11],
                                M = matOf[mat],
                            });
                        }
                    }
                }
            }
            if (tris.Count == 0) return null;

            // framing like the game pictures: from above the head down to the thighs (half of the height from the top),
            // the figure filling 92% of the picture, a bit to the left and lower
            double mnY = 1e9, mxY = -1e9;
            foreach (var t in tris)
            {
                mnY = Math.Min(mnY, Math.Min(t.Y0, Math.Min(t.Y1, t.Y2)));
                mxY = Math.Max(mxY, Math.Max(t.Y0, Math.Max(t.Y1, t.Y2)));
            }
            double top = mxY, bottom = mxY - (mxY - mnY) * opt.Frame;
            double vx0 = 1e9, vx1 = -1e9;
            void Seen(float x, float y) { if (y >= bottom && y <= top) { vx0 = Math.Min(vx0, x); vx1 = Math.Max(vx1, x); } }
            foreach (var t in tris) { Seen(t.X0, t.Y0); Seen(t.X1, t.Y1); Seen(t.X2, t.Y2); }
            if (vx1 < vx0) { vx0 = tris.Min(t => Math.Min(t.X0, Math.Min(t.X1, t.X2))); vx1 = tris.Max(t => Math.Max(t.X0, Math.Max(t.X1, t.X2))); }
            int IW = W * SS, IH = H * SS;
            double fill = opt.Fill;
            double sc = Math.Min(IW * fill / (vx1 - vx0), IH * fill / (top - bottom)), cx = (vx0 + vx1) / 2, cyy = (top + bottom) / 2;
            double SHX = -0.05 * IW, SHY = 0.1 * IH;
            // with a camera: perspective (horizontal field of view), no automatic framing
            double fpx = cam != null ? IW / 2.0 / Math.Tan(opt.FovX / 2) : 0;
            double PX(double x, double z) => cam != null ? IW / 2.0 + x * fpx / Math.Max(1e-4, -z) : IW / 2.0 + SHX + (x - cx) * sc;
            double PY(double y, double z) => cam != null ? IH / 2.0 - y * fpx / Math.Max(1e-4, -z) : IH / 2.0 + SHY - (y - cyy) * sc;

            // lights in view space (x right, y up, z towards the camera): a strong, slightly cool key from the front-left
            // and above, little fill, a dark ambient, a cool rim on the edges and a soft highlight. Linear space.
            double[] Norm(double x, double y, double z) { double l = Math.Sqrt(x * x + y * y + z * z); if (l == 0) l = 1; return new[] { x / l, y / l, z / l }; }
            var KEY = opt.Key != null ? Norm(opt.Key[0], opt.Key[1], opt.Key[2]) : opt.Hq ? (opt.Dark ? Norm(0.72, 0.5, 0.48) : Norm(0.5, 0.55, 0.65)) : Norm(-0.6, 0.55, 0.6); /* Hq: front and a bit above (promotional renders) */ var FILL = Norm(0.75, 0.05, 0.45); var HALF = Norm(-0.6, 0.55, 1.6);
            double[] KEY_C = { 0.93, 0.97, 1.04 }, RIM_C = { 0.55, 0.65, 0.85 };
            const double L_AMB = 0.035, L_KEY = 1.5, L_FILL = 0.08, L_RIM = 0.3, L_SPEC = 0.04;

            // ---- Hq (experimental): shadow map of the key light (orthographic, cut-outs respected)
            const int SM = 2048;
            float[] sm = null, smh = null; // solid occluders / hair (hair lets part of the light through)
            double[] LX = null, LY = null;
            double lx0 = 0, ly0 = 0, lsc = 1;
            if (opt.Hq)
            {
                LX = Norm(KEY[2], 0, -KEY[0]); // horizontal, perpendicular to the key
                LY = new[] { KEY[1] * LX[2] - KEY[2] * LX[1], KEY[2] * LX[0] - KEY[0] * LX[2], KEY[0] * LX[1] - KEY[1] * LX[0] };
                double D(double[] a, double x, double y, double z) => a[0] * x + a[1] * y + a[2] * z;
                double mx0 = 1e9, mx1 = -1e9, my0 = 1e9, my1 = -1e9;
                foreach (var t in tris)
                    foreach (var p in new[] { new[] { t.X0, t.Y0, t.Z0 }, new[] { t.X1, t.Y1, t.Z1 }, new[] { t.X2, t.Y2, t.Z2 } })
                    {
                        double a = D(LX, p[0], p[1], p[2]), c = D(LY, p[0], p[1], p[2]);
                        mx0 = Math.Min(mx0, a); mx1 = Math.Max(mx1, a); my0 = Math.Min(my0, c); my1 = Math.Max(my1, c);
                    }
                lsc = (SM - 4) / Math.Max(mx1 - mx0, my1 - my0); lx0 = mx0; ly0 = my0;
            }

            // Hq on the graphics card (Direct3D 11, or Windows' software device): same scene, same shading; null = not
            // available, drawn below on the CPU
            float[] gpuImg = null;
            if (opt.Hq && opt.Gpu)
            {
                var fr = new GpuFrame
                {
                    IW = IW, IH = IH, Persp = cam != null, Fpx = fpx, Cx = cx, Cy = cyy, Sc = sc, ShX = SHX, ShY = SHY,
                    Key = KEY, Fill = FILL, KeyC = KEY_C, RimC = RIM_C, LKey = L_KEY, MnY = mnY, MxY = mxY, Dark = opt.Dark,
                    LX = LX, LY = LY, Lx0 = lx0, Ly0 = ly0, Lsc = lsc, SM = SM,
                };
                var gsw = System.Diagnostics.Stopwatch.StartNew();
                try { gpuImg = DrawGpu(tris, fr, out GpuDevice); GpuDevice += $" ({gsw.ElapsedMilliseconds} ms)"; } catch (Exception e) { GpuDevice = "failed: " + e.Message; gpuImg = null; }
            }
            if (opt.Hq && gpuImg == null)
            {
                double D(double[] a, double x, double y, double z) => a[0] * x + a[1] * y + a[2] * z;
                sm = new float[SM * SM]; smh = new float[SM * SM];
                for (int i = 0; i < sm.Length; i++) sm[i] = smh[i] = -1e9f;
                var ss4 = new double[4];
                foreach (var t in tris)
                {
                    double ax = (D(LX, t.X0, t.Y0, t.Z0) - lx0) * lsc + 2, ay = (D(LY, t.X0, t.Y0, t.Z0) - ly0) * lsc + 2;
                    double bx = (D(LX, t.X1, t.Y1, t.Z1) - lx0) * lsc + 2, by = (D(LY, t.X1, t.Y1, t.Z1) - ly0) * lsc + 2;
                    double qx = (D(LX, t.X2, t.Y2, t.Z2) - lx0) * lsc + 2, qy = (D(LY, t.X2, t.Y2, t.Z2) - ly0) * lsc + 2;
                    double az = D(KEY, t.X0, t.Y0, t.Z0), bz = D(KEY, t.X1, t.Y1, t.Z1), qz = D(KEY, t.X2, t.Y2, t.Z2);
                    double d = (by - qy) * (ax - qx) + (qx - bx) * (ay - qy);
                    if (Math.Abs(d) < 1e-12) continue;
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, qx)))), x1 = Math.Min(SM - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, qx))));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, qy)))), y1 = Math.Min(SM - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, qy))));
                    var M = t.M;
                    var map = M.HairAlpha ? smh : sm;
                    bool cut = M.Atos != null || (M.Color != null && (M.Color.Alpha || M.HairAlpha));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            double px = x + .5, py = y + .5;
                            double w0 = ((by - qy) * (px - qx) + (qx - bx) * (py - qy)) / d;
                            double w1 = ((qy - ay) * (px - qx) + (ax - qx) * (py - qy)) / d, w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            double zz = w0 * az + w1 * bz + w2 * qz;
                            int i = y * SM + x;
                            if (zz <= map[i]) continue;
                            if (cut)
                            {
                                double tu = w0 * t.U0 + w1 * t.U1 + w2 * t.U2, tv = w0 * t.V0 + w1 * t.V1 + w2 * t.V2;
                                if (M.Atos != null) { Sample(M.Atos, tu, tv, ss4); if (ss4[0] < 128) continue; }
                                if (M.Color != null && (M.Color.Alpha || M.HairAlpha)) { Sample(M.Color, tu, tv, ss4); if (ss4[3] < 128) continue; }
                            }
                            map[i] = (float)zz;
                        }
                }
            }

            // fraction of the key light reaching a point (7x7 PCF; hair shadows at about half strength)
            double Lit(double x, double y, double z)
            {
                double sx = (LX[0] * x + LX[1] * y + LX[2] * z - lx0) * lsc + 2, sy = (LY[0] * x + LY[1] * y + LY[2] * z - ly0) * lsc + 2;
                double sz = KEY[0] * x + KEY[1] * y + KEY[2] * z, bias = 0.012;
                int cx0 = (int)sx, cy0 = (int)sy, lit = 0, hair = 0, n = 0;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int xx = cx0 + dx * 2, yy = cy0 + dy * 2;
                        n++;
                        if (xx < 0 || yy < 0 || xx >= SM || yy >= SM) { lit++; continue; }
                        if (sz + bias >= sm[yy * SM + xx]) lit++;
                        if (sz + bias < smh[yy * SM + xx]) hair++;
                    }
                return lit / (double)n * (1 - 0.45 * hair / n);
            }

            var hs4 = new double[4];
            // Hq shading: normal map in tangent space, GGX specular with the roughness map, metal from the color alpha,
            // occlusion (ATOS blue), key-light shadow, wrapped red-tinted diffuse on skin, sky/ground ambient. Linear space.
            void ShadeHq(Tri t, Mat M, double w0, double w1, double w2, double tu, double tv, double[] ng, bool flip,
                double vx, double vy, double vz, double ao, double colA, double[] alb, float[] im, int pix)
            {
                double rough = 0.75;
                double[] N = ng, Bv = null;
                if (M.Nrm != null && t.T != null)
                {
                    Sample(M.Nrm, tu, tv, hs4);
                    double mx = hs4[0] / 127.5 - 1, my = hs4[1] / 127.5 - 1, mz = Math.Sqrt(Math.Max(0, 1 - mx * mx - my * my));
                    rough = Math.Max(0.08, hs4[3] / 255);
                    var T = t.T;
                    double tx = w0 * T[0] + w1 * T[3] + w2 * T[6], ty = w0 * T[1] + w1 * T[4] + w2 * T[7], tz = w0 * T[2] + w1 * T[5] + w2 * T[8];
                    if (flip) { tx = -tx; ty = -ty; tz = -tz; }
                    double dn = tx * ng[0] + ty * ng[1] + tz * ng[2]; // Gram-Schmidt
                    var Tn = Norm(tx - ng[0] * dn, ty - ng[1] * dn, tz - ng[2] * dn);
                    double sg = (w0 * t.Sg0 + w1 * t.Sg1 + w2 * t.Sg2) >= 0 ? 1 : -1;
                    double bx = (ng[1] * Tn[2] - ng[2] * Tn[1]) * sg, by = (ng[2] * Tn[0] - ng[0] * Tn[2]) * sg, bz = (ng[0] * Tn[1] - ng[1] * Tn[0]) * sg;
                    Bv = Norm(bx, by, bz);
                    N = Norm(Tn[0] * mx + bx * my + ng[0] * mz, Tn[1] * mx + by * my + ng[1] * mz, Tn[2] * mx + bz * my + ng[2] * mz);
                }
                bool cutAlpha = M.Color != null && (M.Color.Alpha || M.HairAlpha);
                double metal = cutAlpha || M.Color == null || M.Color.NoAlpha ? 0 : colA / 255; // color alpha = metalness (game: about 10/255 on skin and cloth)
                double a2 = Math.Pow(rough * rough, 2);
                double nv = Math.Max(1e-3, N[2]);
                double shadow = Lit(vx, vy, vz);
                if (M.Eye) shadow = 0.55 + 0.45 * shadow;
                // skin shading: skin materials by name, elsewhere by the colour (skin painted on a "shirt" or "jacket" material
                // must be lit like the face next to it, or the seam shows)
                double skin = M.Skin ? 1 : M.HairAlpha || M.Eye || M.Color == null || M.Spec2 != null ? 0
                    : SkinTone(Math.Pow(alb[0], 1 / 2.2), Math.Pow(alb[1], 1 / 2.2), Math.Pow(alb[2], 1 / 2.2));
                var lin = new double[3];
                void Light(double[] L, double[] lc, double inten, double sh, bool spec)
                {
                    double nl = N[0] * L[0] + N[1] * L[1] + N[2] * L[2];
                    for (int k = 0; k < 3; k++)
                    {
                        double diff;
                        double wr = k == 0 ? 0.5 : k == 1 ? 0.3 : 0.25;
                        diff = Math.Max(0, nl) * (1 - skin) + Math.Max(0, (nl + wr) / (1 + wr)) * skin;
                        lin[k] += alb[k] * (1 - metal) * diff * inten * lc[k] * sh;
                    }
                    if (!spec || nl <= 0) return;
                    var H = Norm(L[0], L[1], L[2] + 1);
                    if (M.Spec2 != null && Bv != null && M.Sharp1 > 0)
                    {
                        // hair: two strand highlights (Kajiya-Kay) along the strand direction, shifted along the normal
                        double Strand(double shift, double sharp)
                        {
                            var Ts = Norm(Bv[0] + N[0] * shift, Bv[1] + N[1] * shift, Bv[2] + N[2] * shift);
                            double th = Ts[0] * H[0] + Ts[1] * H[1] + Ts[2] * H[2];
                            return Math.Pow(Math.Sqrt(Math.Max(0, 1 - th * th)), sharp);
                        }
                        double p1 = Strand(-M.Shift * 0.5, M.Sharp1), p2 = Strand(M.Shift * 0.5, Math.Max(1, M.Sharp2));
                        double att = Math.Min(1, nl * 2);
                        for (int k = 0; k < 3; k++)
                            lin[k] += (M.Spec1[k] * p1 * 0.6 + M.Spec2[k] * alb[k] * p2 * 10) * M.SpecInt * att * inten * lc[k] * sh; // (the broad one takes the hair color)
                        return;
                    }
                    double nh = Math.Max(0, N[0] * H[0] + N[1] * H[1] + N[2] * H[2]), vh = Math.Max(0, H[2]);
                    double dd = nh * nh * (a2 - 1) + 1, Dg = a2 / (Math.PI * dd * dd);
                    double kk = (rough + 1) * (rough + 1) / 8, G = nl / (nl * (1 - kk) + kk) * (nv / (nv * (1 - kk) + kk));
                    double fw = Math.Pow(1 - vh, 5);
                    for (int k = 0; k < 3; k++)
                    {
                        double f0 = 0.04 * (1 - metal) + alb[k] * metal, F = f0 + (1 - f0) * fw;
                        lin[k] += Dg * G * F / (4 * nl * nv) * nl * inten * lc[k] * sh * Math.PI * (0.15 - 0.08 * skin);
                    }
                }
                // studio look of the game's promotional renders: soft key from the front and a bit above, two white rim
                // lights from behind on both sides (bright edges against the black), little fill: dark sides and folds
                double[] white = { 1.0, 1.0, 1.0 };
                if (opt.Dark)
                {
                    // "strong shadows": key from the right side, almost black shadows, fill and a rim light from the left
                    Light(KEY, KEY_C, L_KEY * 0.9, 0.12 + 0.88 * shadow, true);
                    Light(Norm(-FILL[0], FILL[1], FILL[2]), white, 0.15, 1, false);
                    Light(Norm(-0.75, 0.35, -0.55), RIM_C, 0.5, 1, true);
                }
                else
                {
                    // the key is a spot from above: full on the face, fading down the body
                    double hgt = Math.Max(0, Math.Min(1, (vy - mnY) / Math.Max(1e-6, mxY - mnY)));
                    double spot = 0.15 + 0.85 * Math.Pow(hgt, 2.5);
                    Light(KEY, KEY_C, L_KEY * 0.9 * spot, 0.2 + 0.8 * shadow, true);
                    Light(FILL, white, 0.06, 1, false);
                    Light(Norm(0.85, 0.2, -0.5), white, 1.3, 1, true);
                    Light(Norm(-0.85, 0.2, -0.5), white, 1.3, 1, true);
                    double edge = Math.Pow(1 - Math.Abs(ng[2]), 6) * 0.5 * ao; // thin fresnel edge right on the silhouette
                    for (int k = 0; k < 3; k++) lin[k] += edge * (0.05 + alb[k]);
                }
                // sky (above) / ground (below) ambient and a dim reflection of it
                double up = N[1] * 0.5 + 0.5;
                double[] amb = { 0.04 + 0.05 * up, 0.045 + 0.055 * up, 0.055 + 0.07 * up };
                double fe = Math.Pow(1 - nv, 5) * (1 - rough);
                for (int k = 0; k < 3; k++)
                {
                    double f0 = 0.04 * (1 - metal) + alb[k] * metal;
                    lin[k] += alb[k] * metal * (0.35 + 0.45 * up) * (1 - rough * 0.5) * ao; // metal reflects a light grey surround
                    lin[k] += (alb[k] * (1 - metal) * amb[k] * 1.6 + (f0 + (1 - f0) * fe) * amb[k] * 0.8 * (1 - rough * 0.7)) * ao;
                    lin[k] *= 0.4 + 0.6 * ao;
                }
                if (M.Emis != null) for (int k = 0; k < 3; k++) lin[k] = M.Emis[k] * 1.6 + lin[k] * 0.03; // a lit lamp shows its own colour
                double lum = 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
                for (int k = 0; k < 3; k++)
                {
                    double l = opt.Dark ? lin[k] : lum + (lin[k] - lum) * 0.92; // studio: slightly desaturated, like the reference renders
                    im[pix * 4 + k] = (float)Math.Min(255, Math.Max(0, ToSrgb(l / (1 + l * 0.15))));
                }
                im[pix * 4 + 3] = 255;
            }

            var img = gpuImg ?? new float[IW * IH * 4];
            var zb = gpuImg != null ? null : new float[IW * IH];
            if (zb != null) for (int i = 0; i < zb.Length; i++) zb[i] = -1e9f;
            var s4 = new double[4];
            var lin = new double[3];
            var col = new double[3];
            foreach (var t in gpuImg != null ? new List<Tri>() : tris)
            {
                if (cam != null && (t.Z0 > -0.01 || t.Z1 > -0.01 || t.Z2 > -0.01)) continue; // behind the camera
                double ax = PX(t.X0, t.Z0), ay = PY(t.Y0, t.Z0), bx = PX(t.X1, t.Z1), by = PY(t.Y1, t.Z1), qx = PX(t.X2, t.Z2), qy = PY(t.Y2, t.Z2);
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, qx)))), x1 = Math.Min(IW - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, qx))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, qy)))), y1 = Math.Min(IH - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, qy))));
                double d = (by - qy) * (ax - qx) + (qx - bx) * (ay - qy);
                if (Math.Abs(d) < 1e-12) continue;
                var M = t.M;
                if (M.Cull && t.N != null)
                {
                    // back-facing skin, by the normals against the direction to the camera (as on the GPU; the winding differs between meshes)
                    double nx = t.N[0] + t.N[3] + t.N[6], ny = t.N[1] + t.N[4] + t.N[7], nz = t.N[2] + t.N[5] + t.N[8];
                    double vx = 0, vy = 0, vz = 1;
                    if (cam != null) { vx = -(t.X0 + t.X1 + t.X2); vy = -(t.Y0 + t.Y1 + t.Y2); vz = -(t.Z0 + t.Z1 + t.Z2); }
                    double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz) * Math.Sqrt(vx * vx + vy * vy + vz * vz);
                    if (ln > 0 && (nx * vx + ny * vy + nz * vz) / ln < -0.15) continue;
                }
                var n = t.N;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + .5, py = y + .5;
                        double w0 = ((by - qy) * (px - qx) + (qx - bx) * (py - qy)) / d;
                        double w1 = ((qy - ay) * (px - qx) + (ax - qx) * (py - qy)) / d, w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        double z = w0 * t.Z0 + w1 * t.Z1 + w2 * t.Z2;
                        int i = y * IW + x;
                        if (z <= zb[i]) continue;
                        double tu = w0 * t.U0 + w1 * t.U1 + w2 * t.U2, tv = w0 * t.V0 + w1 * t.V1 + w2 * t.V2;
                        double ao = 1, colA = 255;
                        if (M.Atos != null) { Sample(M.Atos, tu, tv, s4); if (s4[0] < 128) continue; ao = s4[2] / 255; }
                        if (M.Color != null)
                        {
                            Sample(M.Color, tu, tv, s4);
                            if ((M.Color.Alpha || M.HairAlpha) && s4[3] < 128) continue;
                            colA = s4[3];
                            for (int k = 0; k < 3; k++) col[k] = M.Tint != null ? ToLin(s4[k]) * M.Tint[k] : ToLin(s4[k]);
                        }
                        else if (M.Tint != null) for (int k = 0; k < 3; k++) col[k] = Math.Min(1, M.Tint[k]); // (a plain colour: lamps)
                        else col[0] = col[1] = col[2] = ToLin(150);
                        zb[i] = (float)z;
                        // light (two-sided: the meshes are not closed)
                        var nn = Norm(w0 * n[0] + w1 * n[3] + w2 * n[6], w0 * n[1] + w1 * n[4] + w2 * n[7], w0 * n[2] + w1 * n[5] + w2 * n[8]);
                        bool flip = nn[2] < 0;
                        if (flip) { nn[0] = -nn[0]; nn[1] = -nn[1]; nn[2] = -nn[2]; }
                        if (opt.Hq)
                        {
                            double vx = w0 * t.X0 + w1 * t.X1 + w2 * t.X2, vy = w0 * t.Y0 + w1 * t.Y1 + w2 * t.Y2;
                            ShadeHq(t, M, w0, w1, w2, tu, tv, nn, flip, vx, vy, z, ao, colA, col, img, i);
                            continue;
                        }
                        double dk = nn[0] * KEY[0] + nn[1] * KEY[1] + nn[2] * KEY[2];
                        double key = Math.Max(0, (dk + 0.15) / 1.15);
                        double fil = Math.Max(0, nn[0] * FILL[0] + nn[1] * FILL[1] + nn[2] * FILL[2]);
                        double rim = Math.Pow(1 - Math.Abs(nn[2]), 4);
                        double spec = L_SPEC * Math.Pow(Math.Max(0, nn[0] * HALF[0] + nn[1] * HALF[1] + nn[2] * HALF[2]), 20);
                        for (int k = 0; k < 3; k++)
                        {
                            double l = col[k] * (L_AMB + L_KEY * key * KEY_C[k] + L_FILL * fil + L_RIM * rim * RIM_C[k]) + spec;
                            if (M.Emis != null) l = M.Emis[k] * 1.6 + l * 0.03;
                            img[i * 4 + k] = (float)Math.Min(255, Math.Max(0, ToSrgb(l / (1 + l * 0.15)))); // soft roll-off of the highlights
                        }
                        img[i * 4 + 3] = 255;
                    }
            }

            // reduce SS x SS -> 1 (colors weighted by coverage: smooth edges over a transparent background)
            var outp = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double r = 0, g = 0, bl = 0, al = 0;
                    for (int dy = 0; dy < SS; dy++)
                        for (int dx = 0; dx < SS; dx++)
                        {
                            int q = ((y * SS + dy) * IW + x * SS + dx) * 4;
                            double a = img[q + 3] / 255;
                            r += img[q] * a; g += img[q + 1] * a; bl += img[q + 2] * a; al += a;
                        }
                    int o = (y * W + x) * 4;
                    if (al > 0)
                    {
                        outp[o] = (byte)(r / al); outp[o + 1] = (byte)(g / al); outp[o + 2] = (byte)(bl / al);
                        outp[o + 3] = (byte)(255 * al / (SS * SS));
                    }
                }
            if (opt.Re3) { FillHoles(outp, W, H, Math.Max(3, W / 72)); GlowAround(outp, W, H); }
            else if (opt.Hq && !opt.Dark) GlowAround(outp, W, H); // soft light halo around the figure, like the promotional renders
            return outp;
        }

        // A background pixel with figure on all four sides within r pixels is a hole: it takes the average color of those
        // neighbours. (Gaps wider than 2 r, such as between an arm and the body, stay.)
        private static void FillHoles(byte[] px, int w, int h, int r)
        {
            var src = (byte[])px.Clone();
            bool Solid(int x, int y) => src[(y * w + x) * 4 + 3] >= 250;
            var found = new int[4][];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (src[(y * w + x) * 4 + 3] >= 250) continue;
                    int[] dx = { -1, 1, 0, 0 }, dy = { 0, 0, -1, 1 };
                    int sr = 0, sg = 0, sb = 0, n = 0;
                    for (int d = 0; d < 4; d++)
                    {
                        bool ok = false;
                        for (int k = 1; k <= r && !ok; k++)
                        {
                            int nx = x + dx[d] * k, ny = y + dy[d] * k;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) break;
                            if (Solid(nx, ny)) { int q = (ny * w + nx) * 4; sr += src[q]; sg += src[q + 1]; sb += src[q + 2]; n++; ok = true; }
                        }
                        if (!ok) { n = -1; break; }
                    }
                    if (n != 4) continue;
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(sr / 4); px[o + 1] = (byte)(sg / 4); px[o + 2] = (byte)(sb / 4); px[o + 3] = 255;
                }
        }

        // RE3 menu pictures keep their transparency: the figure, and around it a soft glow that is semi-transparent white-blue
        // (alpha = how much glow), like the game's own pictures (RGB under full transparency = 0)
        private static void GlowAround(byte[] px, int w, int h)
        {
            var a = new float[w * h];
            for (int i = 0; i < a.Length; i++) a[i] = px[i * 4 + 3] / 255f;
            var tmp = new float[w * h];
            // (3 passes: the halo reaches about 2.5 r). The game's glow is very subtle: a thin, faint light edge
            int r = Math.Max(1, w / 256);
            for (int pass = 0; pass < 3; pass++)
            {
                for (int y = 0; y < h; y++) // horizontal box blur
                {
                    float sum = 0;
                    for (int x = -r; x <= r; x++) sum += a[y * w + Math.Min(w - 1, Math.Max(0, x))];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[y * w + x] = sum / (2 * r + 1);
                        sum += a[y * w + Math.Min(w - 1, x + r + 1)] - a[y * w + Math.Max(0, x - r)];
                    }
                }
                for (int x = 0; x < w; x++) // vertical
                {
                    float sum = 0;
                    for (int y = -r; y <= r; y++) sum += tmp[Math.Min(h - 1, Math.Max(0, y)) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        a[y * w + x] = sum / (2 * r + 1);
                        sum += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x];
                    }
                }
            }
            double[] gc = { 235, 240, 255 };
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                double cov = px[o + 3] / 255.0, glow = Math.Min(1.0, a[i] * 1.0) * 0.35;
                double al = cov + (1 - cov) * glow; // the figure over the glow
                if (al <= 0.003) { px[o] = px[o + 1] = px[o + 2] = 0; px[o + 3] = 0; continue; }
                // colour already multiplied by the glow (not divided by the alpha): the RE3 menu does not fully honour the
                // transparency, and a pure white edge looked like a strong outline there
                for (int k = 0; k < 3; k++) px[o + k] = (byte)Math.Min(255, cov >= 0.999 ? px[o + k] : px[o + k] * cov + gc[k] * glow * (1 - cov));
                px[o + 3] = (byte)Math.Min(255, al * 255);
            }
        }

        /// <summary>Saves RGBA as PNG (keeps the transparency).</summary>
        public static void SavePng(byte[] rgba, int w, int h, string file)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var bgra = new byte[rgba.Length];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
                }
                for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
                bmp.UnlockBits(data);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
                bmp.Save(file, ImageFormat.Png);
            }
        }
    }
}
