// Costumes.cs - "game costume -> files of each part" table, read from the game's prefabs.
// objectroot/prefab/character/survivor/parts/<group>/<group>_<part>_default.pfb.16/.17 (costume 0; 17 = RT build)
// objectroot/prefab/character/survivor/parts/<group>/<group>_<part>_costume_<hex>.pfb.16/.17 (1..f)
// Families: costumes that are the SAME outfit at different story stages (Claire: "normal" 0-4,
// "alt" 5-9; stage = position in the family). Outside the families: unlockable costumes (a single stage).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CustomOutfits
{
    public sealed class CostumePart
    {
        public string Mdf, Mesh, Skin, Chain;
        public JObj ToJson() => new JObj().Set("mdf", Mdf).Set("mesh", Mesh).Set("skin", Skin).Set("chain", Chain);
    }

    /// <summary>costume -> part ("body"/"face"/"hair") -> files</summary>
    public sealed class CostumeTable : SortedDictionary<int, Dictionary<string, CostumePart>> { }

    public sealed class Stage
    {
        public string Name;   // name in the game's costume menu (null = with/without jacket)
        public bool Jacket;
        public int Dirt;
        public Stage(string name, bool jacket, int dirt) { Name = name; Jacket = jacket; Dirt = dirt; }
    }

    public static class Costumes
    {
        public static readonly string[] Parts = { "body", "face", "hair" };

        // families per group, in order (normal, alt)
        public static readonly Dictionary<string, List<KeyValuePair<string, int[]>>> Families =
            new Dictionary<string, List<KeyValuePair<string, int[]>>>
            {
                ["pl1000"] = Fam(("normal", new[] { 0, 1, 2, 3, 4 }), ("alt", new[] { 5, 6, 7, 8, 9 })), // Claire
                ["pl0000"] = Fam(("normal", new[] { 0, 1, 2, 3, 4 }), ("alt", new[] { 5, 6, 7, 8, 9 })), // Leon
                // Ada: costumes 5-7 reuse the normal files (no alternative look) -> a single family
                ["pl2000"] = Fam(("normal", new[] { 0, 1, 2, 5, 6, 7 })),
                ["pl3000"] = Fam(("normal", new[] { 0, 1, 2, 3 }), ("alt", new[] { 5, 6, 7, 8 })),       // Sherry
            };

        private static List<KeyValuePair<string, int[]>> Fam(params (string, int[])[] f) =>
            f.Select(x => new KeyValuePair<string, int[]>(x.Item1, x.Item2)).ToList();

        private static readonly Stage[] ClaireStages =
        {
            new Stage(null, true, 0), new Stage(null, true, 1), new Stage(null, true, 2),
            new Stage(null, false, 0), new Stage(null, false, 1),
        };

        public static readonly Dictionary<string, Dictionary<string, Stage[]>> Stages =
            new Dictionary<string, Dictionary<string, Stage[]>>
            {
                ["pl1000"] = new Dictionary<string, Stage[]> { ["normal"] = ClaireStages, ["alt"] = ClaireStages },
                ["pl0000"] = new Dictionary<string, Stage[]>
                {
                    ["normal"] = new[]
                    {
                        new Stage("Casual", false, 0), new Stage("Police", false, 0), new Stage("Police", false, 1),
                        new Stage("Police (Injured)", false, 0), new Stage("Police (Injured)", false, 1),
                    },
                    ["alt"] = new[]
                    {
                        new Stage("Classic Police", false, 0), new Stage("Classic Police", false, 0), new Stage("Classic Police", false, 0),
                        new Stage("Classic Police (Injured)", false, 0), new Stage("Classic Police (Injured)", false, 0),
                    },
                },
            };

        // file group -> character id (groups without a name use their own code, e.g. "pl7500")
        public static readonly Dictionary<string, string> GroupChar = new Dictionary<string, string>
        {
            ["pl1000"] = "claire", ["pl0000"] = "leon", ["pl2000"] = "ada", ["pl3000"] = "sherry",
            ["pl4000"] = "hunk", ["pl4100"] = "tofu", ["pl5000"] = "kendo", ["pl6400"] = "katherine", ["pl5600"] = "ghost",
        };

        // names of extra costumes (characters without a story)
        public static readonly Dictionary<string, Dictionary<int, string>> MenuNames = new Dictionary<string, Dictionary<int, string>>
        {
            // DLC costumes of the game menu (Claire: a Noir, b Military, c Elza Walker, as in the game's costume list; d is not confirmed)
            ["pl1000"] = new Dictionary<int, string> { [10] = "Noir", [11] = "Military", [12] = "Elza Walker", [13] = "'98" },
            // Leon (checked against the DLC paks, 2026-11-25): a Noir (pl0004), b Arklay Sheriff (pl0005), d '98 (pl0007 + face pl0057)
            ["pl0000"] = new Dictionary<int, string> { [10] = "Noir", [11] = "Arklay Sheriff", [13] = "'98" },
            ["pl4100"] = new Dictionary<int, string> { [0] = "Tofu", [1] = "Uiro", [2] = "Annin Tofu", [3] = "Konnyaku", [4] = "Flan" },
        };

        public static string CharOf(string group) => GroupChar.TryGetValue(group, out var c) ? c : group;

        // prefab versions: 16 = non-RT build (DX11), 17 = RT build (DX12)
        public static readonly string[] PfbVers = { "16", "17" };

        private static readonly Regex Printable = new Regex("[ -~]{6,}");

        /// <summary>Reads the costume prefabs (loose files in gameDir, if any, else from the paks).</summary>
        public static CostumeTable Table(GamePaks paks, string gameDir, string group)
        {
            var table = new CostumeTable();
            var ids = new List<string> { "default" };
            ids.AddRange("123456789abcdef".Select(ch => ch.ToString()));
            foreach (var id in ids)
            {
                int num = id == "default" ? 0 : Convert.ToInt32(id, 16);
                foreach (var part in Parts)
                {
                    string relBase = $"objectroot/prefab/character/survivor/parts/{group}/{group}_{part}_{(id == "default" ? "default" : "costume_" + id)}.pfb.";
                    byte[] buf = null;
                    foreach (var ver in PfbVers) // non-RT and RT
                    {
                        string rel = relBase + ver;
                        foreach (var plat in new[] { "x64", "stm" }) // non-RT and RT platform folders
                        {
                            string loose = Path.Combine(gameDir, "natives", plat, rel.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(loose)) { buf = File.ReadAllBytes(loose); break; }
                        }
                        if (buf == null) buf = paks?.ReadAny("natives/x64/" + rel);
                        if (buf != null) break;
                    }
                    if (buf == null) continue;
                    string text = Encoding.Unicode.GetString(buf, 0, buf.Length & ~1);
                    // character files from any group (the Forgotten Soldier uses Hunk's body, pl4000)
                    var s = Printable.Matches(text).Cast<Match>().Select(m => m.Value.ToLowerInvariant())
                        .Where(x => x.Contains("sectionroot/character/player/")).ToList();
                    string mdf = s.FirstOrDefault(x => x.EndsWith(".mdf2", StringComparison.Ordinal));
                    if (mdf == null) continue;
                    string mesh = s.FirstOrDefault(x => x.EndsWith(".mesh", StringComparison.Ordinal) && !x.EndsWith("_blend.mesh", StringComparison.Ordinal));
                    if (!table.TryGetValue(num, out var parts)) table[num] = parts = new Dictionary<string, CostumePart>();
                    parts[part] = new CostumePart
                    {
                        Mdf = mdf,
                        Mesh = mesh,
                        Skin = s.FirstOrDefault(x => x.EndsWith("_blend.mesh", StringComparison.Ordinal)) ?? mesh,
                        Chain = s.FirstOrDefault(x => x.EndsWith(".chain", StringComparison.Ordinal)),
                    };
                }
            }
            return table;
        }

        /// <summary>Costume prefabs shipped by the MOD ITSELF (e.g. PS1 links costume b back to the normal face/hair).</summary>
        public static CostumeTable ModTable(string modRoot, string group)
        {
            if (!Directory.Exists(Path.Combine(modRoot, "natives", "x64", "objectroot")) && !Directory.Exists(Path.Combine(modRoot, "natives", "stm", "objectroot"))) return new CostumeTable();
            return Table(null, modRoot, group);
        }

        /// <summary>(family, stage) of a costume, or null if it isn't in any family.</summary>
        public static Tuple<string, int> StageOf(string group, int costume)
        {
            if (!Families.TryGetValue(group, out var fams)) return null;
            foreach (var f in fams)
            {
                int i = Array.IndexOf(f.Value, costume);
                if (i >= 0) return Tuple.Create(f.Key, i);
            }
            return null;
        }

        /// <summary>Variant label from the stages it covers (uses the cleanest stage).</summary>
        public static string StageLabel(string group, List<Tuple<string, int>> pairs)
        {
            if (pairs.Count == 0) return null;
            if (!Stages.TryGetValue(group, out var byFam)) return "stage " + (pairs.Min(p => p.Item2) + 1);
            var defs = pairs
                .Select((p, order) => new { p.Item2, order, fam = p.Item1, d = byFam.TryGetValue(p.Item1, out var arr) && p.Item2 < arr.Length ? arr[p.Item2] : null })
                .Where(x => x.d != null)
                .OrderBy(x => x.d.Dirt).ThenBy(x => x.Item2).ThenBy(x => x.order)
                .ToList();
            if (defs.Count == 0) return null;
            var d = defs[0].d;
            string dirt = d.Dirt == 1 ? ", dirty" : d.Dirt == 2 ? ", very dirty" : "";
            // a name shared by both families ("Jacket", "Tank top") gets the family in front: "Normal Jacket", "Alternative Tank top"
            string prefix = d.Name == null && Families.TryGetValue(group, out var ff) && ff.Count > 1
                ? (defs[0].fam == "alt" ? "Classic " : "") : ""; // the game calls the alternative costume "Classic"
            return prefix + (d.Name ?? (d.Jacket ? "Jacket" : "Tank top")) + dirt;
        }

        /// <summary>Every group with costume prefabs in the game (pl0000..pl9900).</summary>
        public static List<string> AllGroups(GamePaks paks)
        {
            var output = new List<string>();
            for (int i = 0; i < 100; i++)
            {
                string g = "pl" + (i * 100).ToString("D4");
                if (PfbVers.Any(v => paks.ExistsAny($"natives/x64/objectroot/prefab/character/survivor/parts/{g}/{g}_body_default.pfb.{v}"))) output.Add(g);
            }
            return output;
        }
    }
}
