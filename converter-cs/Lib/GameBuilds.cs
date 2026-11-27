// GameBuilds.cs - which version of the game is installed (DX11 non-RT or DX12 RT) and which one a mod is made for.
// The two builds use different file versions, so a mod made for one doesn't load in the other:
//   RE2 non-RT: natives/x64, .mesh.1808312334, .mdf2.10, .tex.10       RE2 RT: natives/stm, .mesh.2109108288, .mdf2.21, .tex.34
//   RE3 non-RT: natives/stm, .mesh.1902042334, .mdf2.13, .tex.190820018 RE3 RT: .mdf2.21, .tex.34 (like RE2 RT; not verified)
// Installed build: a file only the non-RT build has (checked in the paks, 2026-11-25), else one of the RT build, else the
// Steam beta branch ("dx11_non-rt") in the game's appmanifest.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CustomOutfits
{
    public static class GameBuilds
    {
        public const string Rt = "rt", NonRt = "nonrt", Both = "both";

        public static string Name(string build) =>
            build == Rt ? "DX12 (RT)" : build == NonRt ? "DX11 (non-RT)" : build == Both ? "DX11 and DX12" : "unknown version";

        private static readonly Dictionary<string, (string NonRt, string Rt, string AppId)> Probe = new Dictionary<string, (string, string, string)>
        {
            ["re2"] = ("natives/x64/sectionroot/character/player/pl1000/pl1000/pl1000.mdf2.10",
                       "natives/stm/sectionroot/character/player/pl1000/pl1000/pl1000.mdf2.21", "883710"),
            ["re3"] = ("natives/stm/escape/character/player/pl2000/mesh/pl2000.mdf2.13",
                       "natives/stm/escape/character/player/pl2000/mesh/pl2000.mdf2.21", "952060"),
        };

        /// <summary>"rt", "nonrt" or null (unknown) for the game installed in dir.</summary>
        public static string Detect(string game, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Probe.TryGetValue(game, out var p)) return null;
            try
            {
                var paks = new GamePaks(dir);
                if (paks.Exists(p.NonRt)) return NonRt;
                if (paks.Exists(p.Rt)) return Rt;
            }
            catch { /* unreadable paks: try Steam */ }
            try
            {
                // <library>/steamapps/common/<game> -> <library>/steamapps/appmanifest_<id>.acf
                string acf = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir.TrimEnd('\\', '/'))), "appmanifest_" + p.AppId + ".acf");
                if (File.Exists(acf))
                {
                    var m = Regex.Match(File.ReadAllText(acf), "\"BetaKey\"\\s+\"([^\"]*)\"");
                    return m.Success && m.Groups[1].Value.IndexOf("non-rt", StringComparison.OrdinalIgnoreCase) >= 0 ? NonRt : Rt;
                }
            }
            catch { }
            return null;
        }

        private static readonly Regex Versioned = new Regex(@"\.(mesh|mdf2|tex)\.(\d+)$", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> NonRtVers = new HashSet<string> { "mesh.1808312334", "mdf2.10", "tex.10", "mesh.1902042334", "mdf2.13", "tex.190820018" };
        private static readonly HashSet<string> RtVers = new HashSet<string> { "mesh.2109108288", "mdf2.21", "tex.34" };

        /// <summary>Which build a mod's files are for: "rt", "nonrt", "both" (it ships the two) or null (no mesh/mdf2/tex).</summary>
        public static string OfFiles(IEnumerable<string> files)
        {
            bool rt = false, non = false;
            foreach (var f in files)
            {
                var m = Versioned.Match(f);
                if (!m.Success) continue;
                string v = m.Groups[1].Value.ToLowerInvariant() + "." + m.Groups[2].Value;
                if (RtVers.Contains(v)) rt = true;
                else if (NonRtVers.Contains(v)) non = true;
                if (rt && non) return Both;
            }
            return rt ? Rt : non ? NonRt : null;
        }

        /// <summary>The mod is for the other build only (it won't load in this game as it is).</summary>
        public static bool Mismatch(string modBuild, string gameBuild) =>
            gameBuild != null && (modBuild == Rt || modBuild == NonRt) && modBuild != gameBuild;
    }
}
