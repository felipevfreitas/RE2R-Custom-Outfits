// Dlc.cs - costume DLCs of each game and whether they are installed in the game folder.
// Each DLC ships its own pak, named by its Steam app id:
//   RE2: <game>/<id>/re_dlc_000.pak   RE3: <game>/dlc/re_dlc_stm_<id>.pak
// Which pak brings which costume was checked by reading the paks (2026-11-25): body/face/hair files and costume prefabs.
//   RE2 Claire: 920563 = costume a (pl1005, Noir), 920562 = b (pl1006, Military), 920564 = c (pl1004, Elza Walker),
//       920566 = d (pl1007 + face pl1057, '98)
//   RE2 Leon:   920561 = a (pl0004, Noir), 920560 = b (pl0005, Arklay Sheriff), 920565 = d (pl0007 + face pl0057, '98)
//   RE3:        1158730 = Classic Costume Pack (Jill pl2020, Carlos face pl0011 / hair pl0015)
// Names from the Steam store pages. The other RE2 DLC paks (920567-920571) are weapons, soundtrack, unlocks.
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CustomOutfits
{
    public sealed class DlcStatus
    {
        public string Id;
        public string Name;
        public bool Installed;
    }

    public static class Dlcs
    {
        private static readonly Dictionary<string, (string Id, string Name)[]> Costume = new Dictionary<string, (string, string)[]>
        {
            ["re2"] = new[]
            {
                ("920561", "Leon: Noir"), ("920560", "Leon: Arklay Sheriff"), ("920565", "Leon: '98"),
                ("920563", "Claire: Noir"), ("920562", "Claire: Military"), ("920564", "Claire: Elza Walker"), ("920566", "Claire: '98"),
            },
            ["re3"] = new[] { ("1158730", "Classic Costume Pack (Jill, Carlos)") },
        };

        /// <summary>The costume DLCs of a game, each marked installed or not (empty list for a game without costume DLCs).</summary>
        public static List<DlcStatus> Scan(string game, string gameDir)
        {
            if (!Costume.TryGetValue(game, out var list)) return new List<DlcStatus>();
            return list.Select(d => new DlcStatus { Id = d.Id, Name = d.Name, Installed = IsInstalled(game, gameDir, d.Id) }).ToList();
        }

        private static bool IsInstalled(string game, string gameDir, string id)
        {
            if (string.IsNullOrEmpty(gameDir)) return false;
            string pak = game == "re3" ? Path.Combine(gameDir, "dlc", "re_dlc_stm_" + id + ".pak") : Path.Combine(gameDir, id, "re_dlc_000.pak");
            try { return File.Exists(pak) && new FileInfo(pak).Length > 16; } // (empty placeholder paks don't count)
            catch { return false; }
        }
    }
}
