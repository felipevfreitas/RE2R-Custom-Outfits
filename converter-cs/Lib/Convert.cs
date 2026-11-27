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
        public string Custom;                  // name the user typed in the converter (else the game menu shows "<outfit> - <label>")
        internal List<string> DlcSwapped = new List<string>();   // textures nowhere in this game (a DLC that isn't installed): swapped for neutral ones
        public string Thumb; public int ThumbW, ThumbH; public bool ThumbFull; // own picture of this outfit's game-menu entry
        public List<string> Textures = new List<string>();
        public List<int> Direct = new List<int>();
        public List<int> Costumes = new List<int>();
        public List<KeyValuePair<string, List<int>>> Also; // other characters that use the same file
        internal int MinStage;
        internal string SrcMesh; // mesh at its original path (the mod's file there, else the game's): to draw the outfit

        public JObj ToJson()
        {
            var o = new JObj().Set("key", Key).Set("replaces", Replaces).Set("mdf", Mdf);
            if (Mesh != null) o.Set("mesh", Mesh);
            if (SkinMesh != null) o.Set("skin_mesh", SkinMesh);
            if (Chain != null) o.Set("chain", Chain);
            o.Set("textures", Textures).Set("direct", Direct).Set("label", Label).Set("costumes", Costumes);
            if (!string.IsNullOrWhiteSpace(Custom)) o.Set("custom", Custom);
            if (Thumb != null)
            {
                o.Set("thumbnail", Thumb).Set("thumbnail_size", new List<int> { ThumbW, ThumbH });
                if (ThumbFull) o.Set("thumbnail_full", true);
            }
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
        internal string Group;
        internal List<Variant> Variants;
        internal int Default;
    }

    public sealed class ConvertReport
    {
        public List<UnitInfo> Units = new List<UnitInfo>();
        public List<OutfitInfo> Outfits = new List<OutfitInfo>();
        public int Copied;
        public List<string> Warnings = new List<string>();
        // outfits whose DLC-only textures were swapped for neutral ones (part, outfit label, texture names): reported once for the
        // whole mod (Core.DlcSwapWarning), not once per outfit
        public List<(string Part, string Label, List<string> Textures)> DlcSwaps = new List<(string, string, List<string>)>();
    }

    public sealed class ConvertOptions
    {
        public bool DryRun;                                                    // analyze only, write nothing
        public Dictionary<string, string> Names = new Dictionary<string, string>(); // "character|part" -> name
        public string GameDir;
        public Dictionary<string, string> VariantPictures = new Dictionary<string, string>(); // "character|part|variant key" -> image of its entry
        public HashSet<string> VariantPicturesDrawn = new HashSet<string>(); // keys of VariantPictures that are drawn poses (transparent, shown whole)
        public HashSet<string> Disabled = new HashSet<string>(); // "character|part" and "character|part|variant key" switched off: left out
        public Dictionary<string, string> VariantDrawn = new Dictionary<string, string>(); // automatic picture drawn from the outfit, used when the mod has none for that costume
        public string PackageName; // name of the group when a package has several outfits of the same part
        public Dictionary<string, string> GroupNames = new Dictionary<string, string>(); // "character|part" -> name of that group in the Wardrobe menu (wins over PackageName)
        public HashSet<string> EachEntry = new HashSet<string>(); // "character|body": an entry per outfit in the game's costume menu
        public Dictionary<string, string> VariantNames = new Dictionary<string, string>(); // "character|part|variant key" -> name
        public string BaseRoot, BaseSlug; // add-on (modinfo addonfor=): the option it is for; files the add-on lacks are that option's
        public string Package; // id of the converted package: face/hair of the same mod follow its body in game
        public string Thumbnail; // image of the mod (modinfo screenshot): body thumbnail in the game's costume menu
        public bool ThumbnailForced; // Thumbnail was chosen by the user: used even if the mod has a costume-menu picture
        public Dictionary<string, string> Drawn; // character -> picture drawn from the outfit (PNG with transparency), when the mod has no image
    }

    public static class Converter
    {
        // mdf2 versions: "10" = non-RT build (DX11), "21" = RT build (DX12, Steam default).
        // Each file keeps the version it came with; an mdf2 missing from the mod is written in every version the pak has.
        public static readonly string[] MdfVers = { "10", "21" };
        private static readonly string[] MeshVers = { "1808312334", "2109108288" }; // non-RT, RT
        private static readonly string[] TexVers = { "10", "34" };                  // non-RT, RT

        // the engine's neutral texture of the same kind (all in the game itself): normal/roughness, occlusion, a mask (black = no
        // effect), else white
        private static string NeutralTexture(string lp)
        {
            string name = Path.GetFileName(lp);
            if (Regex.IsMatch(name, @"_nrm", RegexOptions.IgnoreCase)) return "systems/rendering/NullNormalRoughness.tex";
            if (Regex.IsMatch(name, @"_atos", RegexOptions.IgnoreCase)) return "systems/rendering/NullATOS.tex";
            if (Regex.IsMatch(name, @"_msk|mask", RegexOptions.IgnoreCase)) return "systems/rendering/NullBlack.tex";
            return "systems/rendering/NullWhite.tex";
        }
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
            if (off == 99) return "face"; // pl1099: the default face material (face_default / costume b prefabs)
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

        /// <summary>A part (key null) or one of its versions switched off in the window. Versions of other models of the same look
        /// (RE3 "key@jill_infected") follow it.</summary>
        public static bool IsDisabled(ConvertOptions opts, string character, string part, string key)
        {
            if (opts?.Disabled == null || opts.Disabled.Count == 0) return false;
            if (key == null) return opts.Disabled.Contains(character + "|" + part);
            int at = key.IndexOf('@');
            return opts.Disabled.Contains(character + "|" + part + "|" + key) || at > 0 && opts.Disabled.Contains(character + "|" + part + "|" + key.Substring(0, at));
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
                // the option this one is an add-on for: its files (relative to the group folder), and where they end up in the package
                var baseMain = new Dictionary<string, List<string>>();
                string baseVirt = opts.BaseRoot != null && opts.BaseSlug != null ? $"custom_outfits/{character}/{opts.BaseSlug}" : null;
                if (baseVirt != null)
                    foreach (var pl in Platforms)
                        foreach (var r in ListFiles(OsPath(opts.BaseRoot, $"natives/{pl}", virtGroup)))
                        {
                            if (!baseMain.TryGetValue(r, out var bl)) baseMain[r] = bl = new List<string>();
                            bl.Add(pl);
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
                // textures the add-on needs from the option it is for (it does not bring them): the custom path of that option
                foreach (var rel in baseMain.Keys)
                    if (Regex.IsMatch(rel, @"\.tex\.\d+$", RegexOptions.IgnoreCase))
                    {
                        string vk = $"{virtGroup}/{StripVer(rel)}".ToLowerInvariant();
                        if (!remap.ContainsKey(vk)) remap[vk] = $"{baseVirt}/{StripVer(rel)}".ToLowerInvariant();
                    }
                // textures the mod keeps outside the character folder (e.g. natives/x64/Savina/*.tex):
                // copied to <slug>/_ext/<path> when an mdf2 of this group references them
                var extUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // textures a material takes from the game that are nowhere in this game (a DLC costume's that isn't installed, like
                // the Military wet mask in "TFD Nell TFO", 2026-11-22): the game never loads them and the whole outfit stays
                // untextured. They are swapped for the engine's neutral texture of the same kind, so the outfit works and only
                // that detail is lost. A player who has the DLC installed (its pak in the game folder) keeps the original ones.
                // (A runtime fallback was tried first: the outfit showed untextured ~15 s before switching.) DLC files are never
                // copied into a package.
                var absentTex = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var dlcSwapped = new List<string>();
                bool AbsentFromGame(string lp)
                {
                    if (absentTex.TryGetValue(lp, out var r)) return r;
                    return absentTex[lp] = !TexVers.Any(ver => paks.ExistsAny($"natives/x64/{lp}.{ver}") || paks.ExistsAny($"natives/x64/streaming/{lp}.{ver}"));
                }
                Func<string, string> mapFn = p =>
                {
                    string lp = p.ToLowerInvariant();
                    if (remap.TryGetValue(lp, out var np)) return np;
                    if (extTex.ContainsKey(lp) && !lp.StartsWith(virtGroup + "/"))
                    {
                        extUsed.Add(lp);
                        return $"{newVirtBase}/_ext/{lp}";
                    }
                    if (lp.EndsWith(".tex") && !lp.StartsWith("custom_outfits/") && !lp.StartsWith("systems/") && AbsentFromGame(lp))
                    {
                        dlcSwapped.Add(p);
                        return NeutralTexture(lp);
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

                // Units: normally the files of a folder are named after it (pl1070/pl1070.mdf2). Some mods put a part under another
                // folder (pl1079/pl1070.mdf2: the hair of costume c): the unit is then named after its files.
                var units = new List<(string Dir, string Bas)>();
                foreach (var d in dirs.OrderBy(d => d, StringComparer.Ordinal))
                {
                    string folder = PosixBase(d);
                    if (!Regex.IsMatch(folder, @"^pl\d{4}(_\d+)?$", RegexOptions.IgnoreCase)) continue;
                    var own = main.Where(r => PosixDir(r) == d)
                        .Select(r => Regex.Match(PosixBase(r), @"^(pl\d{4}(?:_\d+)?)\.(?:mdf2|mesh)\.\d+$", RegexOptions.IgnoreCase))
                        .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    // (a folder can hold more than one: pl1099/pl1099.mdf2 is the default face, pl1099/pl1000.mdf2 the body of costume c)
                    if (own.Count == 0) units.Add((d, folder));
                    else foreach (var n in own) if (PartOf(n, groupNum) != null) units.Add((d, n));
                }
                foreach (var (dir, bas) in units)
                {
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
                    // an add-on that brings only textures (the colours of "TFD Nell TFO"): the material of the option it is for, which
                    // is what the original mod uses once both are installed (its textures are mapped to the add-on's own below)
                    if (!modMdf && baseVirt != null)
                    {
                        foreach (var f in baseMain.Keys.Where(r => PosixDir(r) == dir && Regex.IsMatch(PosixBase(r), $"^{esc}\\.mdf2\\.\\d+$", RegexOptions.IgnoreCase)))
                            foreach (var pl in baseMain[f])
                                mdfs.Add((pl, f.Substring(f.LastIndexOf('.') + 1), File.ReadAllBytes(OsPath(opts.BaseRoot, $"natives/{pl}", virtGroup, f))));
                        if (mdfs.Count > 0) mdfSource = "base";
                    }
                    if (mdfs.Count == 0)
                    {
                        // every version the game has, written to every platform folder the mod uses
                        foreach (var ver in MdfVers)
                        {
                            var buf = paks.ReadAny($"natives/x64/{virtGroup}/{dir}/{bas}.mdf2.{ver}");
                            if (buf != null) foreach (var pl in groupPlats) mdfs.Add((pl, ver, buf));
                        }
                        if (mdfs.Count == 0)
                        {
                            report.Warnings.Add($"{character} {dir}: the mod doesn't bring this material (mdf2) and your game doesn't have it: it belongs to a DLC costume you don't own (that part can't work without it), or another mod installed in Fluffy replaces it (uninstall it and try again).");
                            continue;
                        }
                    }
                    int changed = 0;
                    var rewritten = new List<(string Plat, string Ver, byte[] Buf)>();
                    dlcSwapped.Clear();
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
                    // add-on: the mesh of the option it is for (the add-on only brings the material)
                    string baseMesh = baseVirt != null && modMesh == null && parentMesh == null
                        ? baseMain.Keys.FirstOrDefault(r => PosixDir(r) == dir && Regex.IsMatch(PosixBase(r), $"^{esc}\\.mesh\\.\\d+$", RegexOptions.IgnoreCase)) : null;
                    if (modMesh != null) mesh = $"{newVirtBase}/{dir}/{bas}.mesh";
                    else if (parentMesh != null) mesh = $"{newVirtBase}/{parentDir}/{parentBase}.mesh";
                    else if (baseMesh != null) mesh = $"{baseVirt}/{dir}/{bas}.mesh";

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
                    // (a unit named differently from its folder, pl1099/pl1000, takes the folder's chain: pl1099.chain; a face has no chain)
                    string folderChain = main.FirstOrDefault(f => Regex.IsMatch(f, $"^{Regex.Escape(PosixBase(dir))}\\.chain\\.\\d+$", RegexOptions.IgnoreCase));
                    string ownChain = main.FirstOrDefault(f => Regex.IsMatch(f, $"^{esc}\\.chain\\.\\d+$", RegexOptions.IgnoreCase));
                    string chain = part == "face" ? null : PosixBase(dir) != bas ? (folderChain ?? ownChain) : (ownChain ?? folderChain);
                    bool chainFromBase = false;
                    if (chain == null && baseVirt != null)
                    {
                        chain = baseMain.Keys.FirstOrDefault(f => Regex.IsMatch(f, $"^{esc}\\.chain\\.\\d+$", RegexOptions.IgnoreCase))
                            ?? baseMain.Keys.FirstOrDefault(f => Regex.IsMatch(f, $"^{Regex.Escape(PosixBase(dir))}\\.chain\\.\\d+$", RegexOptions.IgnoreCase));
                        chainFromBase = chain != null;
                    }
                    // face: ComputeSkinning uses <base>_blend.mesh (expressions); if the mod ships one, use it
                    string srcMesh = modMesh != null ? $"{virtGroup}/{dir}/{bas}.mesh" : parentMesh != null ? $"{virtGroup}/{parentDir}/{parentBase}.mesh" : baseMesh != null ? $"{virtGroup}/{dir}/{bas}.mesh" : mesh;
                    string meshDir = modMesh != null ? dir : parentMesh != null ? parentDir : null;
                    string meshBase = modMesh != null ? bas : parentBase;
                    string blend = meshDir != null ? Has(meshDir, $"^{Regex.Escape(meshBase)}_blend\\.mesh\\.\\d+$") : null;
                    bool blendFromBase = false;
                    if (blend == null && baseMesh != null)
                    {
                        blend = baseMain.Keys.FirstOrDefault(r => PosixDir(r) == dir && Regex.IsMatch(PosixBase(r), $"^{esc}_blend\\.mesh\\.\\d+$", RegexOptions.IgnoreCase));
                        blendFromBase = blend != null;
                        meshDir = dir;
                    }

                    var v = new Variant
                    {
                        Key = PosixBase(dir) == bas ? bas : PosixBase(dir) + "_" + bas, // (two units can share a name in different folders: pl1000/pl1000 and pl1099/pl1000)
                        Replaces = $"{virtGroup}/{dir}/{bas}.mdf2".ToLowerInvariant(), // original mdf2 that this variant replaces
                        Mdf = $"{newVirtBase}/{dir}/{bas}.mdf2".ToLowerInvariant(),
                        Mesh = mesh?.ToLowerInvariant(),
                        SrcMesh = srcMesh.ToLowerInvariant(),
                        SkinMesh = blend != null ? $"{(blendFromBase ? baseVirt : newVirtBase)}/{meshDir}/{StripVer(PosixBase(blend))}".ToLowerInvariant() : null,
                        Chain = chain != null ? $"{(chainFromBase ? baseVirt : newVirtBase)}/{StripVer(chain)}".ToLowerInvariant() : null,
                        Textures = rewritten.SelectMany(m => CustomTextures(m.Buf)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        DlcSwapped = dlcSwapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
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
                // files of the versions switched off in the window (before they are taken out below)
                var offReplaces = new HashSet<string>(byPart.SelectMany(kv => kv.Value.Where(v => IsDisabled(opts, character, kv.Key, v.Key)).Select(v => v.Replaces))
                    .Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in byPart)
                {
                    string part = kv.Key;
                    var variants = kv.Value;
                    // switched off in the window: the part, or some of its versions (their costumes go to the others, else the game)
                    if (IsDisabled(opts, character, part, null)) continue;
                    var off = variants.Where(v => IsDisabled(opts, character, part, v.Key)).ToList();
                    if (off.Count > 0)
                    {
                        variants.RemoveAll(off.Contains);
                        if (variants.Count == 0) continue;
                    }
                    int def = AssignCostumes(variants, table, group, part);
                    // names the user gave to the outfits of a pack (converter window)
                    foreach (var vv in variants)
                        if (opts.VariantNames != null && opts.VariantNames.TryGetValue($"{character}|{part}|{vv.Key}", out var custom) && !string.IsNullOrWhiteSpace(custom))
                            vv.Custom = custom.Trim();
                    // files the outfit takes from the game that the game doesn't have: the mesh or textures of a DLC costume the
                    // player doesn't own (the mod only brought what it changed). Without them that part is invisible or untextured
                    // in game, so say so now (a player without the DLC saw "only the hair works", 2026-11-22)
                    foreach (var vv in variants)
                    {
                        // (installed DLC paks count: a player with the DLC has its mesh)
                        var missing = new List<string>();
                        if (vv.Mesh != null && !vv.Mesh.StartsWith("custom_outfits/") && !MeshVers.Any(ver => paks.ExistsAny($"natives/x64/{vv.Mesh}.{ver}")))
                            missing.Add(vv.Mesh);
                        // (missing textures are swapped for neutral ones above: only a missing mesh is left to say)
                        if (missing.Count > 0)
                            report.Warnings.Add($"{part} \"{vv.Custom ?? vv.Label}\": needs {missing.Count} file(s) that neither the mod nor your game has "
                                + $"(e.g. {missing[0]}). They come from a DLC costume that isn't installed or from another mod: without it, this part will be invisible or look wrong.");
                        if (vv.DlcSwapped.Count > 0)
                            report.DlcSwaps.Add((part, vv.Custom ?? vv.Label, vv.DlcSwapped.Select(Path.GetFileName).ToList()));
                    }
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
                    bool each = part == "body" && opts.EachEntry.Contains(character + "|body");
                    // a picture for each entry: the image the user chose, else the mod's own menu picture of that costume
                    if (each && !dry)
                        foreach (var vv in variants.Where(x => x.Direct.Count > 0))
                        {
                            string safe = Regex.Replace(vv.Key ?? "v", "[^a-z0-9_]", "_", RegexOptions.IgnoreCase).ToLowerInvariant();
                            int tw = 0, th = 0;
                            string vkey = $"{character}|{part}|{vv.Key}";
                            if (opts.VariantPictures.TryGetValue(vkey, out var img) && File.Exists(img))
                            {
                                // a drawn pose keeps its transparency and is shown whole, like the drawn picture of the mod
                                bool drawnPose = opts.VariantPicturesDrawn.Contains(vkey), any = false;
                                foreach (var pl in plats)
                                {
                                    int ver = pl == "stm" ? 34 : 10;
                                    if (Tex.WriteThumbnail(img, OsPath(outRoot, $"natives/{pl}/{newVirtBase}/thumb_{safe}.tex.{ver}"), ver, out tw, out th, 1024, drawnPose)) any = true;
                                }
                                if (any) { vv.Thumb = newVirtBase + "/thumb_" + safe + ".tex"; vv.ThumbW = tw; vv.ThumbH = th; vv.ThumbFull = drawnPose; }
                            }
                            else
                            {
                                var own = MenuPictureOf(modRoot, character, vv.Direct);
                                if (own != null)
                                {
                                    foreach (var (plat, file) in own)
                                    {
                                        string ver = Regex.Match(file, @"\.tex\.(\d+)$").Groups[1].Value;
                                        string f = OsPath(outRoot, $"natives/{plat}/{newVirtBase}/thumb_{safe}.tex.{ver}");
                                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                                        File.Copy(file, f, true);
                                        var hd = new byte[12];
                                        using (var fs = File.OpenRead(file)) fs.Read(hd, 0, 12);
                                        tw = BitConverter.ToUInt16(hd, 8); th = BitConverter.ToUInt16(hd, 10);
                                    }
                                    vv.Thumb = newVirtBase + "/thumb_" + safe + ".tex"; vv.ThumbW = tw; vv.ThumbH = th; vv.ThumbFull = true;
                                }
                                // no picture of this costume in the mod: the one drawn from this outfit
                                else if (opts.VariantDrawn.TryGetValue(vkey, out var auto) && File.Exists(auto))
                                {
                                    bool any = false;
                                    foreach (var pl in plats)
                                    {
                                        int ver = pl == "stm" ? 34 : 10;
                                        if (Tex.WriteThumbnail(auto, OsPath(outRoot, $"natives/{pl}/{newVirtBase}/thumb_{safe}.tex.{ver}"), ver, out tw, out th, 1024, true)) any = true;
                                    }
                                    if (any) { vv.Thumb = newVirtBase + "/thumb_" + safe + ".tex"; vv.ThumbW = tw; vv.ThumbH = th; vv.ThumbFull = true; }
                                }
                            }
                        }
                    var entry = new JObj().Set("id", id).Set("name", name).Set("character", character).Set("part", part)
                        .Set("version", 2).Set("package", opts.Package).Set("families", families.ToList()).Set("default", def)
                        .Set("variants", variants.Select(v => v.ToJson()).ToList());
                    if (each) entry.Set("menu_entries", "each"); // the game's costume menu gets one entry per outfit of this body
                    // an add-on (modinfo addonfor=, the colours of a skin): an option of the outfit it is for, never an entry of its own
                    // in the game's costume menu (the script groups it with that outfit by package)
                    if (opts.BaseRoot != null) entry.Set("addon", true);
                    string groupName = opts.GroupNames != null && opts.GroupNames.TryGetValue(character + "|" + part, out var gn) && !string.IsNullOrWhiteSpace(gn)
                        ? gn.Trim() : opts.PackageName;
                    if (!string.IsNullOrWhiteSpace(groupName)) entry.Set("package_name", groupName);
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
                                // (a version switched off in the window: the mod's prefab no longer gives it)
                                if (IsDisabled(opts, character, p, null) || offReplaces.Contains(prefab.Mdf)) continue;
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
                        // thumbnail for the game's costume menu: the mod's image as .tex, in each build the mod is for
                        // (x64 = DX11 .tex.10, stm = RT .tex.34); without it the script shows a placeholder
                        // 1st choice: the costume-menu picture the mod itself brings (it replaces the game's:
                        // sectionroot/ui/ui0600/tex/ui0601_<character>_<costume>_iam.tex; shown whole, like the game)
                        var menuPic = dry || opts.ThumbnailForced ? null : MenuPicture(modRoot, character, families, variants);
                        if (menuPic != null)
                        {
                            int tw = 0, th = 0;
                            foreach (var (plat, file) in menuPic)
                            {
                                string ver = Regex.Match(file, @"\.tex\.(\d+)$").Groups[1].Value;
                                string f = OsPath(outRoot, $"natives/{plat}/{newVirtBase}/thumbnail.tex.{ver}");
                                Directory.CreateDirectory(Path.GetDirectoryName(f));
                                File.Copy(file, f, true);
                                var hd = new byte[12];
                                using (var fs = File.OpenRead(file)) fs.Read(hd, 0, 12);
                                tw = BitConverter.ToUInt16(hd, 8); th = BitConverter.ToUInt16(hd, 10);
                            }
                            entry.Set("thumbnail", newVirtBase + "/thumbnail.tex").Set("thumbnail_size", new List<int> { tw, th })
                                .Set("thumbnail_full", true);
                        }
                        else if (!dry && opts.Thumbnail != null)
                        {
                            int tw = 0, th = 0;
                            bool any = false;
                            foreach (var pl in plats)
                            {
                                int ver = pl == "stm" ? 34 : 10;
                                string f = OsPath(outRoot, $"natives/{pl}/{newVirtBase}/thumbnail.tex.{ver}");
                                if (Tex.WriteThumbnail(opts.Thumbnail, f, ver, out tw, out th)) any = true;
                            }
                            if (any) entry.Set("thumbnail", newVirtBase + "/thumbnail.tex").Set("thumbnail_size", new List<int> { tw, th });
                            else report.Warnings.Add("the mod image could not be read: the game menu shows a placeholder");
                        }
                        // no image in the mod: the picture drawn from the outfit (transparent background, shown whole)
                        else if (!dry && opts.Drawn != null && opts.Drawn.TryGetValue(character, out var drawn) && File.Exists(drawn))
                        {
                            int tw = 0, th = 0;
                            bool any = false;
                            foreach (var pl in plats)
                            {
                                int ver = pl == "stm" ? 34 : 10;
                                string f = OsPath(outRoot, $"natives/{pl}/{newVirtBase}/thumbnail.tex.{ver}");
                                if (Tex.WriteThumbnail(drawn, f, ver, out tw, out th, 1024, true)) any = true;
                            }
                            if (any) entry.Set("thumbnail", newVirtBase + "/thumbnail.tex").Set("thumbnail_size", new List<int> { tw, th }).Set("thumbnail_full", true);
                        }
                    }
                    report.Outfits.Add(new OutfitInfo
                    {
                        Id = id, Character = character, Part = part, Name = name,
                        Families = families.ToList(),
                        Also = variants.Where(v => v.Also != null).SelectMany(v => v.Also.Select(a => a.Key)).Distinct().ToList(),
                        VariantLabels = variants.Select(v => v.Label).ToList(),
                        Group = group, Variants = variants, Default = def,
                    });
                    if (!dry) File.WriteAllText(Path.Combine(jsonDir, id + ".json"), Json.Stringify(entry), new UTF8Encoding(false));
                }
            }
            return report;
        }

        // Character code in the costume-menu picture names (ui0601_<code>_<costume>): only Leon and Claire have them.
        private static readonly Dictionary<string, string> MenuPictureChar = new Dictionary<string, string> { ["leon"] = "00", ["claire"] = "01" };

        // The number in ui0601_<code>_<NN> is the entry's place in the game's costume menu, not the costume: 00 and 01 are the two
        // story entries (normal, alternative), then the costumes the menu lists, in its order (normal, alternative, extras).
        // Claire (checked with two mods, 2026-11-21): 02 Jacket (0), 03 Tank top (3), 04 Classic Jacket (5), 05 Classic Tank top (8),
        // 06 Noir (a), 07 Military (b), 08 Elza Walker (c). Leon: same rule over his menu list (not checked with a mod).
        private const int StoryNormal = 16, StoryAlt = 17; // the game's own numbers of the two story entries
        private static readonly Dictionary<string, int[]> MenuOrder = new Dictionary<string, int[]>
        {
            // the DLC costumes come in the order of the game's menu, not by number: Claire 06 = Military (b), 07 = Noir (a)
            // (testarossa's pictures, 2026-11-26: 06 dated like its Military files, 07 like its Noir files; TFD Nell, a
            // Military skin, names its picture 06). Leon by the same rule, b (Arklay Sheriff) before a (Noir): not verified.
            ["claire"] = new[] { 0, 3, 5, 8, 11, 10, 12, 13 },
            ["leon"] = new[] { 0, 1, 3, 5, 8, 11, 10, 13 },
        };

        // picture number of a game costume (or a story entry), or -1 when the menu has no entry for it (the dirty stages)
        private static int MenuPictureIndex(string character, int costume)
        {
            if (costume == StoryNormal) return 0;
            if (costume == StoryAlt) return 1;
            if (!MenuOrder.TryGetValue(character, out var order)) return -1;
            int i = Array.IndexOf(order, costume);
            return i < 0 ? -1 : i + 2;
        }

        /// <summary>The costume-menu picture the mod brings for the costume its game-menu entry stands for:
        /// normal outfit = the story entry's picture (else its costumes), alternative = the same, extra = the costume it replaces.
        /// One file per platform folder (x64/stm), or null.</summary>
        private static List<(string Plat, string File)> MenuPicture(string modRoot, string character, ICollection<string> families, List<Variant> variants)
        {
            var order = new List<int>();
            if (families.Contains("normal")) order.AddRange(new[] { StoryNormal, 0, 1, 2, 3, 4 });
            else if (families.Contains("alt")) order.AddRange(new[] { StoryAlt, 5, 6, 7, 8, 9 });
            else order.AddRange(variants.SelectMany(v => v.Direct).Distinct().OrderBy(c => c));
            // a mod with a single costume-menu picture under another number (TFD Nell TFO replaces Military, 07, but names its
            // picture 06): that picture is still the outfit's, rather than the mod's screenshot cropped
            return MenuPictureOf(modRoot, character, order, true);
        }

        /// <summary>The costume-menu picture the mod brings for one of these game costumes (a .tex; the non-RT one first), or null.</summary>
        public static string MenuPictureFile(string modRoot, string character, IEnumerable<int> costumes)
        {
            if (modRoot == null || character == null) return null;
            return MenuPictureOf(modRoot, character, costumes)?.Select(x => x.File).OrderBy(f => f.EndsWith(".10") ? 0 : 1).FirstOrDefault();
        }

        /// <summary>The mod's costume-menu picture for the first of these game costumes that has one.</summary>
        private static List<(string Plat, string File)> MenuPictureOf(string modRoot, string character, IEnumerable<int> order, bool loneFallback = false)
        {
            if (!MenuPictureChar.TryGetValue(character, out var code)) return null;
            var found = new Dictionary<int, List<(string, string)>>();
            foreach (var pl in Platforms)
            {
                string dir = OsPath(modRoot, $"natives/{pl}/sectionroot/ui/ui0600/tex");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                {
                    var m = Regex.Match(Path.GetFileName(f), $@"^ui0601_{code}_(\d\d)_iam\.tex\.\d+$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    int c = int.Parse(m.Groups[1].Value);
                    if (!found.TryGetValue(c, out var l)) found[c] = l = new List<(string, string)>();
                    l.Add((pl, f));
                }
            }
            foreach (int c in order)
            {
                int n = MenuPictureIndex(character, c);
                if (n >= 0 && found.TryGetValue(n, out var files)) return files;
            }
            return loneFallback && found.Count == 1 ? found.Values.First() : null;
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
                        : v.Direct.Count > 0 ? "costume " + string.Join("/", v.Direct.Select(c => System.Convert.ToString(c, 16))) : null);
                    if (v.Label != null) v.Label = Regex.Replace(v.Label, @"costume ([0-9a-f])(?![0-9a-z])", m => "Costume " + m.Groups[1].Value.ToUpperInvariant());
                }
            }
            // a variant no game costume points to (pl1001_01: the same body with other material) is named after the one it belongs to
            if (part == "body")
                foreach (var v in variants.Where(x => x.Label == null))
                {
                    var pm = Regex.Match(v.Key ?? "", @"^(pl\d{4})_\d+$", RegexOptions.IgnoreCase);
                    var parent = pm.Success ? variants.FirstOrDefault(x => string.Equals(x.Key, pm.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) : null;
                    v.Label = parent?.Label != null ? parent.Label + " (other material)" : "Extra material";
                }
            // face/hair: only the dirt changes -> named by the order of the stage where they start
            if (part != "body")
            {
                string[] dirtNames = { "clean", "dirty", "dirtier", "very dirty" };
                var ranked = variants.Where(v => v.MinStage < 99).OrderBy(v => v.MinStage).ToList(); // OrderBy is stable
                foreach (var v in variants)
                {
                    int r = ranked.IndexOf(v);
                    v.Label = r < 0 ? (v.Direct.Count > 0 ? string.Join(" / ", v.Direct.Select(c => menuNames != null && menuNames.TryGetValue(c, out var nn) ? nn : "Costume " + System.Convert.ToString(c, 16).ToUpperInvariant())) : "Other") : ranked.Count == 1 ? "all stages" : r < dirtNames.Length ? dirtNames[r] : "stage " + v.MinStage;
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
