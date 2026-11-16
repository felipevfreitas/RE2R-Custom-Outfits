// Re3.cs - Resident Evil 3 Remake support: costume tables and conversion.
// RE3 differs from RE2 in the folders (natives/stm/escape/character/player/<group>/<unit>/mesh/), the file
// versions (mesh .1902042334, mdf2 .13, tex .190820018) and the costume prefabs, whose names don't follow one
// pattern (Jill's bodies live in prefab/character/player/, special models in prefab/character/survivor/parts/).
// So the tables below were written from the game (runtime probe + prefabs) instead of being read from it.
//
// Conversion: for every part of every costume in the tables, the outfit is created when the mod touches it
// (ships its mdf2, mesh, skin mesh or chain, or any texture its material uses). Textures are remapped
// mod-wide, since RE3 materials often use textures from other folders (pl2720 uses pl2020's).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CustomOutfits
{
    public static class Re3
    {
        public const string PlayerRoot = "escape/character/player";
        public static readonly string[] MdfVers = { "13", "21" };   // non-RT, RT (RT not verified)
        public static readonly string[] Platforms = { "stm", "x64" };
        private const string TexVer = "190820018", MmtrVer = "1905100741"; // non-RT

        /// <summary>A character model of the game: its costume table and the menu character it belongs to.</summary>
        public sealed class Model
        {
            public string Id, Menu, Group;
            public CostumeTable Table = new CostumeTable();
            public Dictionary<int, string> Labels = new Dictionary<int, string>();
        }

        private static CostumePart P(string mesh, string mdf, string skin, string chain)
        {
            string F(string s) => s == null ? null : (s.StartsWith("sectionroot/", StringComparison.OrdinalIgnoreCase) ? s : PlayerRoot + "/" + s).ToLowerInvariant();
            return new CostumePart { Mesh = F(mesh), Mdf = F(mdf), Skin = F(skin), Chain = F(chain) };
        }

        private static void Add(Model m, int costume, string label, CostumePart body, CostumePart face, CostumePart hair)
        {
            var parts = new Dictionary<string, CostumePart>();
            if (body != null) parts["body"] = body;
            if (face != null) parts["face"] = face;
            if (hair != null) parts["hair"] = hair;
            m.Table[costume] = parts;
            if (label != null) m.Labels[costume] = label;
        }

        public static readonly List<Model> Models = BuildModels();

        private static List<Model> BuildModels()
        {
            // Jill: runtime probe (costume number -> files); 1, 4 and 6-17 are not real costumes for her
            var jill = new Model { Id = "jill", Menu = "jill", Group = "pl2000" };
            var jFace = P("pl2000/pl2001/mesh/pl2001.mesh", "pl2000/pl2001/mesh/pl2001.mdf2", "pl2000/pl2001/mesh/pl2001_blend.mesh", null);
            var jHair = P("pl2000/pl2005/mesh/pl2005.mesh", "pl2000/pl2005/mesh/pl2005.mdf2", "pl2000/pl2005/mesh/pl2005.mesh", "pl2000/pl2005/mesh/pl2005.chain");
            Add(jill, 0, "Default", P("pl2000/mesh/pl2000.mesh", "pl2000/mesh/pl2000.mdf2", "pl2000/mesh/pl2000.mesh", "pl2000/mesh/pl2000.chain"), jFace, jHair);
            Add(jill, 2, "Default, injured", P("pl2000/mesh/pl2000.mesh", "pl2000/mesh/pl2000_01.mdf2", "pl2000/mesh/pl2000.mesh", "pl2000/mesh/pl2000.chain"),
                P("pl2000/pl2001/mesh/pl2001.mesh", "pl2000/pl2001/mesh/pl2001_01.mdf2", "pl2000/pl2001/mesh/pl2001_blend.mesh", null), jHair);
            Add(jill, 3, "S.T.A.R.S.", P("pl2000/pl2010/mesh/pl2010.mesh", "pl2000/pl2010/mesh/pl2010.mdf2", "pl2000/pl2010/mesh/pl2010.mesh", "pl2000/mesh/pl2000.chain"), jFace,
                P("pl2000/pl2015/mesh/pl2015.mesh", "pl2000/pl2015/mesh/pl2015.mdf2", "pl2000/pl2005/mesh/pl2005.mesh", "pl2000/pl2005/mesh/pl2005.chain"));
            Add(jill, 5, "Classic", P("pl2000/pl2020/mesh/pl2020.mesh", "pl2000/pl2020/mesh/pl2020.mdf2", "pl2000/pl2020/mesh/pl2020.mesh", "pl2000/pl2020/mesh/pl2020.chain"), jFace, jHair);

            // Carlos: prefabs player/pl0000 (00 default, 01 classic: same body, other face and hair)
            var carlos = new Model { Id = "carlos", Menu = "carlos", Group = "pl0000" };
            var cBody = P("pl0000/mesh/pl0000.mesh", "pl0000/mesh/pl0000.mdf2", "pl0000/mesh/pl0000.mesh", "pl0000/mesh/pl0000.chain");
            Add(carlos, 0, "Default", cBody, P("pl0000/pl0001/mesh/pl0001.mesh", "pl0000/pl0001/mesh/pl0001.mdf2", "pl0000/pl0001/mesh/pl0001_blend.mesh", null),
                P("pl0000/pl0005/mesh/pl0005.mesh", "pl0000/pl0005/mesh/pl0005.mdf2", "pl0000/pl0005/mesh/pl0005.mesh", "pl0000/pl0005/mesh/pl0005.chain"));
            Add(carlos, 1, "Classic", cBody, P("pl0000/pl0011/mesh/pl0011.mesh", "pl0000/pl0011/mesh/pl0011.mdf2", "pl0000/pl0001/mesh/pl0001_blend.mesh", null),
                P("pl0000/pl0015/mesh/pl0015.mesh", "pl0000/pl0015/mesh/pl0015.mdf2", "pl0000/pl0015/mesh/pl0015.mesh", "pl0000/pl0015/mesh/pl0015.chain"));

            // Jill after the infection (bandages, parasite damage): prefabs survivor/parts/pl2700, one body per outfit
            var inf = new Model { Id = "jill_infected", Menu = "jill", Group = "pl2700" };
            var iFace = P("pl2700/pl2701/mesh/pl2701.mesh", "pl2700/pl2701/mesh/pl2701.mdf2", "pl2700/pl2701/mesh/pl2701_blend.mesh", null);
            Add(inf, 0, "Infected", P("pl2700/mesh/pl2700.mesh", "pl2700/mesh/pl2700.mdf2", "pl2700/mesh/pl2700.mesh", "pl2000/mesh/pl2000.chain"), iFace, jHair);
            Add(inf, 1, "Infected, S.T.A.R.S.", P("pl2700/pl2710/mesh/pl2710.mesh", "pl2700/pl2710/mesh/pl2710.mdf2", "pl2700/pl2710/mesh/pl2710.mesh", "pl2000/mesh/pl2000.chain"), iFace,
                P("pl2000/pl2015/mesh/pl2015.mesh", "pl2000/pl2015/mesh/pl2015.mdf2", "pl2000/pl2015/mesh/pl2015.mesh", null));
            Add(inf, 2, "Infected, Classic", P("pl2700/pl2720/mesh/pl2720.mesh", "pl2700/pl2720/mesh/pl2720.mdf2", "pl2700/pl2720/mesh/pl2720.mesh", "pl2700/pl2720/mesh/pl2720.chain"), iFace, jHair);

            // Jill in the dream (zombie textures): prefabs survivor/parts/pl2910 (body 00-02, face, hair)
            var dream = new Model { Id = "jill_dream", Menu = "jill", Group = "pl2910" };
            var dFace = P("pl2910/pl2911/mesh/pl2911.mesh", "pl2910/pl2911/mesh/pl2911.mdf2", "pl2910/pl2911/mesh/pl2911_blend.mesh", null);
            var dHair = P("pl2900/pl2905/mesh/pl2905.mesh", "pl2900/pl2905/mesh/pl2905.mdf2", "pl2900/pl2905/mesh/pl2905.mesh", "pl2900/pl2905/mesh/pl2905.chain");
            Add(dream, 0, "Dream", P("pl2910/mesh/pl2910.mesh", "pl2910/mesh/pl2910.mdf2", "pl2910/mesh/pl2910.mesh", "pl2000/mesh/pl2000.chain"), dFace, dHair);
            Add(dream, 1, "Dream", P("pl2920/mesh/pl2920.mesh", "pl2920/mesh/pl2920.mdf2", "pl2920/mesh/pl2920.mesh", null), dFace,
                P("pl2920/pl2925/mesh/pl2925.mesh", "pl2920/pl2925/mesh/pl2925.mdf2", "pl2920/pl2925/mesh/pl2925.mesh", "pl2900/pl2905/mesh/pl2905.chain"));
            Add(dream, 2, "Dream", P("pl2930/mesh/pl2930.mesh", "pl2930/mesh/pl2930.mdf2", "pl2930/mesh/pl2930.mesh", "pl2000/pl2020/mesh/pl2020.chain"), dFace, dHair);

            // Jill in the mirror (prefabs survivor/parts/pl2800; face and hair are Jill's)
            var mirror = new Model { Id = "jill_mirror", Menu = "jill", Group = "pl2800" };
            const string claireChain = "sectionroot/character/player/pl1000/pl1000/pl1000.chain";
            foreach (var kv in new[] { (0, "pl2800"), (7, "pl2800"), (8, "pl2810"), (9, "pl2820") })
                Add(mirror, kv.Item1, "Mirror", P($"{kv.Item2}/mesh/{kv.Item2}.mesh", $"{kv.Item2}/mesh/{kv.Item2}.mdf2", $"{kv.Item2}/mesh/{kv.Item2}.mesh", claireChain), jFace, jHair);

            return new List<Model> { jill, carlos, inf, dream, mirror };
        }

        public static readonly Dictionary<string, string> CharNames = new Dictionary<string, string>
        {
            ["jill"] = "Jill", ["carlos"] = "Carlos",
        };

        // ------------------------------------------------------------------ game tables for the script
        /// <summary>Writes reframework/data/CustomOutfits/game/&lt;model&gt;.json for every model.</summary>
        public static void WriteGameTables(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var m in Models)
            {
                var costumes = new JObj();
                foreach (var c in m.Table)
                {
                    var parts = new JObj();
                    foreach (var p in new[] { "body", "face", "hair" })
                        if (c.Value.TryGetValue(p, out var cp)) parts.Set(p, cp.ToJson());
                    costumes.Set(c.Key.ToString(), parts);
                }
                var labels = new JObj();
                foreach (var l in m.Labels) labels.Set(l.Key.ToString(), l.Value);
                var j = new JObj().Set("character", m.Id).Set("group", m.Group).Set("labels", labels).Set("costumes", costumes);
                File.WriteAllText(Path.Combine(dir, m.Id + ".json"), Json.Stringify(j), new UTF8Encoding(false));
            }
        }

        // ------------------------------------------------------------------ conversion
        public static bool IsRe3Mod(string modRoot) =>
            Platforms.Any(pl => Directory.Exists(Path.Combine(modRoot, "natives", pl, "escape", "character", "player")));

        private static string StripVer(string n) => Regex.Replace(n, @"\.\d+$", "");
        private static string Os(string root, params string[] parts) =>
            Path.Combine(new[] { root }.Concat(parts.Select(x => x.Replace('/', Path.DirectorySeparatorChar))).ToArray());

        private static List<string> ListFiles(string dir, string rel = "")
        {
            var output = new List<string>();
            if (!Directory.Exists(dir)) return output;
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                string name = Path.GetFileName(e);
                string r = rel.Length > 0 ? rel + "/" + name : name;
                if (Directory.Exists(e)) output.AddRange(ListFiles(e, r));
                else output.Add(r);
            }
            return output;
        }

        private sealed class ModFile { public string Natives, Rel; } // e.g. "natives/STM", "escape/character/player/pl2000/mesh/pl2000.mesh.1902042334"

        public static ConvertReport Convert(string modRoot, string outRoot, string slug, string displayName, ConvertOptions opts)
        {
            bool dry = opts.DryRun;
            var paks = Converter.Paks(opts.GameDir);
            var report = new ConvertReport();

            // every file of the mod, by virtual path without version (lowercase) -> where it is
            var byVirt = new Dictionary<string, List<ModFile>>(StringComparer.OrdinalIgnoreCase);
            string nativesDir = Path.Combine(modRoot, "natives");
            foreach (var platDir in Directory.Exists(nativesDir) ? Directory.GetDirectories(nativesDir) : new string[0])
            {
                string natives = "natives/" + Path.GetFileName(platDir); // keeps the mod's own spelling (STM / stm)
                foreach (var rel in ListFiles(platDir))
                {
                    string key = StripVer(rel).ToLowerInvariant();
                    if (!byVirt.TryGetValue(key, out var l)) byVirt[key] = l = new List<ModFile>();
                    l.Add(new ModFile { Natives = natives, Rel = rel });
                }
            }
            bool Has(string virt) => virt != null && byVirt.ContainsKey(virt);

            string jsonDir = Os(outRoot ?? ".", "reframework/data/CustomOutfits/outfits");
            if (!dry) Directory.CreateDirectory(jsonDir);

            // where a mod file goes: custom_outfits/<menu character>/<slug>/<path after character/player/ (or full path)>
            // (files from outside the character folders go to x/<code>_<name>: their full paths are long, and Windows
            // paths are limited to 260 characters, also where Fluffy extracts the package)
            string CustomOf(string menu, string virt)
            {
                string v = virt.ToLowerInvariant();
                string rest = v.StartsWith(PlayerRoot + "/") ? v.Substring(PlayerRoot.Length + 1)
                    : "x/" + Hash4(v.Substring(0, Math.Max(0, v.LastIndexOf('/')))) + "_" + v.Substring(v.LastIndexOf('/') + 1);
                return $"custom_outfits/{menu}/{slug}/{rest}";
            }

            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // copies a mod file (all its versions) to dstVirt (without version); a texture also takes its
            // high-resolution copy from streaming/ along
            void CopyTo(string virt, string dstVirt)
            {
                foreach (var (src, dst) in new[] { (virt, dstVirt), ("streaming/" + virt, "streaming/" + dstVirt) })
                {
                    if (!byVirt.TryGetValue(src, out var files)) continue;
                    foreach (var f in files)
                    {
                        string ver = Path.GetFileName(f.Rel).Substring(Path.GetFileName(StripVer(f.Rel)).Length); // ".13", ".1902042334"...
                        string natives = f.Natives.ToLowerInvariant();
                        if (!copied.Add(natives + "|" + dst + ver)) continue;
                        report.Copied++;
                        if (dry) continue;
                        string path = Os(outRoot, natives, dst + ver);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.Copy(Os(modRoot, f.Natives, f.Rel), path, true);
                    }
                }
            }
            void CopyMod(string menu, string virt) => CopyTo(virt, CustomOf(menu, virt));

            foreach (var menu in Models.Select(m => m.Menu).Distinct())
            {
                // textures of the mod (any folder) -> custom path; used by every material of this character
                Func<string, string> mapFn = p => Has(p.ToLowerInvariant()) && Regex.IsMatch(byVirt[p.ToLowerInvariant()][0].Rel, @"\.tex\.\d+$", RegexOptions.IgnoreCase)
                    ? CustomOf(menu, p) : null;

                var byPart = new Dictionary<string, List<Variant>>();
                var variantModel = new Dictionary<Variant, Model>();
                var primary = new HashSet<Variant>(); // the mod ships the mesh or the material of this part
                var pending = new Dictionary<Variant, List<Action>>();
                var mdfOf = new Dictionary<Variant, byte[]>();
                var missingOf = new Dictionary<Variant, List<string>>(); // files the material needs that nobody has
                var done = new Dictionary<string, Variant>(StringComparer.OrdinalIgnoreCase); // model|part|mdf -> variant
                foreach (var m in Models.Where(x => x.Menu == menu))
                {
                    foreach (var c in m.Table)
                    {
                        foreach (var kv in c.Value)
                        {
                            string part = kv.Key;
                            var e = kv.Value;
                            string dkey = m.Id + "|" + part + "|" + e.Mdf;
                            if (done.TryGetValue(dkey, out var already))
                            {
                                if (already != null) already.Direct.Add(c.Key);
                                continue;
                            }
                            done[dkey] = null;

                            // the material: from the mod, else the game's
                            var mdfs = new List<(string Natives, string Ver, byte[] Buf)>();
                            bool modMdf = byVirt.TryGetValue(e.Mdf, out var mf);
                            if (modMdf)
                                foreach (var f in mf) mdfs.Add((f.Natives.ToLowerInvariant(), f.Rel.Substring(f.Rel.LastIndexOf('.') + 1), File.ReadAllBytes(Os(modRoot, f.Natives, f.Rel))));
                            else
                                foreach (var ver in MdfVers)
                                {
                                    var buf = paks.ReadAny($"natives/stm/{e.Mdf}.{ver}");
                                    if (buf != null) mdfs.Add(("natives/stm", ver, buf));
                                }
                            if (mdfs.Count == 0) continue; // the game doesn't have it (DLC not installed) and the mod doesn't ship it

                            int changed = 0;
                            var rewritten = new List<(string Natives, string Ver, byte[] Buf)>();
                            foreach (var x in mdfs)
                            {
                                rewritten.Add((x.Natives, x.Ver, Mdf2.RewriteTexturePaths(x.Buf, mapFn, out int ch)));
                                changed = Math.Max(changed, ch);
                            }
                            bool modMesh = Has(e.Mesh), modSkin = e.Skin != e.Mesh && Has(e.Skin), modChain = Has(e.Chain);
                            if (!modMdf && !modMesh && !modSkin && !modChain && changed == 0) continue; // the mod doesn't touch this part

                            string mdfCustom = CustomOf(menu, e.Mdf);
                            // written only if the variant is kept (see below)
                            var writes = new List<Action>();
                            foreach (var x in rewritten)
                            {
                                var xx = x;
                                writes.Add(() =>
                                {
                                    if (dry) return;
                                    string dst = Os(outRoot, xx.Natives, mdfCustom + "." + xx.Ver);
                                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                    File.WriteAllBytes(dst, xx.Buf);
                                });
                            }
                            if (modMesh) writes.Add(() => CopyMod(menu, e.Mesh));
                            if (modSkin) writes.Add(() => CopyMod(menu, e.Skin));
                            if (modChain) writes.Add(() => CopyMod(menu, e.Chain));

                            var v = new Variant
                            {
                                Key = Path.GetFileName(e.Mdf).Replace(".mdf2", "") + (m.Id == menu ? "" : "@" + m.Id),
                                Replaces = e.Mdf,
                                Mdf = mdfCustom,
                                Mesh = modMesh ? CustomOf(menu, e.Mesh) : e.Mesh,
                                // the outfit is worn over any costume, so it carries the skin mesh and the physics
                                // (chain) of the body it was made for, not the ones of the costume the game has on
                                SkinMesh = modSkin ? CustomOf(menu, e.Skin) : (e.Skin != null && e.Skin != e.Mesh ? e.Skin : null),
                                Chain = modChain ? CustomOf(menu, e.Chain) : e.Chain,
                                Textures = rewritten.SelectMany(x => CustomTexturesOf(x.Buf)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                            };
                            done[dkey] = v;
                            variantModel[v] = m;
                            pending[v] = writes;
                            mdfOf[v] = rewritten[0].Buf;
                            if (modMdf || modMesh) primary.Add(v);
                            v.Direct.Add(c.Key);
                            if (!byPart.TryGetValue(part, out var list)) byPart[part] = list = new List<Variant>();
                            list.Add(v);
                            report.Units.Add(new UnitInfo { Character = menu, Dir = e.Mdf, Part = part, V = v, MdfSource = modMdf ? "mod" : "pak", Rewritten = changed, HasChain = modChain, HasBlend = modSkin });
                        }
                    }
                }

                // One Body (Face, Hair) selector per character: the chosen outfit is worn over any costume of the game,
                // and the variant follows the story stage. So only the parts the mod really replaces (mesh or
                // material) count; parts touched just because they share a replaced texture are dropped, unless the
                // mod only has textures. Every costume of a model then gets the variant of its own costume, else one
                // of the same stage (clean / injured), else the first.
                foreach (var part in byPart.Keys.ToList())
                {
                    var all = byPart[part];
                    bool anyPrimary = all.Any(primary.Contains);
                    var kept = new List<Variant>();
                    foreach (var m in Models.Where(x => x.Menu == menu))
                    {
                        var mine = all.Where(v => variantModel[v] == m && (!anyPrimary || primary.Contains(v))).ToList();
                        if (mine.Count == 0) continue;
                        kept.AddRange(mine);
                        foreach (var c in m.Table.Where(t => t.Value.ContainsKey(part)).Select(t => t.Key))
                        {
                            var v = mine.FirstOrDefault(x => x.Direct.Contains(c))
                                ?? mine.FirstOrDefault(x => x.Direct.Any(d => StageOf(m, d) == StageOf(m, c)))
                                ?? mine[0];
                            Assign(v, m, c);
                        }
                    }
                    byPart[part] = kept;
                    foreach (var v in kept) foreach (var w in pending[v]) w();
                    foreach (var v in kept)
                    {
                        var missing = new List<string>();
                        foreach (var mat in Mdf2.Parse(mdfOf[v]))
                        {
                            // master material: a new one shipped by the mod is copied as is (it replaces nothing);
                            // the material never finishes loading without it
                            if (!string.IsNullOrEmpty(mat.Mmtr))
                            {
                                string mm = mat.Mmtr.ToLowerInvariant();
                                bool inGame = paks.ExistsAny($"natives/stm/{mat.Mmtr}.{MmtrVer}");
                                if (Has(mm) && !inGame) CopyTo(mm, mm);
                                else if (Has(mm)) report.Warnings.Add($"{part}: the mod changes the game's master material {mat.Mmtr}; that change is not carried over.");
                                else if (!inGame) missing.Add(mat.Mmtr);
                            }
                            // textures that neither the mod nor the game has
                            foreach (var t in mat.Textures)
                                if (t.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && !t.Path.StartsWith("custom_outfits/", StringComparison.OrdinalIgnoreCase)
                                    && !paks.ExistsAny($"natives/stm/{t.Path}.{TexVer}") && !paks.ExistsAny($"natives/stm/streaming/{t.Path}.{TexVer}"))
                                    missing.Add(t.Path);
                        }
                        missing = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (missing.Count > 0)
                            missingOf[v] = missing;
                    }
                }
                report.Units.RemoveAll(u => u.Character == menu && !byPart.Values.Any(l => l.Contains(u.V)));

                // copy every texture the converted materials use
                foreach (var list in byPart.Values)
                    foreach (var v in list)
                        foreach (var t in v.Textures)
                        {
                            string orig = byVirt.Keys.FirstOrDefault(k => CustomOf(menu, k).Equals(t, StringComparison.OrdinalIgnoreCase));
                            if (orig != null) CopyMod(menu, orig);
                        }

                foreach (var kv in byPart)
                {
                    string part = kv.Key;
                    var variants = kv.Value;
                    foreach (var v in variants)
                    {
                        var m = variantModel[v];
                        var labels = v.Direct.Select(c => m.Labels.TryGetValue(c, out var l) ? l : "costume " + c).Distinct().ToList();
                        v.Label = labels.Count > 0 ? string.Join(" / ", labels) : "extra";
                        if (missingOf.TryGetValue(v, out var miss))
                            report.Warnings.Add($"{PartName(part)} \"{v.Label}\": needs {miss.Count} file(s) that neither the mod nor the game has "
                                + $"(e.g. {miss[0]}). It may look wrong or not load in game; the original mod probably needs another mod.");
                    }
                    // default: the main model's costume 0, else its first variant
                    int def = variants.FindIndex(v => variantModel[v].Id == menu && v.Costumes.Contains(0));
                    if (def < 0) def = variants.FindIndex(v => variantModel[v].Id == menu);
                    if (def < 0) def = 0;

                    string id = $"{menu}_{slug}_{part}";
                    string name = opts.Names.TryGetValue($"{menu}|{part}", out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm : displayName;
                    var entry = new JObj().Set("id", id).Set("name", name).Set("character", menu).Set("part", part)
                        .Set("version", 2).Set("game", "re3").Set("package", opts.Package).Set("families", new List<string>()).Set("default", def)
                        .Set("variants", variants.Select(v => v.ToJson()).ToList());
                    report.Outfits.Add(new OutfitInfo
                    {
                        Id = id, Character = menu, Part = part, Name = name,
                        Also = new List<string>(),
                        VariantLabels = variants.Select(v => v.Label).ToList(),
                    });
                    if (!dry) File.WriteAllText(Path.Combine(jsonDir, id + ".json"), Json.Stringify(entry), new UTF8Encoding(false));
                }
            }

            // files of character folders that no costume of the game uses (e.g. figure-viewer materials)
            var used = new HashSet<string>(report.Units.SelectMany(u => new[] { u.V.Replaces }), StringComparer.OrdinalIgnoreCase);
            foreach (var k in byVirt.Keys.Where(k => k.StartsWith(PlayerRoot + "/") && k.EndsWith(".mdf2") && !k.EndsWith("_figure.mdf2") && !used.Contains(k)).OrderBy(k => k))
                report.Warnings.Add($"{k.Substring(PlayerRoot.Length + 1)}: not used by any costume of the game, skipped");
            return report;
        }

        private static string PartName(string part) => part == "body" ? "Body" : part == "face" ? "Face" : "Hair";

        private static string Hash4(string s)
        {
            uint h = 2166136261;
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            return (h % 1679616).ToString("x5"); // short code of a folder
        }

        // story stage of a costume: Jill's costume 2 is the injured one, everything else is clean
        private static string StageOf(Model m, int costume) => m.Id == "jill" && costume == 2 ? "injured" : "clean";

        // main model: the variant's own costumes; other models of the same character (infected, dream...): "also"
        private static void Assign(Variant v, Model m, int costume)
        {
            if (m.Id == m.Menu) { if (!v.Costumes.Contains(costume)) v.Costumes.Add(costume); return; }
            v.Also = v.Also ?? new List<KeyValuePair<string, List<int>>>();
            var e = v.Also.FirstOrDefault(a => a.Key == m.Id);
            if (e.Key == null) v.Also.Add(e = new KeyValuePair<string, List<int>>(m.Id, new List<int>()));
            e.Value.Add(costume);
        }

        private static IEnumerable<string> CustomTexturesOf(byte[] mdf) =>
            Mdf2.Parse(mdf).SelectMany(m => m.Textures).Select(t => t.Path).Where(p => p.StartsWith("custom_outfits/", StringComparison.OrdinalIgnoreCase)).Select(p => p.ToLowerInvariant());
    }
}
