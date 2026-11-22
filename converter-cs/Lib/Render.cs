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
        public double Frame = 0.5, Fill = 0.92; // part of the height shown from the top; share of the picture the figure fills
        public static readonly RenderOptions Re2 = new RenderOptions();
        public static readonly RenderOptions Re3Jill = new RenderOptions { Re3 = true, Frame = 0.62, Fill = 0.86 };
    }

    public static class OutfitRender
    {
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
                using (var s = typeof(OutfitRender).Assembly.GetManifestResourceStream("pose." + character))
                    if (s != null) using (var r = new StreamReader(s)) text = r.ReadToEnd();
                if (text != null)
                {
                    p = new Dictionary<string, double[]>();
                    // { "bones": { "<name>": { "rot": [9 numbers], "rest": [...] }, ... } }
                    foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\\{\\s*\"rot\"\\s*:\\s*\\[([^\\]]*)\\]"))
                        p[m.Groups[1].Value] = m.Groups[2].Value.Split(',').Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                }
                PoseCache[character] = p;
                return p;
            }
        }

        public static bool HasPose(string character) => Pose(character) != null;

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
            public byte[] Px;
            public bool Alpha; // *_ALBA: alpha is a cut-out
        }

        private sealed class Mat
        {
            public Tex Color, Atos;
            public double[] Tint;
            public bool HairAlpha, Cull;
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
                return new Tex { W = w, H = h, Px = px, Alpha = Regex.IsMatch(Regex.Replace(path, @"\.tex$", "", RegexOptions.IgnoreCase), "_alba$", RegexOptions.IgnoreCase) };
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
        private static double[][] BaseColors(byte[] b, bool re3 = false)
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
                    if (Encoding.Unicode.GetString(b, nameOff, end - nameOff) != "BaseColor") continue;
                    int comp = (int)BitConverter.ToUInt32(b, h + (re3 ? 0x14 : 0x10)), rel = (int)BitConverter.ToUInt32(b, h + (re3 ? 0x10 : 0x14)); // (v13: offset first, then the count)
                    if (comp < 3) break;
                    res[i] = new double[] { BitConverter.ToSingle(b, (int)(pd + rel)), BitConverter.ToSingle(b, (int)(pd + rel + 4)), BitConverter.ToSingle(b, (int)(pd + rel + 8)) };
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
                if (!isBody && bodyS.TryGetValue(nm[i], out var bs)) { S[i] = bs; Q[i] = bodyQ[nm[i]]; return; }
                int p = par[i] >= 0 && par[i] < n && !busy[par[i]] ? par[i] : -1;
                busy[i] = true; // only while solving its parents: guards against a loop in the hierarchy
                if (p >= 0) Solve(p);
                busy[i] = false;
                double[] q = null;
                if (pose.TryGetValue(nm[i], out var rot) && rot.Length >= 9)
                    q = new[] { rot[0], rot[1], rot[2], 0, rot[3], rot[4], rot[5], 0, rot[6], rot[7], rot[8], 0, 0, 0, 0, 1 };
                else if (p >= 0) q = Q[p];
                if (q == null) { S[i] = Ident; Q[i] = null; return; }
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
            if (isBody) for (int i = 0; i < n; i++) { bodyS[nm[i]] = S[i]; bodyQ[nm[i]] = Q[i]; }
            var map = new int[mapN];
            for (int i = 0; i < mapN; i++) map[i] = BitConverter.ToUInt16(b, (int)bo + 0x30 + i * 2);
            return new Skin { S = S, Map = map };
        }

        // ------------------------------------------------------------------ render
        private struct Tri
        {
            public float X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2;   // view space
            public float[] N;                                  // 9: normals of the corners (view space)
            public float U0, V0, U1, V1, U2, V2;
            public Mat M;
        }

        /// <summary>Draws the parts (body first) and returns the picture as RGBA (w x h), transparent background.</summary>
        public static byte[] Render(List<RenderPart> parts, Dictionary<string, double[]> pose, int W = 1024, int H = 1024, RenderOptions opt = null)
        {
            opt = opt ?? RenderOptions.Re2;
            const int SS = 3;
            double yaw = -25 * Math.PI / 180, cy = Math.Cos(yaw), sy = Math.Sin(yaw);
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
                var texCache = new Dictionary<string, Tex>(StringComparer.OrdinalIgnoreCase);
                Tex Texture(string p)
                {
                    if (!texCache.TryGetValue(p, out var t)) texCache[p] = t = LoadTex(part.Texture(p), p);
                    return t;
                }
                var matOf = new Mat[matCount]; // null = not drawn
                for (int i = 0; i < matCount; i++)
                {
                    string name = Str(U64(names + BitConverter.ToUInt16(b, (int)(matIdx + i * 2)) * 8));
                    int mi = mats.FindIndex(x => x.Name == name);
                    if (mi < 0 || Regex.IsMatch(name, "sling", RegexOptions.IgnoreCase)) continue; // the weapon sling only shows with some weapons
                    var m = mats[mi];
                    var ct = ColorTypes.Select(ty => m.Textures.FirstOrDefault(x => x.Type == ty)).FirstOrDefault(x => x != null && !Regex.IsMatch(x.Path, "null", RegexOptions.IgnoreCase));
                    if (ct == null) continue; // cornea, tear line...
                    var at = m.Textures.FirstOrDefault(x => x.Type == "AlphaTranslucentOcclusionSSSMap" && !Regex.IsMatch(x.Path, "null", RegexOptions.IgnoreCase));
                    var bc = colors[mi];
                    matOf[i] = new Mat
                    {
                        Color = Texture(ct.Path),
                        Atos = at != null ? Texture(at.Path) : null,
                        // white on most materials; the actual color on hair, beard and brows (their textures are a light base)
                        Tint = bc != null && bc.Any(v => Math.Abs(v - 1) > 0.001) ? bc : null,
                        // hair shaders: the alpha of the color texture is the coverage (elsewhere it is metalness)
                        HairAlpha = !opt.Re3 && Regex.IsMatch(m.Mmtr ?? "", "hair", RegexOptions.IgnoreCase), // (RE3: the alpha of the hair texture is not a cut-out)
                        Cull = opt.Re3 && Regex.IsMatch(name, "Face_Mat", RegexOptions.IgnoreCase),
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
                            px = ax; py = ay; pz = az; nx = bx; ny = by; nz = bz;
                        }
                    }
                    int uo = (int)(vb + el[2] + i * 4);
                    // view: turned around Y
                    r = new float[]
                    {
                        (float)(px * cy + pz * sy), (float)py, (float)(-px * sy + pz * cy),
                        (float)(nx * cy + nz * sy), (float)ny, (float)(-nx * sy + nz * cy),
                        Half(BitConverter.ToUInt16(b, uo)), Half(BitConverter.ToUInt16(b, uo + 2)),
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
            double PX(double x) => IW / 2.0 + SHX + (x - cx) * sc;
            double PY(double y) => IH / 2.0 + SHY - (y - cyy) * sc;

            // lights in view space (x right, y up, z towards the camera): a strong, slightly cool key from the front-left
            // and above, little fill, a dark ambient, a cool rim on the edges and a soft highlight. Linear space.
            double[] Norm(double x, double y, double z) { double l = Math.Sqrt(x * x + y * y + z * z); if (l == 0) l = 1; return new[] { x / l, y / l, z / l }; }
            var KEY = Norm(-0.6, 0.55, 0.6); var FILL = Norm(0.75, 0.05, 0.45); var HALF = Norm(-0.6, 0.55, 1.6);
            double[] KEY_C = { 0.93, 0.97, 1.04 }, RIM_C = { 0.55, 0.65, 0.85 };
            const double L_AMB = 0.035, L_KEY = 1.5, L_FILL = 0.08, L_RIM = 0.3, L_SPEC = 0.04;

            var img = new float[IW * IH * 4];
            var zb = new float[IW * IH];
            for (int i = 0; i < zb.Length; i++) zb[i] = -1e9f;
            var s4 = new double[4];
            var lin = new double[3];
            var col = new double[3];
            foreach (var t in tris)
            {
                double ax = PX(t.X0), ay = PY(t.Y0), bx = PX(t.X1), by = PY(t.Y1), qx = PX(t.X2), qy = PY(t.Y2);
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, qx)))), x1 = Math.Min(IW - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, qx))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, qy)))), y1 = Math.Min(IH - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, qy))));
                double d = (by - qy) * (ax - qx) + (qx - bx) * (ay - qy);
                if (Math.Abs(d) < 1e-12) continue;
                var M = t.M;
                if (M.Cull && d > 0) continue; // back-facing skin
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
                        if (M.Atos != null) { Sample(M.Atos, tu, tv, s4); if (s4[0] < 128) continue; }
                        if (M.Color != null)
                        {
                            Sample(M.Color, tu, tv, s4);
                            if ((M.Color.Alpha || M.HairAlpha) && s4[3] < 128) continue;
                            for (int k = 0; k < 3; k++) col[k] = M.Tint != null ? ToLin(s4[k]) * M.Tint[k] : ToLin(s4[k]);
                        }
                        else col[0] = col[1] = col[2] = ToLin(150);
                        zb[i] = (float)z;
                        // light (two-sided: the meshes are not closed)
                        var nn = Norm(w0 * n[0] + w1 * n[3] + w2 * n[6], w0 * n[1] + w1 * n[4] + w2 * n[7], w0 * n[2] + w1 * n[5] + w2 * n[8]);
                        if (nn[2] < 0) { nn[0] = -nn[0]; nn[1] = -nn[1]; nn[2] = -nn[2]; }
                        double dk = nn[0] * KEY[0] + nn[1] * KEY[1] + nn[2] * KEY[2];
                        double key = Math.Max(0, (dk + 0.15) / 1.15);
                        double fil = Math.Max(0, nn[0] * FILL[0] + nn[1] * FILL[1] + nn[2] * FILL[2]);
                        double rim = Math.Pow(1 - Math.Abs(nn[2]), 4);
                        double spec = L_SPEC * Math.Pow(Math.Max(0, nn[0] * HALF[0] + nn[1] * HALF[1] + nn[2] * HALF[2]), 20);
                        for (int k = 0; k < 3; k++)
                        {
                            double l = col[k] * (L_AMB + L_KEY * key * KEY_C[k] + L_FILL * fil + L_RIM * rim * RIM_C[k]) + spec;
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
