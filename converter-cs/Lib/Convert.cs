// Convert.cs - converts a skin mod (folder with natives/) into custom_outfits/ without replacing the originals.
// For each "unit" of the mod (folder plXXXX or variant plXXXX_NN):
// - part by number: +00..49 body, +50..69 face, +70..99 hair (relative to the group)
// - an mdf2 missing from the mod is read from the game's pak
// - textures (including streaming/) go to custom_outfits/<character>/<slug>/...
// Writes one outfit (JSON) per part, with the story variants inside.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CustomOutfits
{
    public sealed class Variant
    {
        public string Key, Replaces, Mdf, Mesh, SkinMesh, Chain, Label;
        public List<string> Textures = new List<string>();
        public List<int> Direct = new List<int>();
        public List<int> Costumes = new List<int>();
        public List<KeyValuePair<string, List<int>>> Also; // other characters that use the same file
        internal int MinStage;

        public JObj ToJson()
        {
            var o = new JObj().Set("key", Key).Set("replaces", Replaces).Set("mdf", Mdf);
            if (Mesh != null) o.Set("mesh", Mesh);
            if (SkinMesh != null) o.Set("skin_mesh", SkinMesh);
            if (Chain != null) o.Set("chain", Chain);
            o.Set("textures", Textures).Set("direct", Direct).Set("label", Label).Set("costumes", Costumes);
            if (Also != null)
            {
                var a = new JObj();
                foreach (var kv in Also) a.Set(kv.Key, kv.Value);
                o.Set("also", a);
            }
            return o;
        }
    }

    public sealed class UnitInfo
    {
        public string Character, Dir, Part, MdfSource;
        public Variant V;
        public int Rewritten;
        public bool HasChain, HasBlend;
    }

    public sealed class OutfitInfo
    {
        public string Id, Character, Part, Name;
        public List<string> Families = new List<string>();
        public List<string> Also = new List<string>();
        public List<string> VariantLabels = new List<string>();
    }

    public sealed class ConvertReport
    {
        public List<UnitInfo> Units = new List<UnitInfo>();
        public List<OutfitInfo> Outfits = new List<OutfitInfo>();
        public int Copied;
        public List<string> Warnings = new List<string>();
    }

    public sealed class ConvertOptions
    {
        public bool DryRun;                                                    // analyze only, write nothing
        public Dictionary<string, string> Names = new Dictionary<string, string>(); // "character|part" -> name
        public string GameDir;
        public string Package; // id of the converted package: face/hair of the same mod follow its body in game
    }

    public static class Converter
    {
        // mdf2 versions: "10" = non-RT build (DX11), "21" = RT build (DX12, Steam default).
        // Each file keeps the version it came with; an mdf2 missing from the mod is written in every version the pak has.
        public static readonly string[] MdfVers = { "10", "21" };
        // platform folders under natives/: "x64" (non-RT build) and "stm" (RT build). Mods may use either, or both.
        public static readonly string[] Platforms = { "x64", "stm" };

        private static readonly Dictionary<string, GamePaks> PaksCache = new Dictionary<string, GamePaks>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SortedDictionary<string, CostumeTable>> AllTablesCache =
            new Dictionary<string, SortedDictionary<string, CostumeTable>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static GamePaks Paks(string gameDir)
        {
            lock (CacheLock)
            {
                if (!PaksCache.TryGetValue(gameDir, out var p)) PaksCache[gameDir] = p = new GamePaks(gameDir);
                return p;
            }
        }

        // costume tables of every character (to find who else uses each file), read once
        private static SortedDictionary<string, CostumeTable> AllTables(GamePaks paks, string gameDir)
        {
            lock (CacheLock)
            {
                if (!AllTablesCache.TryGetValue(gameDir, out var t))
                {
                    t = new SortedDictionary<string, CostumeTable>(StringComparer.Ordinal);
                    foreach (var g in Costumes.AllGroups(paks)) t[g] = Costumes.Table(paks, gameDir, g);
                    AllTablesCache[gameDir] = t;
                }
                return t;
            }
        }

        private static string PartOf(string baseName, int groupNum)
        {
            int off = int.Parse(baseName.Substring(2, 4)) - groupNum;
            if (off >= 0 && off < 50) return "body";
            if (off >= 50 && off < 70) return "face";
            if (off >= 70 && off < 100) return "hair";
            return null;
        }

        /// <summary>Files in a folder, with relative paths using "/".</summary>
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

        private static string StripVer(string n) => Regex.Replace(n, @"\.\d+$", "");
        private static string PosixDir(string p) { int i = p.LastIndexOf('/'); return i < 0 ? "." : p.Substring(0, i); }
        private static string PosixBase(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }
        private static string OsPath(string root, params string[] parts) =>
            Path.Combine(new[] { root }.Concat(parts.Select(x => x.Replace('/', Path.DirectorySeparatorChar))).ToArray());

        // custom textures referenced by an mdf2 (the script loads them before applying the material)
        private static List<string> CustomTextures(byte[] mdf)
        {
            var set = new HashSet<string>();
            foreach (var m in Mdf2.Parse(mdf))
                foreach (var t in m.Textures)
                    if (t.Path.StartsWith("custom_outfits/", StringComparison.OrdinalIgnoreCase)) set.Add(t.Path.ToLowerInvariant());
            var list = set.ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public static ConvertReport Convert(string modRoot, string outRoot, string slug, string displayName, ConvertOptions opts = null)
        {
            opts = opts ?? new ConvertOptions();
            bool dry = opts.DryRun;
            string gameDir = opts.GameDir;
            string PlayerDir(string plat) => OsPath(modRoot, $"natives/{plat}/sectionroot/character/player");
            var plats = Platforms.Where(pl => Directory.Exists(PlayerDir(pl))).ToList();
            if (plats.Count == 0) throw new IOException("mod has no natives/x64 (or natives/stm)/sectionroot/character/player");
            var groups = plats.SelectMany(pl => Directory.GetDirectories(PlayerDir(pl)).Select(Path.GetFileName))
                .Where(g => Regex.IsMatch(g, @"^pl\d{4}$", RegexOptions.IgnoreCase)).Select(g => g.ToLowerInvariant())
                .Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
            var paks = Paks(gameDir);
            var report = new ConvertReport();
            // every .tex in the mod: virtual path (lowercase, no version) -> (natives folder, file path relative to it)
            var extTex = new Dictionary<string, List<(string Natives, string Rel)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pl in Platforms)
                foreach (var natives in new[] { $"natives/{pl}", $"natives/{pl}/streaming" })
                    foreach (var rel in ListFiles(OsPath(modRoot, natives)))
                    {
                        if (!Regex.IsMatch(rel, @"\.tex\.\d+$", RegexOptions.IgnoreCase)) continue;
                        if (!natives.EndsWith("/streaming") && rel.StartsWith("streaming/", StringComparison.OrdinalIgnoreCase)) continue;
                        string key = StripVer(rel).ToLowerInvariant();
                        if (!extTex.TryGetValue(key, out var l)) extTex[key] = l = new List<(string, string)>();
                        l.Add((natives, rel));
                    }
            string jsonDir = OsPath(outRoot ?? ".", "reframework/data/CustomOutfits/outfits");
            if (!dry) Directory.CreateDirectory(jsonDir);

            foreach (var group in groups)
            {
                string character = Costumes.CharOf(group);
                string newVirtBase = $"custom_outfits/{character}/{slug}";
                var byPart = new List<KeyValuePair<string, List<Variant>>>(); // parts in order of appearance
                int groupNum = int.Parse(group.Substring(2));
                string virtGroup = $"sectionroot/character/player/{group}";
                // files of the group in every platform folder: relative path -> platforms that have it
                var srcMain = new Dictionary<string, List<string>>();
                var srcStream = new Dictionary<string, List<string>>();
                void Collect(Dictionary<string, List<string>> into, string plat, string dirPath)
                {
                    foreach (var r in ListFiles(dirPath))
                    {
                        if (!into.TryGetValue(r, out var l)) into[r] = l = new List<string>();
                        l.Add(plat);
                    }
                }
                foreach (var pl in plats)
                {
                    Collect(srcMain, pl, OsPath(modRoot, $"natives/{pl}", virtGroup));
                    Collect(srcStream, pl, OsPath(modRoot, $"natives/{pl}/streaming", virtGroup));
                }
                var groupPlats = plats.Where(pl => srcMain.Values.Any(l => l.Contains(pl)) || srcStream.Values.Any(l => l.Contains(pl))).ToList();
                var main = srcMain.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();
                var streaming = srcStream.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();

                // map: original virtual path (lowercase, no version) -> custom path
                var remap = new Dictionary<string, string>();
                foreach (var rel in main.Concat(streaming).Distinct())
                {
                    if (Regex.IsMatch(rel, @"\.tex\.\d+$", RegexOptions.IgnoreCase))
                        remap[$"{virtGroup}/{StripVer(rel)}".ToLowerInvariant()] = $"{newVirtBase}/{StripVer(rel)}".ToLowerInvariant();
                }
                // textures the mod keeps outside the character folder (e.g. natives/x64/Savina/*.tex):
                // copied to <slug>/_ext/<path> when an mdf2 of this group references them
                var extUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Func<string, string> mapFn = p =>
                {
                    string lp = p.ToLowerInvariant();
                    if (remap.TryGetValue(lp, out var np)) return np;
                    if (extTex.ContainsKey(lp) && !lp.StartsWith(virtGroup + "/"))
                    {
                        extUsed.Add(lp);
                        return $"{newVirtBase}/_ext/{lp}";
                    }
                    return null;
                };

                // copy every file that isn't an mdf2 (mesh, tex, chain...) keeping its subfolder
                // (each file stays in the platform folder it came from: x64 = non-RT, stm = RT)
                void Copy(string natives, string rel)
                {
                    report.Copied++;
                    if (dry) return;
                    string dst = OsPath(outRoot, natives, newVirtBase, rel.ToLowerInvariant());
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(OsPath(modRoot, natives, virtGroup, rel), dst, true);
                }
                foreach (var rel in main)
                    if (!Regex.IsMatch(rel, @"\.mdf2\.\d+$", RegexOptions.IgnoreCase))
                        foreach (var pl in srcMain[rel]) Copy($"natives/{pl}", rel);
                foreach (var rel in streaming)
                    foreach (var pl in srcStream[rel]) Copy($"natives/{pl}/streaming", rel);

                // units: every folder with files (pl1070, pl1050/pl1050_01, ...)
                var dirs = new HashSet<string>(main.Where(r => r.Contains("/")).Select(PosixDir));
                foreach (var s in streaming) if (s.Contains("/")) dirs.Add(PosixDir(s));
                string Has(string rel, string pattern) =>
                    main.FirstOrDefault(r => PosixDir(r) == rel && Regex.IsMatch(PosixBase(r), pattern, RegexOptions.IgnoreCase));

                foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
                {
                    string bas = PosixBase(dir);
                    if (!Regex.IsMatch(bas, @"^pl\d{4}(_\d+)?$", RegexOptions.IgnoreCase)) continue;
                    string part = PartOf(bas, groupNum);
                    if (part == null) { report.Warnings.Add($"{character} {dir}: unknown part, skipped"); continue; }
                    string esc = Regex.Escape(bas);

                    // mdf2: from the mod (every version it ships: non-RT and/or RT) or from the pak
                    var mdfs = new List<(string Plat, string Ver, byte[] Buf)>();
                    foreach (var f in main.Where(r => PosixDir(r) == dir && Regex.IsMatch(PosixBase(r), $"^{esc}\\.mdf2\\.\\d+$", RegexOptions.IgnoreCase)))
                        foreach (var pl in srcMain[f])
                            mdfs.Add((pl, f.Substring(f.LastIndexOf('.') + 1), File.ReadAllBytes(OsPath(modRoot, $"natives/{pl}", virtGroup, f))));
                    bool modMdf = mdfs.Count > 0;
                    string mdfSource = modMdf ? "mod" : "pak";
                    if (!modMdf)
                    {
                        // every version the game has, written to every platform folder the mod uses
                        foreach (var ver in MdfVers)
                        {
                            var buf = paks.ReadAny($"natives/x64/{virtGroup}/{dir}/{bas}.mdf2.{ver}");
                            if (buf != null) foreach (var pl in groupPlats) mdfs.Add((pl, ver, buf));
                        }
                        if (mdfs.Count == 0)
                        {
                            report.Warnings.Add($"{character} {dir}: original material (mdf2) not found in the game files. Another mod installed in Fluffy probably replaces it: uninstall it and try again.");
                            continue;
                        }
                    }
                    int changed = 0;
                    var rewritten = new List<(string Plat, string Ver, byte[] Buf)>();
                    foreach (var m in mdfs)
                    {
                        rewritten.Add((m.Plat, m.Ver, Mdf2.RewriteTexturePaths(m.Buf, mapFn, out int ch)));
                        changed = Math.Max(changed, ch);
                    }

                    // mesh: from the mod in the same folder, or from the parent folder (_NN variants), or the original
                    string parentDir = Regex.IsMatch(bas, @"_\d+$") ? PosixDir(dir) : null;
                    string parentBase = parentDir != null ? PosixBase(parentDir) : null;
                    string modMesh = Has(dir, $"^{esc}\\.mesh\\.\\d+$");
                    string parentMesh = parentDir != null ? Has(parentDir, $"^{Regex.Escape(parentBase)}\\.mesh\\.\\d+$") : null;
                    string mesh = null;
                    if (modMesh != null) mesh = $"{newVirtBase}/{dir}/{bas}.mesh";
                    else if (parentMesh != null) mesh = $"{newVirtBase}/{parentDir}/{parentBase}.mesh";

                    // only create an outfit if the mod changes something in this unit
                    if (!modMdf && modMesh == null && parentMesh == null && changed == 0) continue;

                    if (!dry)
                    {
                        foreach (var m in rewritten)
                        {
                            string mdfOut = OsPath(outRoot, $"natives/{m.Plat}", newVirtBase, dir, $"{bas}.mdf2.{m.Ver}".ToLowerInvariant());
                            Directory.CreateDirectory(Path.GetDirectoryName(mdfOut));
                            File.WriteAllBytes(mdfOut, m.Buf);
                        }
                    }

                    // no custom mesh: path of the original mesh (the game always resolves it, from the pak or a loose file)
                    if (mesh == null)
                    {
                        string origBase = parentBase ?? bas, origDir = parentDir ?? dir;
                        mesh = $"{virtGroup}/{origDir}/{origBase}.mesh";
                    }
                    string chain = main.FirstOrDefault(f => Regex.IsMatch(f, $"^{esc}\\.chain\\.\\d+$", RegexOptions.IgnoreCase));
                    // face: ComputeSkinning uses <base>_blend.mesh (expressions); if the mod ships one, use it
                    string meshDir = modMesh != null ? dir : parentMesh != null ? parentDir : null;
                    string meshBase = modMesh != null ? bas : parentBase;
                    string blend = meshDir != null ? Has(meshDir, $"^{Regex.Escape(meshBase)}_blend\\.mesh\\.\\d+$") : null;

                    var v = new Variant
                    {
                        Key = bas,
                        Replaces = $"{virtGroup}/{dir}/{bas}.mdf2".ToLowerInvariant(), // original mdf2 that this variant replaces
                        Mdf = $"{newVirtBase}/{dir}/{bas}.mdf2".ToLowerInvariant(),
                        Mesh = mesh?.ToLowerInvariant(),
                        SkinMesh = blend != null ? $"{newVirtBase}/{meshDir}/{StripVer(PosixBase(blend))}".ToLowerInvariant() : null,
                        Chain = chain != null ? $"{newVirtBase}/{StripVer(chain)}".ToLowerInvariant() : null,
                        Textures = rewritten.SelectMany(m => CustomTextures(m.Buf)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    };
                    var list = byPart.FirstOrDefault(kv => kv.Key == part).Value;
                    if (list == null) { list = new List<Variant>(); byPart.Add(new KeyValuePair<string, List<Variant>>(part, list)); }
                    list.Add(v);
                    report.Units.Add(new UnitInfo { Character = character, Dir = dir, Part = part, V = v, MdfSource = mdfSource, Rewritten = changed, HasChain = chain != null, HasBlend = blend != null });
                }

                foreach (var key in extUsed.OrderBy(k => k, StringComparer.Ordinal))
                    foreach (var (natives, rel) in extTex[key])
                    {
                        report.Copied++;
                        if (dry) continue;
                        string dst = OsPath(outRoot, natives, newVirtBase, "_ext", rel.ToLowerInvariant());
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(OsPath(modRoot, natives, rel), dst, true);
                    }

                // one outfit per part, with the story variants inside
                var table = Costumes.Table(paks, gameDir, group);
                // prefabs shipped by the mod itself take priority (e.g. PS1 links costume b back to the normal face/hair)
                var modTable = Costumes.ModTable(modRoot, group);
                foreach (var kv in modTable)
                {
                    if (!table.TryGetValue(kv.Key, out var parts)) table[kv.Key] = parts = new Dictionary<string, CostumePart>();
                    foreach (var p in kv.Value) parts[p.Key] = p.Value;
                }
                foreach (var kv in byPart)
                {
                    string part = kv.Key;
                    var variants = kv.Value;
                    int def = AssignCostumes(variants, table, group, part);
                    // other characters whose costumes use the same file (the Forgotten Soldier uses Hunk's body)
                    foreach (var g2 in AllTables(paks, gameDir))
                    {
                        if (g2.Key == group) continue;
                        foreach (var v in variants)
                        {
                            var cs = g2.Value.Where(c => c.Value.TryGetValue(part, out var cp) && cp.Mdf == v.Replaces).Select(c => c.Key).OrderBy(c => c).ToList();
                            if (cs.Count > 0) (v.Also = v.Also ?? new List<KeyValuePair<string, List<int>>>()).Add(new KeyValuePair<string, List<int>>(Costumes.CharOf(g2.Key), cs));
                        }
                    }
                    // families the outfit replaces: "normal", "alt" (story) and/or "extra" (unlockable costume)
                    var families = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var v in variants) foreach (var c in v.Direct) families.Add(Costumes.StageOf(group, c)?.Item1 ?? "extra");

                    string id = $"{character}_{slug}_{part}";
                    string name = opts.Names.TryGetValue($"{character}|{part}", out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm : displayName;
                    var entry = new JObj().Set("id", id).Set("name", name).Set("character", character).Set("part", part)
                        .Set("version", 2).Set("package", opts.Package).Set("families", families.ToList()).Set("default", def)
                        .Set("variants", variants.Select(v => v.ToJson()).ToList());
                    // body: face/hair that the MOD's prefabs set for each costume (specific costume)
                    if (part == "body")
                    {
                        var cp = new JObj();
                        foreach (var m in modTable)
                        {
                            JObj perCostume = null;
                            foreach (var p in new[] { "face", "hair" })
                            {
                                if (!m.Value.TryGetValue(p, out var prefab)) continue;
                                var other = byPart.FirstOrDefault(x => x.Key == p).Value;
                                var conv = other?.FirstOrDefault(x => x.Replaces == prefab.Mdf);
                                var item = conv != null
                                    ? new JObj().Set("mdf", conv.Mdf).Set("mesh", conv.Mesh ?? prefab.Mesh).Set("skin", conv.SkinMesh ?? prefab.Skin).Set("chain", conv.Chain ?? prefab.Chain)
                                    : prefab.ToJson();
                                (perCostume = perCostume ?? new JObj()).Set(p, item);
                            }
                            if (perCostume != null) cp.Set(m.Key.ToString(), perCostume);
                        }
                        if (cp.Count > 0) entry.Set("costume_parts", cp);
                    }
                    report.Outfits.Add(new OutfitInfo
                    {
                        Id = id, Character = character, Part = part, Name = name,
                        Families = families.ToList(),
                        Also = variants.Where(v => v.Also != null).SelectMany(v => v.Also.Select(a => a.Key)).Distinct().ToList(),
                        VariantLabels = variants.Select(v => v.Label).ToList(),
                    });
                    if (!dry) File.WriteAllText(Path.Combine(jsonDir, id + ".json"), Json.Stringify(entry), new UTF8Encoding(false));
                }
            }
            return report;
        }

        // Decides which game costumes each variant is used in:
        // 1) direct: costumes whose prefab uses the mdf2 the variant replaces;
        // 2) by stage: costumes of the other family at the same story stage.
        // Returns the index of the default variant (costume 0, else stage 0, else the first).
        private static int AssignCostumes(List<Variant> variants, CostumeTable table, string group, string part)
        {
            var assign = new SortedDictionary<int, int>();
            foreach (var c in table)
            {
                int vi = c.Value.TryGetValue(part, out var cp) ? variants.FindIndex(v => v.Replaces == cp.Mdf) : -1;
                if (vi >= 0) assign[c.Key] = vi;
            }
            // costumes the variant replaces directly -> label and family of the outfit
            Costumes.MenuNames.TryGetValue(group, out var menuNames);
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                v.Direct = assign.Where(a => a.Value == i).Select(a => a.Key).ToList();
                var stages = v.Direct.Select(c => Costumes.StageOf(group, c)).ToList();
                v.MinStage = Math.Min(stages.Select(s => s?.Item2 ?? 99).DefaultIfEmpty(99).Min(), 99);
                if (part == "body")
                {
                    var pairs = stages.Where(s => s != null).ToList();
                    var names = v.Direct.Select(c => menuNames != null && menuNames.TryGetValue(c, out var n) ? n : null).Where(n => n != null).ToList();
                    v.Label = Costumes.StageLabel(group, pairs)
                        ?? (names.Count > 0 ? string.Join(" / ", names)
                        : v.Direct.Count > 0 ? "costume " + string.Join("/", v.Direct.Select(c => System.Convert.ToString(c, 16))) : "extra");
                }
            }
            // face/hair: only the dirt changes -> named by the order of the stage where they start
            if (part != "body")
            {
                string[] dirtNames = { "clean", "dirty", "dirtier", "very dirty" };
                var ranked = variants.Where(v => v.MinStage < 99).OrderBy(v => v.MinStage).ToList(); // OrderBy is stable
                foreach (var v in variants)
                {
                    int r = ranked.IndexOf(v);
                    v.Label = r < 0 ? "extra" : ranked.Count == 1 ? "all stages" : r < dirtNames.Length ? dirtNames[r] : "stage " + v.MinStage;
                }
            }
            var byStage = new Dictionary<int, int>();
            foreach (var a in assign)
            {
                var st = Costumes.StageOf(group, a.Key);
                if (st != null && !byStage.ContainsKey(st.Item2)) byStage[st.Item2] = a.Value;
            }
            foreach (var c in table.Keys.ToList())
            {
                var st = Costumes.StageOf(group, c);
                if (!assign.ContainsKey(c) && st != null && byStage.TryGetValue(st.Item2, out var vi)) assign[c] = vi;
            }
            for (int i = 0; i < variants.Count; i++) variants[i].Costumes = assign.Where(a => a.Value == i).Select(a => a.Key).ToList();
            if (assign.TryGetValue(0, out var d0)) return d0;
            if (byStage.TryGetValue(0, out var s0)) return s0;
            return 0;
        }
    }
}
