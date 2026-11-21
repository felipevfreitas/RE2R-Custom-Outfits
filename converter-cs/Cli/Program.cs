// co-cli - command line for testing the converter.
//   co-cli convert <mod> <output> <slug> "<Name>" [game_folder]
//   co-cli analyze <archive_or_folder> [game_folder]
//   co-cli build <archive_or_folder> <output.zip> [game_folder]
//   co-cli re3tables <output_dir>   (RE3 costume tables for the script)
//   co-cli thumb <image> <out.tex.N> <version>
//   co-cli branding <logo.png> <project_root>   (logo, icon, Nexus images, game menu placeholder)
using System;
using System.IO;
using System.Linq;

namespace CustomOutfits.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                string cmd = args.Length > 0 ? args[0] : "";
                string Game(int i) => args.Length > i ? args[i] : Core.DetectGameDir();
                switch (cmd)
                {
                    case "convert":
                    {
                        var rep = Converter.Convert(args[1], args[2], args[3], args[4], new ConvertOptions { GameDir = Game(5) });
                        foreach (var u in rep.Units)
                            Console.WriteLine($"{u.Character,-7} {u.Part,-5} {u.Dir,-18} \"{u.V.Label}\" direct:[{string.Join(",", u.V.Direct)}] costumes:[{string.Join(",", u.V.Costumes)}] mdf2:{u.MdfSource}");
                        Console.WriteLine("copied: " + rep.Copied);
                        foreach (var w in rep.Warnings) Console.WriteLine("WARNING: " + w);
                        return 0;
                    }
                    case "analyze":
                    {
                        var p = Core.Analyze(args[1], Game(2));
                        Console.WriteLine("package: " + p.PackageName);
                        foreach (var o in p.Options)
                            foreach (var x in o.Outfits)
                                Console.WriteLine($"  {x.CharacterName} / {x.PartName} => \"{x.Name}\" [{string.Join(", ", x.Variants)}] slug {o.Slug}");
                        // drawn pictures (mods without an image): copied to CO_DRAW_OUT, if set
                        string drawOut = Environment.GetEnvironmentVariable("CO_DRAW_OUT");
                        foreach (var o in p.Options)
                            foreach (var x in o.Outfits.Where(x => x.Drawn != null))
                            {
                                Console.WriteLine("  drawn: " + x.Drawn);
                                if (drawOut != null) { Directory.CreateDirectory(drawOut); File.Copy(x.Drawn, Path.Combine(drawOut, o.Slug + "_" + x.Character + ".png"), true); }
                            }
                        foreach (var w in p.Warnings) Console.WriteLine("WARNING: " + w);
                        Core.Cleanup(p);
                        return 0;
                    }
                    case "build":
                    {
                        var p = Core.Analyze(args[1], Game(3));
                        // (tests) CO_EACH=1: one entry per outfit for every pack body; prints the outfits each body lists
                        foreach (var o in p.Options.SelectMany(x => x.Outfits))
                        {
                            if (Environment.GetEnvironmentVariable("CO_EACH") == "1" && o.Pickable.Count > 1) o.EachEntry = true;
                            if (o.Pickable.Count > 0) Console.WriteLine($"{o.Character} {o.Part}: {string.Join(", ", o.Pickable.Select(v => v.Slot + " [" + v.Key + "]"))}{(o.EachEntry ? " (one entry each)" : "")}");
                        }
                        var r = Core.Build(p, args[2], false);
                        Core.Cleanup(p);
                        Console.WriteLine($"ok: {r.Output} ({r.Outfits} outfits)");
                        foreach (var w in r.Warnings) Console.WriteLine("WARNING: " + w);
                        return 0;
                    }
                    case "paklock":
                    {
                        // paklock <game dir> <pak entry>: reads an entry, then checks that the pak can be opened exclusively (as Fluffy does)
                        if (!File.Exists(args[1])) { var gp = Converter.Paks(args[1]); var data = gp.Read(args[2]); Console.WriteLine("read " + (data == null ? "nothing" : data.Length + " bytes")); }
                        else { using (var tp = new Pak(args[1])) Console.WriteLine("opened a pak object (" + tp.Entries.Count + " entries)"); }
                        foreach (var pk in File.Exists(args[1]) ? new[] { args[1] } : Directory.GetFiles(args[1], "re_chunk_000.pak"))
                            using (new FileStream(pk, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Console.WriteLine("exclusive open of " + Path.GetFileName(pk) + ": OK");
                        return 0;
                    }
                    case "re3tables":
                    {
                        // RE3 costume tables for the script: reframework/data/CustomOutfits/game/*.json
                        Re3.WriteGameTables(args[1]);
                        Console.WriteLine("ok: " + string.Join(", ", Re3.Models.Select(m => m.Id)));
                        return 0;
                    }
                    case "thumb":
                    {
                        // co-cli thumb <image> <out.tex.N> <version> [alpha]   (alpha: keep transparency, BC3)
                        bool ok = Tex.WriteThumbnail(args[1], args[2], int.Parse(args[3]), out int w, out int h, 1024, args.Length > 4 && args[4] == "alpha");
                        Console.WriteLine(ok ? $"ok: {w}x{h}" : "image could not be read");
                        return ok ? 0 : 1;
                    }
                    case "draw":
                    {
                        // draw <config.json of research/render/preview> <out.png> [character]: the C# renderer on the
                        // prototype's parts (comparison with render_outfit.js)
                        string cfg = File.ReadAllText(args[1]);
                        var parts = new System.Collections.Generic.List<RenderPart>();
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(cfg, "\"mesh\":\"([^\"]+)\",\"mdf\":\"([^\"]+)\",\"roots\":\\[([^\\]]+)\\]"))
                        {
                            string U(string x) => x.Replace("\\\\", "\\");
                            var roots = m.Groups[3].Value.Split(',').Select(r => U(r.Trim('"'))).ToList();
                            parts.Add(new RenderPart
                            {
                                Mesh = File.ReadAllBytes(U(m.Groups[1].Value)), Mdf = File.ReadAllBytes(U(m.Groups[2].Value)),
                                Texture = t =>
                                {
                                    foreach (var r in roots)
                                        foreach (var c in new[] { Path.Combine(r, "streaming", t + ".10"), Path.Combine(r, t + ".10") })
                                            if (File.Exists(c)) return File.ReadAllBytes(c);
                                    return null;
                                },
                            });
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var rgba = OutfitRender.Render(parts, OutfitRender.Pose(args.Length > 3 ? args[3] : "claire"), 1024, 1024);
                        OutfitRender.SavePng(rgba, 1024, 1024, args[2]);
                        Console.WriteLine($"{parts.Count} parts, {sw.ElapsedMilliseconds} ms");
                        return 0;
                    }
                    case "texpng":
                    {
                        // co-cli texpng <file.tex.N> <out.png>: mip 0 of a texture as PNG (checking the decoder)
                        var px = TexDecode.Mip0(System.IO.File.ReadAllBytes(args[1]), out int w, out int h, out int fmt);
                        if (args[2].EndsWith(".rgba"))
                        {
                            // raw pixels for the render prototype: width (u32), height (u32), RGBA
                            using (var f = System.IO.File.Create(args[2]))
                            {
                                f.Write(BitConverter.GetBytes(w), 0, 4); f.Write(BitConverter.GetBytes(h), 0, 4); f.Write(px, 0, px.Length);
                            }
                            Console.WriteLine($"ok: {w}x{h} format {fmt}");
                            return 0;
                        }
                        using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            var bgra = new byte[px.Length];
                            for (int i = 0; i < px.Length; i += 4) { bgra[i] = px[i + 2]; bgra[i + 1] = px[i + 1]; bgra[i + 2] = px[i]; bgra[i + 3] = 255; }
                            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, d.Scan0, bgra.Length);
                            bmp.UnlockBits(d);
                            bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine($"ok: {w}x{h} format {fmt}");
                        return 0;
                    }
                    case "branding":
                    {
                        // co-cli branding <logo.png> <project_root>: every image with the logo (see Branding.cs)
                        Branding.Run(args[1], args[2]);
                        return 0;
                    }
                    default:
                        Console.WriteLine("usage: co-cli convert|analyze|build|re3tables|thumb|branding ...");
                        return 2;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        }
    }
}
