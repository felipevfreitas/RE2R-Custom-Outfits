// Core.cs - converter core.
// Analyze(input): opens the mod (.zip/.rar/.7z or folder), finds its options (folders with natives/, each with
//   its own modinfo.ini), runs a dry conversion and returns the plan with suggested names.
// Build(plan, outZip): converts each option with the chosen names, writes modinfo.ini and creates the .zip
//   ready for Fluffy Mod Manager (one folder with modinfo.ini + natives/ + reframework/).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharpCompress.Archives;

namespace CustomOutfits
{
    /// <summary>One outfit of a body (a pack has several): the game costume it replaces and the name it will have.</summary>
    public sealed class PlanVariant
    {
        public PlanOutfit Owner;
        public string Picture; // image chosen for this entry (only used when the pack has one entry per outfit)
        public string Key;
        public string Slot;    // the game costume it replaces (fixed text)
        public string Name;    // name typed by the user; empty = "<body name> - <Slot>"
        public bool PictureDrawn; // Picture is a pose drawn from this outfit (transparent PNG, shown whole) rather than an image of the user's
        public List<int> Costumes = new List<int>(); // the game costumes it replaces directly
        public string Root;    // the mod folder (option) it comes from
        public bool Disabled;  // switched off in the window: left out of the package (its costumes: the other versions, else the game)
        public string Drawn;   // automatic picture drawn from this outfit (plan's pose), when the mod has no costume-menu picture for it
    }

    public sealed class PlanOutfit
    {
        public List<PlanVariant> Pickable = new List<PlanVariant>(); // the variants a game costume points to (body: what the Specific Outfit submenu lists; face/hair: the options of the entry)
        public bool Disabled;    // switched off in the window: left out of the package
        public bool EachEntry;   // body: one entry per outfit in the game's costume menu (else one that follows the campaign)
        public string Character, CharacterName, Part, PartName, Name;
        public List<string> Variants = new List<string>();
        public List<string> Also = new List<string>();
        public string Drawn; // body: picture drawn from the outfit (PNG), used when the mod has no image
    }

    /// <summary>An add-on the user gave a mod in the window (a separate archive: the colours of a skin...).</summary>
    public sealed class PlanAddon
    {
        public string Input, Name, Error; // Error: could not be read (nothing for this game, broken archive)
        public List<string> Roots;        // its mod folders, joined to the mod's as add-on options
    }

    public sealed class PlanOption
    {
        public string AddonInput; // it comes from this add-on (window), else from the mod itself
        public string Root, Name, Slug, Author, Version, Screenshot;
        public bool IsAddon; // modinfo.ini with "addonfor=": add-on (face/hair) of another option of the mod
        public string BaseRoot, BaseSlug; // the option it is an add-on for (its files the add-on does not bring come from there)
        public List<PlanOutfit> Outfits = new List<PlanOutfit>();
        public List<string> Warnings = new List<string>();
    }

    public sealed class Plan
    {
        public string Input, Extracted, Game = "re2", GameDir, BaseName, PackageName;
        public Dictionary<string, string> GroupNames = new Dictionary<string, string>(); // "character|part" -> name in the Wardrobe menu of the outfits of that part (a package with several: the colours of a hair mod)
        public bool IsArchive;
        public bool PicturesDrawn;     // DrawMissingPictures already ran (drawn, or not drawable)
        public List<PlanAddon> Addons = new List<PlanAddon>(); // add-ons the user gave this mod in the window (separate archives)
        public List<string> AddonDirs = new List<string>();    // where they were extracted (deleted with the plan)
        public string ModBuild;        // game version the mod's files are for: "rt", "nonrt", "both" or null (no versioned files)
        public string CustomPicture;  // image the user chose for the game costume menu (wins over the mod's)
        public bool HasMenuPicture;    // the mod brings its own costume-menu picture (ui0601_*_iam.tex)
        public string MenuPictureFile; // one of them (preview in the window)
        public string DrawDir;         // temporary folder of the drawn pictures
        public int Pose;               // pose of the drawn pictures (OutfitRender.PoseNames)
        public string PictureChoice;   // the user's choice in the window: null = automatic, "mod" = the mod's image, "pose" = drawn (Pose); CustomPicture = an image of theirs
        public List<KeyValuePair<PlanOption, ConvertReport>> DrawReports; // what the pictures are drawn from (drawn again with another pose)
        public List<PlanOption> Options = new List<PlanOption>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>The mod is for a game whose folder is unknown: the window asks for that folder and tries again.</summary>
    public sealed class GameFolderMissingException : IOException
    {
        public string Game { get; }
        public GameFolderMissingException(string game, string message) : base(message) { Game = game; }
    }

    public sealed class BuildResult
    {
        public string Output;
        public int Outfits;
        public List<string> Warnings = new List<string>();
    }

    public static class Core
    {
        public static readonly Dictionary<string, string> CharNames = new Dictionary<string, string>
        {
            ["jill"] = "Jill", ["carlos"] = "Carlos",
            ["claire"] = "Claire", ["leon"] = "Leon", ["ada"] = "Ada", ["sherry"] = "Sherry", ["hunk"] = "Hunk", ["tofu"] = "Tofu",
            ["kendo"] = "Robert Kendo", ["katherine"] = "Katherine (Runaway)", ["ghost"] = "Forgotten Soldier",
        };
        public static readonly Dictionary<string, string> PartNames = new Dictionary<string, string>
        {
            ["body"] = "Body", ["face"] = "Face", ["hair"] = "Hair",
        };
        private static readonly Regex Archive = new Regex(@"\.(zip|rar|7z)$", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------------ game folder
        // games: "re2" (Resident Evil 2 Remake) and "re3" (Resident Evil 3 Remake)
        // (re4 / re9: shown on the game picker, not supported yet)
        public static readonly Dictionary<string, string> GameNames = new Dictionary<string, string>
        {
            ["re2"] = "Resident Evil 2", ["re3"] = "Resident Evil 3", ["re4"] = "Resident Evil 4", ["re9"] = "Resident Evil Requiem",
        };
        private static readonly Dictionary<string, string> GameFolders = new Dictionary<string, string>
        {
            ["re2"] = "RESIDENT EVIL 2  BIOHAZARD RE2", ["re3"] = "RE3",
        };

        public static bool IsGameDir(string d, string game = "re2") =>
            !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, game + ".exe")) && File.Exists(Path.Combine(d, "re_chunk_000.pak"));

        /// <summary>Which game a mod is for: RE3 keeps its characters under natives/*/escape/, RE4 everything under
        /// natives/stm/_chainsaw/; anything else is taken as RE2.</summary>
        public static string GameOfMod(string modRoot)
        {
            if (Re3.IsRe3Mod(modRoot)) return "re3";
            if (Directory.Exists(Path.Combine(modRoot, "natives", "stm", "_chainsaw"))) return "re4";
            return "re2";
        }

        /// <summary>Whether a mod folder has files for this game (one folder can hold files for two games).</summary>
        public static bool ModHasGame(string modRoot, string game)
        {
            switch (game)
            {
                case "re3": return Re3.IsRe3Mod(modRoot);
                case "re4": return Directory.Exists(Path.Combine(modRoot, "natives", "stm", "_chainsaw"));
                case "re2":
                    // RE2 keeps its characters in sectionroot/character/player/ (costume prefabs in objectroot/prefab/character/),
                    // under natives/x64 or natives/stm (RE3 has some sectionroot/ files too, but not its characters)
                    return new[] { "x64", "stm" }.Any(pl => Directory.Exists(Path.Combine(modRoot, "natives", pl, "sectionroot", "character", "player"))
                        || Directory.Exists(Path.Combine(modRoot, "natives", pl, "objectroot", "prefab", "character")));
                default: return GameOfMod(modRoot) == game;
            }
        }

        /// <summary>Default Steam path or any Steam library (libraryfolders.vdf).</summary>
        public static string DetectGameDir(string game = "re2")
        {
            var libs = new List<string> { @"C:\Program Files (x86)\Steam" };
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k?.GetValue("SteamPath") is string sp && sp.Length > 0) libs.Insert(0, sp.Replace('/', '\\'));
            }
            catch { /* Steam not in the registry */ }
            foreach (var steam in libs.ToList())
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            foreach (var lib in libs)
            {
                string d = Path.Combine(lib, "steamapps", "common", GameFolders[game]);
                if (IsGameDir(d, game)) return d;
            }
            return null;
        }

        // ------------------------------------------------------------------ utilities
        private static string TempRoot => Path.Combine(Path.GetTempPath(), "CustomOutfitsConverter");

        private static string TempDir(string prefix)
        {
            string d = Path.Combine(TempRoot, prefix + "_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Deletes temporary folders left by earlier runs (closed by force, crashed, files in use).
        /// Extracted mods can take gigabytes. Only folders older than a few hours, so another open window is safe.</summary>
        public static void SweepOldTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                foreach (var d in Directory.GetDirectories(TempRoot))
                    if (DateTime.Now - Directory.GetCreationTime(d) > TimeSpan.FromHours(6)) TryDelete(d);
            }
            catch { /* best effort */ }
        }

        /// <summary>Extracts .zip/.rar/.7z. Every path is checked: nothing can be written outside dest.</summary>
        private static void Extract(string file, string dest)
        {
            string root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // .zip: Windows' own decompressor (native zlib) is ~5x faster than SharpCompress (527 MB pack: 1.8 s vs 9.7 s);
            // a zip it can't read (Deflate64, odd headers) goes to SharpCompress below
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                        foreach (var e in zip.Entries)
                        {
                            if (!Needed(e.FullName)) continue;
                            string target = SafeTarget(root, e.FullName, e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"));
                            if (target == null) continue;
                            using (var src = e.Open())
                            using (var fs = File.Create(target)) src.CopyTo(fs);
                        }
                    return;
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is InvalidDataException)
                {
                    foreach (var d in Directory.GetDirectories(dest)) TryDelete(d); // start again with SharpCompress
                    foreach (var f in Directory.GetFiles(dest)) File.Delete(f);
                }
            }
            using (var archive = ArchiveFactory.OpenArchive(file, null))
            {
                if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
                {
                    // solid archives and .7z: sequential reading (much faster than opening entry by entry)
                    using (var reader = archive.ExtractAllEntries())
                    {
                        while (reader.MoveToNextEntry())
                        {
                            string target = SafeTarget(root, reader.Entry.Key, reader.Entry.IsDirectory);
                            if (target == null) continue;
                            using (var fs = File.Create(target)) reader.WriteEntryTo(fs);
                        }
                    }
                }
                else
                {
                    foreach (var entry in archive.Entries)
                    {
                        string target = SafeTarget(root, entry.Key, entry.IsDirectory);
                        if (target == null) continue;
                        using (var src = entry.OpenEntryStream())
                        using (var fs = File.Create(target)) src.CopyTo(fs);
                    }
                }
            }
        }

        // what the converter reads from a mod: natives/ (the game files), modinfo.ini and images (its picture); the rest
        // (texture sources .dds/.psd, readmes, videos, nested archives) is not extracted
        private static readonly Regex NeededImage = new Regex(@"\.(png|jpe?g|bmp|gif|webp)$", RegexOptions.IgnoreCase);
        private static bool Needed(string key)
        {
            string k = key.Replace('\\', '/');
            return k.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) || k.IndexOf("/natives/", StringComparison.OrdinalIgnoreCase) >= 0
                || k.EndsWith("modinfo.ini", StringComparison.OrdinalIgnoreCase) || NeededImage.IsMatch(k);
        }

        // target of an archive entry, or null to skip (folder, empty name, not needed, or outside root = "zip slip")
        private static string SafeTarget(string root, string key, bool isDir)
        {
            if (isDir || string.IsNullOrEmpty(key) || !Needed(key)) return null;
            string target = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            return target;
        }

        private static Dictionary<string, string> ReadModinfo(string dir)
        {
            var info = new Dictionary<string, string>();
            string f = Path.Combine(dir, "modinfo.ini");
            if (!File.Exists(f)) return info;
            foreach (var line in File.ReadAllLines(f))
            {
                var m = Regex.Match(line, @"^\s*([^=;#]+?)\s*=\s*(.*?)\s*$");
                if (m.Success) info[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            }
            return info;
        }

        // folders that contain natives/ (mod options); skips the ones inside another option
        private static void FindModRoots(string dir, int depth, List<string> output)
        {
            if (depth > 5) return;
            var subdirs = Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            if (subdirs.Any(d => Path.GetFileName(d).Equals("natives", StringComparison.OrdinalIgnoreCase))) { output.Add(dir); return; }
            foreach (var d in subdirs) FindModRoots(d, depth + 1, output);
        }

        /// <summary>"Claire - Zky's Classic Costume-985-2-1-1774584586.rar" -> "Claire - Zky's Classic Costume"</summary>
        public static string CleanName(string s)
        {
            s = Archive.Replace(s, "");
            s = Regex.Replace(s, @"(-\d+)+$", "");        // Nexus suffix (id-version-date)
            // newer Nexus suffix: "<id> <version> <date>T<time>Z <code>", e.g. "2535 1.0 2026-07-15T12-27Z pAPMqGlpR"
            s = Regex.Replace(s, @"(\s+\d+)?\s+\S+\s+\d{4}-\d\d-\d\dT[\d-]+Z(\s+[A-Za-z0-9]+)?$", "");
            s = Regex.Replace(s, "_+", " ");
            s = Regex.Replace(s, @"\s*\|\s*", " - ");
            s = Regex.Replace(s, @"^RE2R?\s+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim();
        }

        // suggested name: removes character names and the part word, which the menu already shows
        private static readonly Regex CharWords = new Regex(@"\b(claire( redfield)?|leon( s\.?)?( kennedy)?|ada( wong)?|sherry( birkin)?|jill( valentine)?|carlos( oliveira)?)\b", RegexOptions.IgnoreCase);
        private static readonly Dictionary<string, Regex> PartWords = new Dictionary<string, Regex>
        {
            ["body"] = new Regex(@"\b(outfit|body)\b", RegexOptions.IgnoreCase),
            ["face"] = new Regex(@"\b(face|head)\b", RegexOptions.IgnoreCase),
            ["hair"] = new Regex(@"\bhair\b", RegexOptions.IgnoreCase),
        };

        private static string OutfitName(string optionName, string part)
        {
            string n = CharWords.Replace(optionName, " ");
            if (PartWords.TryGetValue(part, out var re)) n = re.Replace(n, " ");
            n = Regex.Replace(n, @"\s+", " ");
            n = Regex.Replace(n, @"(\s*-\s*)+", " - ");
            n = Regex.Replace(n, @"^[\s\-:|]+|[\s\-:|]+$", "").Trim();
            return n.Length > 0 ? n : optionName;
        }

        // short code from the file name: two conversions with the same name don't share folders
        private static string ShortHash(string s)
        {
            uint h = 2166136261;
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            string b36 = "";
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            do { b36 = digits[(int)(h % 36)] + b36; h /= 36; } while (h > 0);
            return b36.Length > 4 ? b36.Substring(0, 4) : b36;
        }

        private static string Slugify(string s)
        {
            string n = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            n = Regex.Replace(n, "[^a-z0-9]+", "_");
            n = Regex.Replace(n, "^_|_$", "");
            if (n.Length > 40) n = n.Substring(0, 40);
            return n.Length > 0 ? n : "mod";
        }

        private static string SafeFileName(string s)
        {
            string n = Regex.Replace(s, "[<>:\"/\\\\|?*]+", "");
            n = Regex.Replace(n, @"\s+", " ").Trim();
            // the package folder is part of every file path: keep it short (Windows paths are limited to 260 characters)
            if (n.Length > 60) n = n.Substring(0, 60).Trim();
            return n.Length > 0 ? n : "Wardrobe";
        }

        // ------------------------------------------------------------------ game of an archive, without extracting it
        // natives/<platform>/<what> -> game (the same folders ModHasGame looks for)
        private static readonly Regex NativesPath = new Regex(@"(^|/)natives/(x64|stm)/(.+)$", RegexOptions.IgnoreCase);

        private static string GameOfPath(string key)
        {
            var m = NativesPath.Match(key.Replace('\\', '/'));
            if (!m.Success) return null;
            string rest = m.Groups[3].Value.ToLowerInvariant();
            if (rest.StartsWith("escape/character/player/")) return "re3";
            if (rest.StartsWith("_chainsaw/")) return "re4";
            if (rest.StartsWith("sectionroot/character/player/") || rest.StartsWith("objectroot/prefab/character/")) return "re2";
            if (rest.StartsWith("custom_outfits/") || rest.StartsWith("streaming/custom_outfits/")) return "wardrobe";
            return "";
        }

        private const string AlreadyConverted = "Already converted: this is a Wardrobe package. Install it in Fluffy Mod Manager as it is.";

        private static bool IsConvertedRoot(string root) =>
            new[] { "x64", "stm" }.Any(pl => Directory.Exists(Path.Combine(root, "natives", pl, "custom_outfits")));

        /// <summary>Refuses an archive with no files for this game, reading only its list of files.</summary>
        private static void CheckArchiveGame(string file, string onlyGame)
        {
            var games = new HashSet<string>();
            bool natives = false;
            try
            {
                using (var archive = ArchiveFactory.OpenArchive(file, null))
                    foreach (var e in archive.Entries)
                    {
                        if (e.IsDirectory || string.IsNullOrEmpty(e.Key)) continue;
                        string g = GameOfPath(e.Key);
                        if (g == null) continue;
                        natives = true;
                        if (g == onlyGame) return; // found one: the mod is for this game
                        if (g.Length > 0) games.Add(g);
                    }
            }
            catch (Exception e) { throw new IOException("Could not open the archive: " + e.Message); }
            if (!natives) throw new IOException("No mod found: there is no 'natives' folder inside.");
            if (games.Remove("wardrobe") && games.Count == 0) throw new IOException(AlreadyConverted);
            if (games.Count == 0) throw new IOException("No character outfit found in this mod (only other files).");
            throw new IOException($"Not a {GameNames[onlyGame]} mod (it is for {string.Join(" / ", games.Select(g => GameNames.TryGetValue(g, out var n) ? n : g))}). Choose that game on the first screen to convert it.");
        }

        // ------------------------------------------------------------------ analyze
        /// <summary>re2Dir: the RE2 folder; the RE3 folder is detected through Steam.</summary>
        public static Plan Analyze(string input, string re2Dir) => Analyze(input, g => g == "re2" ? re2Dir : DetectGameDir(g));

        /// <summary>gameDirOf("re2"/"re3") gives the game folder (null when unknown).</summary>
        public static Plan Analyze(string input, Func<string, string> gameDirOf) => Analyze(input, gameDirOf, null, false, null, null);

        /// <summary>Only the parts of the mod for onlyGame (the game picked in the window): a mod of another game is refused,
        /// and the folders of a pack that are for another game are left out (with a warning).</summary>
        /// drawLater: the pictures of a mod without one are not drawn here (the window shows the mod at once and calls DrawMissingPictures).
        /// addons: archives/folders the user gave this mod as add-ons; reuseExtracted: the mod already extracted (read again with add-ons).
        public static Plan Analyze(string input, string onlyGame, string gameDir, bool drawLater = false, IList<string> addons = null, string reuseExtracted = null) =>
            Analyze(input, g => g == onlyGame ? gameDir : null, onlyGame, drawLater, addons, reuseExtracted);

        private static Plan Analyze(string input, Func<string, string> gameDirOf, string onlyGame, bool drawLater, IList<string> addons, string reuseExtracted)
        {
            // (CO_TIMING: how long each step took, as a warning of the mod)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var steps = new List<string>();
            void Step(string what) { steps.Add(what + " " + clock.ElapsedMilliseconds + " ms"); clock.Restart(); }
            if (!File.Exists(input) && !Directory.Exists(input)) throw new IOException("File or folder not found: " + input);
            bool isArchive = File.Exists(input);
            if (isArchive && !Archive.IsMatch(input)) throw new IOException("Unsupported file. Use .zip, .rar, .7z or a folder.");
            // the game first: from the archive's list of files (no extraction, a fraction of a second), so a mod of another game
            // is refused before the slow part (extracting can take a minute for big mods)
            if (isArchive && onlyGame != null && reuseExtracted == null) CheckArchiveGame(input, onlyGame);
            string workDir = input, extracted = null;
            if (isArchive && reuseExtracted != null && Directory.Exists(reuseExtracted)) workDir = extracted = reuseExtracted; // (read again: no new extraction)
            else if (isArchive)
            {
                extracted = TempDir("extract");
                try { Step("check"); Extract(input, extracted); Step("extract"); }
                catch (Exception e) { TryDelete(extracted); throw new IOException("Could not open the archive: " + e.Message); }
                workDir = extracted;
            }
            var roots = new List<string>();
            FindModRoots(workDir, 0, roots);
            if (roots.Count == 0) { TryDelete(extracted); throw new IOException("No mod found: there is no 'natives' folder inside."); }
            string game;
            var otherGames = new List<string>();
            if (onlyGame != null)
            {
                var gameOf = roots.ToDictionary(r => r, GameOfMod);
                var mine = roots.Where(r => ModHasGame(r, onlyGame)).ToList();
                foreach (var r in mine) gameOf[r] = onlyGame;
                if (mine.Count == 0)
                {
                    TryDelete(extracted);
                    var found = gameOf.Values.Distinct().Where(g => g != onlyGame).Select(g => GameNames.TryGetValue(g, out var gn) ? gn : g).ToList();
                    if (found.Count == 0 && roots.Any(IsConvertedRoot)) throw new IOException(AlreadyConverted);
                    if (found.Count == 0) throw new IOException("No character outfit found in this mod (only other files).");
                    throw new IOException($"Not a {GameNames[onlyGame]} mod (it is for {string.Join(" / ", found)}). Choose that game on the first screen to convert it.");
                }
                // a pack with folders for other games: those are left out
                foreach (var r in roots.Where(r => gameOf[r] != onlyGame))
                    otherGames.Add($"{Path.GetFileName(r)} ({(GameNames.TryGetValue(gameOf[r], out var gn) ? gn : gameOf[r])})");
                roots = mine;
                game = onlyGame;
            }
            else game = roots.Any(r => GameOfMod(r) == "re3") ? "re3" : "re2";
            string gameDir = gameDirOf(game);
            if (!IsGameDir(gameDir, game))
            {
                TryDelete(extracted);
                throw new GameFolderMissingException(game, $"This is a {GameNames[game]} mod, but the game folder was not found. Choose the {GameNames[game]} folder (the one with {game}.exe).");
            }

            string baseName = CleanName(Path.GetFileName(input.TrimEnd('\\', '/')));
            var plan = new Plan
            {
                Input = input, IsArchive = isArchive, Extracted = extracted, Game = game, GameDir = gameDir, BaseName = baseName,
                PackageName = baseName + " (Wardrobe)",
            };
            // add-ons the user gave this mod (converter window: a separate archive, e.g. the colours of a skin): their folders join
            // the mod's as add-on options of it, whatever their modinfo "addonfor=" says (the user picked the mod)
            var baseRoots = roots.ToList();
            var forcedAddon = new HashSet<string>();
            foreach (var a in addons ?? new List<string>())
            {
                string aName = Path.GetFileName(a.TrimEnd('\\', '/'));
                try
                {
                    bool aArchive = File.Exists(a);
                    if (aArchive && !Archive.IsMatch(a)) throw new IOException("unsupported file (use .zip, .rar, .7z or a folder)");
                    if (aArchive) CheckArchiveGame(a, game);
                    string aDir = a;
                    if (aArchive)
                    {
                        aDir = TempDir("addon");
                        plan.AddonDirs.Add(aDir);
                        Extract(a, aDir);
                    }
                    var aRoots = new List<string>();
                    FindModRoots(aDir, 0, aRoots);
                    aRoots = aRoots.Where(r => ModHasGame(r, game)).ToList();
                    if (aRoots.Count == 0) throw new IOException("no " + GameNames[game] + " files in it");
                    foreach (var r in aRoots) { roots.Add(r); forcedAddon.Add(r); }
                    plan.Addons.Add(new PlanAddon { Input = a, Name = CleanName(aName), Roots = aRoots });
                }
                catch (Exception e)
                {
                    plan.Addons.Add(new PlanAddon { Input = a, Name = CleanName(aName), Error = e.Message });
                    plan.Warnings.Add("Add-on " + CleanName(aName) + ": " + e.Message);
                }
            }
            // DX11 (non-RT) or DX12 (RT): from the versions of its files (the window compares it with the installed game)
            plan.ModBuild = GameBuilds.OfFiles(roots.SelectMany(r => Directory.Exists(Path.Combine(r, "natives"))
                ? Directory.EnumerateFiles(Path.Combine(r, "natives"), "*", SearchOption.AllDirectories) : Enumerable.Empty<string>()));
            if (otherGames.Count > 0)
                plan.Warnings.Add($"Left out (not for {GameNames[game]}): " + string.Join(", ", otherGames) + ".");
            var usedSlugs = new HashSet<string>();
            var reports = new List<KeyValuePair<PlanOption, ConvertReport>>();
            var dlcSwaps = new List<(string Option, string Part, string Label, List<string> Textures)>();
            // name and slug of every option first: an add-on needs the slug of the option it is for
            var metaOf = new Dictionary<string, (Dictionary<string, string> Info, string Name, string Slug)>();
            foreach (var root in roots)
            {
                var info = ReadModinfo(root);
                // (the mod's own folders decide: an add-on given in the window doesn't rename the mod's single option)
                bool single = baseRoots.Count == 1 && baseRoots[0] == root;
                var addonOf = plan.Addons.FirstOrDefault(x => x.Roots != null && x.Roots.Contains(root));
                string optName = CleanName(info.TryGetValue("name", out var mn) && mn.Length > 0 ? mn
                    : single ? baseName : addonOf != null && addonOf.Roots.Count == 1 ? addonOf.Name : Path.GetFileName(root));
                string slugBase = Slugify(CharWords.Replace(single ? baseName : optName, " "));
                if (game == "re3" && slugBase.Length > 24) slugBase = slugBase.Substring(0, 24).TrimEnd('_'); // RE3 paths are deeper
                string slug = slugBase + "_" + ShortHash(Path.GetFileName(input.TrimEnd('\\', '/')) + "|" + optName);
                while (usedSlugs.Contains(slug)) slug += "_2";
                usedSlugs.Add(slug);
                metaOf[root] = (info, optName, slug);
            }
            foreach (var root in roots)
            {
                var (info, optName, slug) = metaOf[root];
                // add-on: the option named by "addonfor=" (when it is in this mod)
                string baseRoot = null, baseSlug = null;
                if (info.TryGetValue("addonfor", out var addonFor))
                {
                    var b = roots.Where(r => r != root && string.Equals(metaOf[r].Info.TryGetValue("name", out var bn) ? bn.Trim() : "", addonFor.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (b.Count > 0) { baseRoot = b[0]; baseSlug = metaOf[baseRoot].Slug; }
                }
                // an add-on the user gave this mod: for the mod's main folder when its "addonfor=" names none of the mod's
                if (forcedAddon.Contains(root) && baseRoot == null)
                {
                    baseRoot = baseRoots.FirstOrDefault(r => !metaOf[r].Info.ContainsKey("addonfor")) ?? baseRoots.FirstOrDefault();
                    if (baseRoot != null) baseSlug = metaOf[baseRoot].Slug;
                }
                ConvertReport rep;
                try { rep = ConvertOption(game, root, null, slug, optName, new ConvertOptions { DryRun = true, GameDir = gameDir, BaseRoot = baseRoot, BaseSlug = baseSlug }); }
                catch (Exception e) { plan.Warnings.Add(optName + ": " + e.Message); continue; }
                string shot = info.TryGetValue("screenshot", out var sc) && sc.Length > 0 && File.Exists(Path.Combine(root, sc)) ? Path.Combine(root, sc) : null;
                var opt = new PlanOption
                {
                    Root = root, Name = optName, Slug = slug, Screenshot = shot,
                    Author = info.TryGetValue("author", out var au) ? au : "",
                    Version = info.TryGetValue("version", out var ve) ? ve : "",
                    IsAddon = info.ContainsKey("addonfor") || forcedAddon.Contains(root), BaseRoot = baseRoot, BaseSlug = baseSlug,
                    AddonInput = plan.Addons.FirstOrDefault(x => x.Roots != null && x.Roots.Contains(root))?.Input,
                    Warnings = rep.Warnings,
                };
                foreach (var o in rep.Outfits)
                {
                    var po = new PlanOutfit
                    {
                        Character = o.Character, CharacterName = CharNames.TryGetValue(o.Character, out var cn) ? cn : o.Character,
                        Part = o.Part, PartName = PartNames.TryGetValue(o.Part, out var pn) ? pn : o.Part,
                        Name = OutfitName(optName, o.Part),
                        Variants = o.VariantLabels,
                        // body: the outfits of a pack; face/hair: the versions a pack brings for different costumes (Noir hair, Elza Walker hair)
                        Pickable = PickableOf(o),
                        // RE2: on for big packs (a small one follows the story); RE3 has no story families, so any pack gets one entry per outfit
                        EachEntry = o.Part == "body" && PickableOf(o).Count > (Re3Menu.ContainsKey(o.Group ?? "") ? 1 : 4),
                        Also = o.Also.Select(c => CharNames.TryGetValue(c, out var an) ? an : c).ToList(),
                    };
                    foreach (var pv in po.Pickable) { pv.Owner = po; pv.Root = root; }
                    opt.Outfits.Add(po);
                }
                if (opt.Outfits.Count == 0) plan.Warnings.Add(optName + ": no character outfit found (only other files), skipped.");
                else { plan.Options.Add(opt); reports.Add(new KeyValuePair<PlanOption, ConvertReport>(opt, rep)); }
                foreach (var w in rep.Warnings) plan.Warnings.Add(optName + ": " + w);
                foreach (var d in rep.DlcSwaps) dlcSwaps.Add((optName, d.Part, d.Label, d.Textures));
            }
            Step("analyze");
            if (dlcSwaps.Count > 0) plan.Warnings.Add(DlcSwapWarning(dlcSwaps));
            plan.MenuPictureFile = roots.SelectMany(r => Directory.EnumerateFiles(r, "ui0601_*_iam.tex.*", SearchOption.AllDirectories))
                .OrderBy(f => f.EndsWith(".10") ? 0 : 1).FirstOrDefault();
            plan.HasMenuPicture = plan.MenuPictureFile != null;
            if (game == "re2" || game == "re3") plan.DrawReports = reports; // (pictures can be drawn later on request, see Redraw)
            if (!drawLater) { DrawMissingPictures(plan); Step("draw"); }
            if (Environment.GetEnvironmentVariable("CO_TIMING") != null) plan.Warnings.Add("timing: " + string.Join(", ", steps));
            if (plan.Options.Count == 0)
            {
                TryDelete(extracted);
                throw new IOException("This mod has no character outfits that can be converted.\n" + string.Join("\n", plan.Warnings));
            }
            return plan;
        }

        // ------------------------------------------------------------------ drawn pictures
        /// <summary>A mod without an image (or with one not made for the menu, see FitsMenu): the picture of each body, drawn from the
        /// outfit itself. Nothing to do if already drawn or if the mod has a picture. True when the mod needs this (pictures drawn).</summary>
        public static bool NeedsDrawnPictures(Plan plan) =>
            (plan.Game == "re2" || plan.Game == "re3") && plan.DrawReports != null && !plan.HasMenuPicture && plan.Options.All(o => !FitsMenu(o.Screenshot));

        public static void DrawMissingPictures(Plan plan)
        {
            // (the window draws them in the background; saving the mod meanwhile waits here for them)
            lock (plan)
            {
                if (plan.PicturesDrawn || !NeedsDrawnPictures(plan)) return;
                try { if (plan.Game == "re3") DrawPicturesRe3(plan, plan.DrawReports); else DrawPictures(plan, plan.DrawReports); }
                catch { /* no picture: the game shows a placeholder */ }
                finally { plan.PicturesDrawn = true; }
            }
        }

        private static readonly Dictionary<string, string> NonRtVersion = new Dictionary<string, string>
        {
            ["mesh"] = "1808312334", ["mdf2"] = "10", ["tex"] = "10",
        };

        /// <summary>A file by its game path (".mesh"/".mdf2"/".tex", no version), as the non-RT game would load it:
        /// the mod's (root), else a loose file of the game (gameDir), else the paks. A file the mod has only in another
        /// version (RT) can't be drawn: throws, so the original is never drawn by mistake.</summary>
        private static byte[] GameFile(string root, string gameDir, GamePaks paks, string virt, string prefix = "")
        {
            string ext = virt.Substring(virt.LastIndexOf('.') + 1).ToLowerInvariant();
            if (!NonRtVersion.TryGetValue(ext, out var ver)) return null;
            string rel = (prefix + virt + "." + ver).Replace('/', Path.DirectorySeparatorChar);
            if (root != null)
            {
                string f = Path.Combine(root, "natives", "x64", rel);
                if (File.Exists(f)) return File.ReadAllBytes(f);
                string name = Path.GetFileName(prefix + virt) + ".";
                foreach (var pl in Converter.Platforms)
                {
                    string dir = Path.GetDirectoryName(Path.Combine(root, "natives", pl, (prefix + virt).Replace('/', Path.DirectorySeparatorChar)));
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, name + "*").Any())
                        throw new NotSupportedException("the mod has " + virt + " only for the RT build");
                }
            }
            if (gameDir == null) return null;
            string loose = Path.Combine(gameDir, "natives", "x64", rel);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
            return paks?.Read("natives/x64/" + (prefix + virt + "." + ver).ToLowerInvariant());
        }

        // baseRoot: an add-on's files that it doesn't bring are its mod's (a colour add-on ships only materials)
        private static RenderPart DrawPart(string root, string gameDir, GamePaks paks, string mdf, string mesh, string baseRoot = null)
        {
            if (mdf == null || mesh == null) return null;
            var mdfB = GameFile(root, null, null, mdf) ?? GameFile(baseRoot, gameDir, paks, mdf);
            var meshB = GameFile(root, null, null, mesh) ?? GameFile(baseRoot, gameDir, paks, mesh);
            if (mdfB == null || meshB == null || Mdf2.IsRT(mdfB)) return null;
            // textures: the streaming copy (full size) first; the mod's, then the game's
            return new RenderPart
            {
                Mdf = mdfB, Mesh = meshB,
                Texture = t => GameFile(root, null, null, t, "streaming/") ?? GameFile(root, null, null, t)
                    ?? GameFile(baseRoot, null, null, t, "streaming/") ?? GameFile(baseRoot, null, null, t)
                    ?? GameFile(null, gameDir, paks, t, "streaming/") ?? GameFile(null, gameDir, paks, t),
            };
        }

        /// <summary>Draws every body of the mod (characters with a thumbnail pose) with the face and hair it is worn
        /// with in game: the same mod's (same option first), else the game's for the costume it is shown in.</summary>
        /// <summary>Draws the pictures of a mod in a pose (0 = the first) and makes them the ones used (window: "Change picture").
        /// Pictures already drawn in that pose are reused.</summary>
        public static void Redraw(Plan plan, int pose)
        {
            if (plan.DrawReports == null) return;
            plan.Pose = Math.Max(0, pose);
            if (plan.Game == "re3") DrawPicturesRe3(plan, plan.DrawReports); else DrawPictures(plan, plan.DrawReports);
        }

        /// <summary>The picture of the mod in a pose (for the choice in the window), without changing the one used; null if it
        /// can't be drawn.</summary>
        public static string PosePreview(Plan plan, int pose)
        {
            if (plan.DrawReports == null) return null;
            var all = plan.Options.SelectMany(o => o.Outfits).ToList();
            var keep = all.Select(o => o.Drawn).ToList();
            int keepPose = plan.Pose;
            try
            {
                foreach (var o in all) o.Drawn = null;
                Redraw(plan, pose);
                return (MainOption(plan)?.Outfits ?? new List<PlanOutfit>()).Concat(all).Select(o => o.Drawn).FirstOrDefault(d => d != null && File.Exists(d));
            }
            finally
            {
                for (int i = 0; i < all.Count; i++) all[i].Drawn = keep[i];
                plan.Pose = keepPose;
            }
        }

        /// <summary>How many poses the pictures of this mod can be drawn in (0 = none: no body of a posed character).</summary>
        public static int PoseCount(Plan plan)
        {
            if (plan.DrawReports == null) return 0;
            var counts = plan.DrawReports.SelectMany(r => r.Value.Outfits).Where(o => o.Part == "body" && o.Variants != null)
                .Select(o => OutfitRender.PoseCount(o.Character)).Where(n => n > 0).ToList();
            return counts.Count == 0 ? 0 : counts.Min();
        }

        // one drawing at a time: the main pictures (read queue) and the pack's pictures (mod screen) can be asked for together
        private static readonly object DrawLock = new object();

        /// <summary>RE2: one body variant in a pose, with the face and hair it is worn with in game (the same mod's, same option
        /// first, else the game's for that costume). RGBA 1024², or null if it can't be drawn.</summary>
        private static byte[] RenderBody(Plan plan, GamePaks paks, List<KeyValuePair<PlanOption, ConvertReport>> reports,
            KeyValuePair<PlanOption, ConvertReport> kv, OutfitInfo body, Variant bv, int poseIndex)
        {
            string poseKey = OutfitRender.PoseKey(body.Character, poseIndex) ?? body.Character;
            var pose = OutfitRender.Pose(poseKey);
            if (pose == null) return null;
            int costume = bv.Costumes.Count > 0 ? bv.Costumes[0] : 0;
            var first = DrawPart(kv.Key.Root, plan.GameDir, paks, bv.Replaces, bv.SrcMesh, kv.Key.BaseRoot);
            if (first == null) return null;
            var parts = new List<RenderPart> { first };
            var table = Costumes.Table(paks, plan.GameDir, body.Group);
            var modTable = Costumes.ModTable(kv.Key.Root, body.Group); // the face/hair the mod's own prefabs give each costume
            foreach (var p in new[] { "face", "hair" })
            {
                // the same rule as the script in game: the mod's face/hair if it covers this costume (one with a single version is
                // worn with any costume; one with several, only with the costumes of its versions), else what the mod's own
                // prefabs give this costume, else the game's
                var mine = reports.OrderBy(r => r.Key == kv.Key ? 0 : 1)
                    .SelectMany(r => r.Value.Outfits.Where(o => o.Character == body.Character && o.Part == p && o.Variants != null && o.Variants.Count > 0)
                        .Select(o => new { r.Key.Root, r.Key.BaseRoot, O = o, V = o.Variants.Count == 1 ? o.Variants[0] : o.Variants.FirstOrDefault(x => x.Costumes.Contains(costume)) }))
                    .FirstOrDefault(x => x.V != null);
                RenderPart rp = null;
                if (mine != null)
                {
                    var v = mine.V;
                    rp = DrawPart(mine.Root, plan.GameDir, paks, v.Replaces, v.SrcMesh, mine.BaseRoot);
                }
                else if (modTable.TryGetValue(costume, out var mps) && mps.TryGetValue(p, out var mp))
                    rp = DrawPart(kv.Key.Root, plan.GameDir, paks, mp.Mdf, mp.Mesh, kv.Key.BaseRoot); // (its files: the mod's, else the game's)
                if (rp == null && mine == null && table.TryGetValue(costume, out var cps) && cps.TryGetValue(p, out var cp))
                    rp = DrawPart(null, plan.GameDir, paks, cp.Mdf, cp.Mesh);
                if (rp != null) parts.Add(rp);
            }
            // Hq on the graphics card (CPU if there is none), strong shadows with the key light from the right,
            // the character's camera from Blender; a figure it cuts off (other proportions) gets automatic framing
            var opt = new RenderOptions { Hq = true, Gpu = true, Dark = true };
            if (OutfitRender.Camera(poseKey, out var cam, out var fovY)) { opt.Camera = cam; opt.FovX = fovY; }
            var rgba = OutfitRender.Render(parts, pose, 1024, 1024, opt);
            // a figure the camera cuts off (a tall hat, other proportions): the same camera, a little further back, until it fits.
            // (It used to switch to automatic framing, whose angle is the old one: that picture looked turned to the right.)
            for (int k = 0; k < 4 && opt.Camera != null && OutfitRender.TouchesBorder(rgba, 1024, 1024); k++)
            {
                opt.FovX *= 1.12;
                rgba = OutfitRender.Render(parts, pose, 1024, 1024, opt);
            }
            return rgba;
        }

        /// <summary>Picture of one outfit of a pack, drawn in a pose (cached per outfit and pose); null if it can't be drawn.</summary>
        public static string DrawVariant(Plan plan, PlanVariant pv, int pose)
        {
            try
            {
                if (plan.DrawReports == null || pv?.Owner == null) return null;
                var kv = plan.DrawReports.FirstOrDefault(r => r.Key.Outfits.Contains(pv.Owner));
                if (kv.Key == null) return null;
                var body = kv.Value.Outfits.FirstOrDefault(o => o.Character == pv.Owner.Character && o.Part == "body" && o.Variants != null);
                var bv = body?.Variants.FirstOrDefault(v => v.Key == pv.Key);
                if (bv == null) return null;
                lock (DrawLock)
                {
                    if (plan.DrawDir == null) plan.DrawDir = TempDir("draw");
                    string safe = Regex.Replace(pv.Key ?? "v", "[^a-z0-9_]", "_", RegexOptions.IgnoreCase).ToLowerInvariant();
                    string file = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + "_v_" + safe + "_pose" + pose + ".png");
                    if (File.Exists(file)) return file;
                    var paks = Converter.Paks(plan.GameDir);
                    var rgba = plan.Game == "re3" ? RenderBodyRe3(plan, paks, plan.DrawReports, kv, body, bv, pose) : RenderBody(plan, paks, plan.DrawReports, kv, body, bv, pose);
                    if (rgba == null) return null;
                    OutfitRender.SavePng(rgba, 1024, 1024, file);
                    return file;
                }
            }
            catch { return null; } // this outfit can't be drawn (RT-only files, unknown format): automatic falls back to the mod's picture
        }

        private static void DrawPictures(Plan plan, List<KeyValuePair<PlanOption, ConvertReport>> reports)
        {
            var paks = Converter.Paks(plan.GameDir);
            foreach (var kv in reports)
                foreach (var body in kv.Value.Outfits.Where(o => o.Part == "body" && o.Variants != null))
                {
                    string poseKey = OutfitRender.PoseKey(body.Character, plan.Pose) ?? body.Character;
                    var pose = OutfitRender.Pose(poseKey);
                    var po = kv.Key.Outfits.FirstOrDefault(x => x.Character == body.Character && x.Part == "body");
                    if (pose == null || po == null) continue;
                    if (plan.DrawDir != null)
                    {
                        string done = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + "_pose" + plan.Pose + ".png");
                        if (File.Exists(done)) { po.Drawn = done; continue; } // drawn before in this pose
                    }
                    try
                    {
                        byte[] rgba;
                        lock (DrawLock) rgba = RenderBody(plan, paks, reports, kv, body, body.Variants[body.Default], plan.Pose);
                        if (rgba == null) continue;
                        if (plan.DrawDir == null) plan.DrawDir = TempDir("draw");
                        string file = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + "_pose" + plan.Pose + ".png");
                        OutfitRender.SavePng(rgba, 1024, 1024, file);
                        po.Drawn = file;
                    }
                    catch { /* this outfit can't be drawn (RT-only files, unknown format): placeholder */ }
                }
        }

        // ------------------------------------------------------------------ drawn pictures (RE3)
        private static readonly Dictionary<string, string> Re3Versions = new Dictionary<string, string>
        {
            ["mesh"] = "1902042334", ["mdf2"] = "13", ["tex"] = "190820018",
        };

        /// <summary>Like GameFile, for Resident Evil 3 (natives/stm, non-RT versions).</summary>
        private static byte[] GameFileRe3(string root, string gameDir, GamePaks paks, string virt, string prefix = "")
        {
            string ext = virt.Substring(virt.LastIndexOf('.') + 1).ToLowerInvariant();
            if (!Re3Versions.TryGetValue(ext, out var ver)) return null;
            string rel = (prefix + virt + "." + ver).Replace('/', Path.DirectorySeparatorChar);
            if (root != null)
            {
                string f = Path.Combine(root, "natives", "stm", rel);
                if (File.Exists(f)) return File.ReadAllBytes(f);
                string dir = Path.GetDirectoryName(Path.Combine(root, "natives", "stm", (prefix + virt).Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, Path.GetFileName(prefix + virt) + ".*").Any())
                    throw new NotSupportedException("the mod has " + virt + " only for the RT build");
            }
            if (gameDir == null) return null;
            string loose = Path.Combine(gameDir, "natives", "stm", rel);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
            return paks?.Read("natives/stm/" + (prefix + virt + "." + ver).ToLowerInvariant());
        }

        private static RenderPart DrawPartRe3(string root, string gameDir, GamePaks paks, string mdf, string mesh, string baseRoot = null)
        {
            if (mdf == null || mesh == null) return null;
            var mdfB = GameFileRe3(root, null, null, mdf) ?? GameFileRe3(baseRoot, gameDir, paks, mdf);
            var meshB = GameFileRe3(root, null, null, mesh) ?? GameFileRe3(baseRoot, gameDir, paks, mesh);
            if (mdfB == null || meshB == null) return null;
            return new RenderPart
            {
                Mdf = mdfB, Mesh = meshB,
                Texture = t => GameFileRe3(root, null, null, t, "streaming/") ?? GameFileRe3(root, null, null, t)
                    ?? GameFileRe3(baseRoot, null, null, t, "streaming/") ?? GameFileRe3(baseRoot, null, null, t)
                    ?? GameFileRe3(null, gameDir, paks, t, "streaming/") ?? GameFileRe3(null, gameDir, paks, t),
            };
        }

        /// <summary>RE3: draws every body of the mod (characters with a thumbnail pose: Jill) with the face and hair it is worn
        /// with in game: the same mod's, else the game's for the costume it is shown in. Opaque, on black, like the game's pictures.</summary>
        /// <summary>RE3: one body variant (Jill's pose) with the face and hair it is worn with; RGBA 1024², or null.</summary>
        private static byte[] RenderBodyRe3(Plan plan, GamePaks paks, List<KeyValuePair<PlanOption, ConvertReport>> reports,
            KeyValuePair<PlanOption, ConvertReport> kv, OutfitInfo body, Variant bv, int poseIndex)
        {
            string poseKey = OutfitRender.PoseKey(body.Character, poseIndex) ?? body.Character;
            var pose = OutfitRender.Pose(poseKey);
            var model = Re3.Models.FirstOrDefault(m => m.Id == body.Character);
            if (pose == null || model == null) return null;
            int costume = bv.Costumes.Count > 0 ? bv.Costumes[0] : 0;
            var first = DrawPartRe3(kv.Key.Root, plan.GameDir, paks, bv.Replaces, bv.SrcMesh, kv.Key.BaseRoot);
            if (first == null) return null;
            var parts = new List<RenderPart> { first };
            foreach (var p in new[] { "face", "hair" })
            {
                // RE3: one face per model, the same with every costume (in game, the faces a pack's prefabs give S.T.A.R.S. / Classic
                // don't show: JILL ALL STARS wears its default face in all three, and those other faces have their material
                // names swapped, drawn with the hair texture); the hair does change with the costume (the S.T.A.R.S. beret)
                int pc = p == "face" ? 0 : costume;
                // the same rule as the script in game: the mod's face/hair if it covers this costume (one with a single version is
                // worn with any costume; one with several, only with the costumes of its versions), else what the mod's own
                // prefabs give this costume, else the game's
                var mine = reports.OrderBy(r => r.Key == kv.Key ? 0 : 1)
                    .SelectMany(r => r.Value.Outfits.Where(o => o.Character == body.Character && o.Part == p && o.Variants != null && o.Variants.Count > 0)
                        .Select(o => new { r.Key.Root, r.Key.BaseRoot, O = o, V = o.Variants.Count == 1 ? o.Variants[0] : o.Variants.FirstOrDefault(x => x.Costumes.Contains(pc)) }))
                    .FirstOrDefault(x => x.V != null);
                RenderPart rp = null;
                if (mine != null)
                {
                    var v = mine.V;
                    rp = DrawPartRe3(mine.Root, plan.GameDir, paks, v.Replaces, v.SrcMesh, mine.BaseRoot);
                }
                else if (model.Table.TryGetValue(pc, out var pcs) && pcs.TryGetValue(p, out var cp))
                    rp = DrawPartRe3(null, plan.GameDir, paks, cp.Mdf, cp.Mesh);
                if (rp != null) parts.Add(rp);
            }
            // the same render as RE2 (graphics card, shadows, key light from the right, the pose's camera from Blender), with
            // RE3's files and its picture style (opaque figure with a faint glow: the RE3 menu ignores transparency)
            var opt = new RenderOptions { Re3 = true, Hq = true, Gpu = true, Dark = true, Frame = 0.62, Fill = 0.86 };
            if (OutfitRender.Camera(poseKey, out var cam, out var fovY)) { opt.Camera = cam; opt.FovX = fovY; }
            var rgba = OutfitRender.Render(parts, pose, 1024, 1024, opt);
            for (int k = 0; k < 4 && opt.Camera != null && OutfitRender.TouchesBorder(rgba, 1024, 1024); k++)
            {
                opt.FovX *= 1.12; // cut off by the camera: the same angle, a little further back
                rgba = OutfitRender.Render(parts, pose, 1024, 1024, opt);
            }
            return rgba;
        }

        private static void DrawPicturesRe3(Plan plan, List<KeyValuePair<PlanOption, ConvertReport>> reports)
        {
            var paks = Converter.Paks(plan.GameDir);
            foreach (var kv in reports)
                foreach (var body in kv.Value.Outfits.Where(o => o.Part == "body" && o.Variants != null))
                {
                    var pose = OutfitRender.Pose(body.Character);
                    var po = kv.Key.Outfits.FirstOrDefault(x => x.Character == body.Character && x.Part == "body");
                    var model = Re3.Models.FirstOrDefault(m => m.Id == body.Character);
                    if (pose == null || po == null || model == null) { if (Environment.GetEnvironmentVariable("CO_DEBUG") != null) plan.Warnings.Add("picture skipped: pose " + (pose != null) + ", plan outfit " + (po != null) + ", model " + (model != null)); continue; }
                    if (plan.DrawDir != null)
                    {
                        string done = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + "_pose" + plan.Pose + ".png");
                        if (File.Exists(done)) { po.Drawn = done; continue; } // drawn before in this pose
                    }
                    try
                    {
                        byte[] rgba;
                        lock (DrawLock) rgba = RenderBodyRe3(plan, paks, reports, kv, body, body.Variants[body.Default], plan.Pose);
                        if (rgba == null) { if (Environment.GetEnvironmentVariable("CO_DEBUG") != null) plan.Warnings.Add("picture skipped: body files not found"); continue; }
                        if (plan.DrawDir == null) plan.DrawDir = TempDir("draw");
                        string file = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + "_pose" + plan.Pose + ".png");
                        OutfitRender.SavePng(rgba, 1024, 1024, file);
                        po.Drawn = file;
                    }
                    catch (Exception ex) { if (Environment.GetEnvironmentVariable("CO_DEBUG") != null) plan.Warnings.Add("picture: " + ex); /* else: this outfit can't be drawn: placeholder */ }
                }
        }

        private static ConvertReport ConvertOption(string game, string root, string outRoot, string slug, string name, ConvertOptions o) =>
            game == "re3" ? Re3.Convert(root, outRoot, slug, name, o) : Converter.Convert(root, outRoot, slug, name, o);

        // ------------------------------------------------------------------ build
        /// <summary>overwrite: writes the new file and deletes the original (no backup; the user already confirmed a warning).</summary>
        // labels of the story stages of a face/hair (dirt): not a look of its own, never an option to pick
        private static readonly Regex StageLabel = new Regex(@"^(clean|dirty|dirtier|very dirty|all stages|stage \d+)$", RegexOptions.IgnoreCase);

        /// <summary>Body: the outfits a game costume points to. Face/hair: its looks for different costumes (the Noir hair and the
        /// Elza Walker hair of a pack), when there are 2 or more; story stages (clean/dirty) are not options.</summary>
        // a costume the game's costume menu lists: an extra (outside the families), or a clean stage of a family
        private static bool InGameMenu(string group, int costume)
        {
            // RE3 (outfits grouped by menu character): Jill's menu has Default, S.T.A.R.S., Classic; Carlos's Default, Classic
            if (Re3Menu.TryGetValue(group ?? "", out var re3)) return Array.IndexOf(re3, costume) >= 0;
            var st = Costumes.StageOf(group ?? "", costume);
            if (st == null) return true;
            if (!Costumes.Stages.TryGetValue(group, out var byFam) || !byFam.TryGetValue(st.Item1, out var stages) || st.Item2 >= stages.Length) return true;
            return stages[st.Item2].Dirt == 0;
        }

        public static readonly Dictionary<string, int[]> Re3Menu = new Dictionary<string, int[]>
        {
            ["jill"] = new[] { 0, 3, 5 }, ["carlos"] = new[] { 0, 1 },
        };

        private static List<PlanVariant> PickableOf(OutfitInfo o)
        {
            if (o.Variants == null) return new List<PlanVariant>();
            // only what the game's costume menu offers: the dirty / injured stages (Claire 1, 2, 4; Jill 2) come with the campaign,
            // and the variants of Jill's other models in RE3 (key "...@jill_infected") go along with her own
            var direct = o.Variants.Where(v => v.Direct != null && v.Direct.Count > 0 && !(v.Key ?? "").Contains("@")
                && v.Direct.Any(c => InGameMenu(o.Group, c))).ToList();
            if (o.Part != "body")
            {
                direct = direct.Where(v => !StageLabel.IsMatch(v.Label ?? "")).ToList();
                if (direct.Count < 2) return new List<PlanVariant>();
            }
            return direct.Select(v => new PlanVariant { Key = v.Key, Slot = v.Label, Name = "", Costumes = v.Direct.ToList() }).ToList();
        }

        /// <summary>Name of a group of options in the Wardrobe menu when the user typed none: the mod name without " (Wardrobe)".</summary>
        public static string GroupDefault(Plan plan) => Regex.Replace(plan.PackageName ?? "", @"\s*\(Wardrobe\)\s*$", "").Trim();

        /// <summary>One warning for every outfit of the mod whose DLC-only textures were swapped for neutral ones (it repeated, the
        /// same, once per colour add-on).</summary>
        public static string DlcSwapWarning(List<(string Option, string Part, string Label, List<string> Textures)> swaps)
        {
            var sb = new StringBuilder();
            sb.Append("Some textures come from a DLC costume that isn't installed in your game, so they were replaced with neutral ones: these outfits work, only that small detail won't show. If you install that DLC later, convert the mod again to get them.");
            foreach (var g in swaps.GroupBy(s => s.Option))
                sb.Append("\n   – ").Append(g.Key).Append(": ").Append(string.Join(", ", g.Select(s => s.Part + " (" + s.Label + ")").Distinct()));
            sb.Append("\n   Textures replaced: ").Append(string.Join(", ", swaps.SelectMany(s => s.Textures).Distinct(StringComparer.OrdinalIgnoreCase)));
            return sb.ToString();
        }

        public static BuildResult Build(Plan plan, string outZip, bool overwrite)
        {
            // everything switched off in the window: nothing to convert
            if (plan.Options.SelectMany(o => o.Outfits).All(x => x.Disabled || x.Pickable.Count > 0 && x.Pickable.All(p => p.Disabled)))
                throw new IOException("every part of this mod is switched off");
            DrawMissingPictures(plan); // (the window may still be drawing it: waits for it)
            string staging = TempDir("build");
            try
            {
                string pkgName = SafeFileName(plan.PackageName);
                // staged under a short folder name ("p"): the package name is only given inside the .zip, so the
                // staging paths stay under Windows' 260-character limit
                string pkgDir = Path.Combine(staging, "p");
                Directory.CreateDirectory(pkgDir);
                var result = new BuildResult();
                var dlcSwaps = new List<(string Option, string Part, string Label, List<string> Textures)>();
                foreach (var opt in plan.Options)
                {
                    var names = new Dictionary<string, string>();
                    var variantNames = new Dictionary<string, string>();
                    var each = new HashSet<string>();
                    var pictures = new Dictionary<string, string>();
                    var picturesDrawn = new HashSet<string>();
                    var autoDrawn = new Dictionary<string, string>();
                    var disabled = new HashSet<string>();
                    foreach (var o in opt.Outfits)
                    {
                        // parts and versions switched off in the window: left out of the package
                        if (o.Disabled) disabled.Add(o.Character + "|" + o.Part);
                        foreach (var pv in o.Pickable) if (pv.Disabled) disabled.Add(o.Character + "|" + o.Part + "|" + pv.Key);
                        names[o.Character + "|" + o.Part] = (string.IsNullOrWhiteSpace(o.Name) ? opt.Name : o.Name).Trim();
                        foreach (var pv in o.Pickable) if (!string.IsNullOrWhiteSpace(pv.Name)) variantNames[o.Character + "|" + o.Part + "|" + pv.Key] = pv.Name.Trim();
                        if (o.EachEntry)
                        {
                            each.Add(o.Character + "|" + o.Part);
                            foreach (var pv in o.Pickable)
                            {
                                string key = o.Character + "|" + o.Part + "|" + pv.Key;
                                if (pv.Picture != null) { pictures[key] = pv.Picture; if (pv.PictureDrawn) picturesDrawn.Add(key); continue; }
                                // automatic: the mod's own picture of that costume (Convert), else one drawn from this outfit
                                if (Converter.MenuPictureFile(pv.Root, o.Character, pv.Costumes) != null) continue;
                                if (pv.Drawn == null || !File.Exists(pv.Drawn)) pv.Drawn = DrawVariant(plan, pv, plan.Pose);
                                if (pv.Drawn != null) autoDrawn[key] = pv.Drawn;
                            }
                        }
                    }
                    var rep = ConvertOption(plan.Game, opt.Root, pkgDir, opt.Slug, opt.Name, new ConvertOptions { Names = names, VariantNames = variantNames, VariantPictures = pictures, VariantPicturesDrawn = picturesDrawn, VariantDrawn = autoDrawn, Disabled = disabled, EachEntry = each, PackageName = GroupDefault(plan), GroupNames = plan.GroupNames, GameDir = plan.GameDir, BaseRoot = opt.BaseRoot, BaseSlug = opt.BaseSlug, Package = ShortHash(Path.GetFileName(plan.Input.TrimEnd('\\', '/'))), Thumbnail = plan.CustomPicture ?? (plan.PictureChoice == "pose" ? null : plan.PictureChoice == "mod" ? ModPicture(plan, opt) : MenuShot(plan, opt)),
                        ThumbnailForced = plan.CustomPicture != null || plan.PictureChoice == "pose",
                        Drawn = plan.CustomPicture == null && plan.PictureChoice == "mod" ? null : opt.Outfits.Where(o => o.Drawn != null).GroupBy(o => o.Character).ToDictionary(g => g.Key, g => g.First().Drawn) });
                    result.Outfits += rep.Outfits.Count;
                    foreach (var w in rep.Warnings) result.Warnings.Add(opt.Name + ": " + w);
                    foreach (var d in rep.DlcSwaps) dlcSwaps.Add((opt.Name, d.Part, d.Label, d.Textures));
                }
                if (dlcSwaps.Count > 0) result.Warnings.Add(DlcSwapWarning(dlcSwaps));
                // modinfo.ini for Fluffy: screenshot/version/author from the mod's main option (in ReHauled, the
                // outfit; face and hair are "addonfor"), not simply the first folder in alphabetical order
                var first = MainOption(plan) ?? new PlanOption();
                var lines = new List<string>
                {
                    "name=" + plan.PackageName,
                    "version=" + (string.IsNullOrEmpty(first.Version) ? "1.0" : first.Version),
                    "description=Converted for Wardrobe: adds the outfits without replacing any game file. Original mod: " + plan.BaseName,
                    "author=" + (string.IsNullOrEmpty(first.Author) ? "unknown" : first.Author),
                    "category=" + FluffyCategory(plan, first),
                };
                if (first.Screenshot != null)
                {
                    string ext = Path.GetExtension(first.Screenshot);
                    File.Copy(first.Screenshot, Path.Combine(pkgDir, "screenshot" + ext), true);
                    lines.Add("screenshot=screenshot" + ext);
                }
                File.WriteAllText(Path.Combine(pkgDir, "modinfo.ini"), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

                // .zip with the package folder; the new one is written first and only then the original is deleted
                string tmpZip = Path.Combine(staging, "out.zip");
                using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
                    foreach (var f in Directory.EnumerateFiles(pkgDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                        zip.CreateEntryFromFile(f, pkgName + "/" + f.Substring(pkgDir.Length + 1).Replace('\\', '/'), CompressionLevel.Optimal);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outZip)));
                File.Copy(tmpZip, outZip, true);
                if (overwrite && !string.Equals(Path.GetFullPath(plan.Input), Path.GetFullPath(outZip), StringComparison.OrdinalIgnoreCase))
                    File.Delete(plan.Input);
                result.Output = outZip;
                return result;
            }
            finally { TryDelete(staging); }
        }

        // Fluffy's character categories for RE2R (exact names from Modmanager.exe). Without a valid "category="
        // Fluffy decides by the files and, since the package has reframework/, everything went to "!Other > RE Framework".
        private static readonly Dictionary<string, string> FluffyCategories = new Dictionary<string, string>
        {
            ["leon"] = "Leon", ["claire"] = "Claire", ["ada"] = "Ada", ["sherry"] = "Sherry",
            ["hunk"] = "Hunk", ["kendo"] = "Kendo", ["tofu"] = "Tofu",
            ["jill"] = "Jill", ["carlos"] = "Carlos", // RE3
        };

        /// <summary>Package category: character of the main option's body (else the one with most outfits).
        /// Characters without a Fluffy category (Katherine, Forgotten Soldier...) go to "!Other > Models".</summary>
        public static string FluffyCategory(Plan plan, PlanOption main)
        {
            string ch = main?.Outfits.FirstOrDefault(o => o.Part == "body")?.Character
                ?? plan.Options.SelectMany(o => o.Outfits).GroupBy(o => o.Character)
                       .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            return ch != null && FluffyCategories.TryGetValue(ch, out var c) ? "!Characters > " + c : "!Other > Models";
        }

        private static readonly Dictionary<string, bool> FitsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The mod's image is made for the costume menu: it has a transparent background, like the game's own pictures.
        /// Other images (a banner with the mod's name, a screenshot of a scene) end up cropped in the menu's frame, so the
        /// picture is drawn from the outfit instead.</summary>
        public static bool FitsMenu(string img)
        {
            if (img == null || !File.Exists(img)) return false;
            lock (FitsCache)
                if (FitsCache.TryGetValue(img, out var known)) return known;
            bool fits = false;
            try
            {
                using (var bmp = new System.Drawing.Bitmap(img))
                    if (System.Drawing.Image.IsAlphaPixelFormat(bmp.PixelFormat))
                        for (int y = 0; y < bmp.Height && !fits; y += Math.Max(1, bmp.Height / 64))
                            for (int x = 0; x < bmp.Width && !fits; x += Math.Max(1, bmp.Width / 64))
                                if (bmp.GetPixel(x, y).A < 250) fits = true;
            }
            catch { fits = false; }
            lock (FitsCache) FitsCache[img] = fits;
            return fits;
        }

        /// <summary>The image of an option for the costume menu: its screenshot (or the mod's) when it is made for the menu;
        /// otherwise null, so the picture drawn from the outfit is used (or still the screenshot, when nothing was drawn).</summary>
        public static string MenuShot(Plan plan, PlanOption opt)
        {
            string main = MainOption(plan)?.Screenshot;
            if (FitsMenu(opt.Screenshot)) return opt.Screenshot;
            if (FitsMenu(main)) return main;
            bool drawn = plan.Options.Any(o => o.Outfits.Any(x => x.Drawn != null && File.Exists(x.Drawn)));
            return drawn ? null : opt.Screenshot ?? main;
        }

        /// <summary>The mod's own image for an option (when the user picks it, even if it isn't made for the menu).</summary>
        public static string ModPicture(Plan plan, PlanOption opt) => MenuShot(plan, opt) ?? opt.Screenshot ?? MainOption(plan)?.Screenshot;

        /// <summary>Option that represents the mod: main (no addonfor) with body and screenshot, then
        /// main with screenshot, then any with body and screenshot, then the first.</summary>
        public static PlanOption MainOption(Plan plan)
        {
            bool HasBody(PlanOption o) => o.Outfits.Any(x => x.Part == "body");
            return plan.Options.FirstOrDefault(o => !o.IsAddon && HasBody(o) && o.Screenshot != null)
                ?? plan.Options.FirstOrDefault(o => !o.IsAddon && o.Screenshot != null)
                ?? plan.Options.FirstOrDefault(o => HasBody(o) && o.Screenshot != null)
                ?? plan.Options.FirstOrDefault(o => o.Screenshot != null)
                ?? plan.Options.FirstOrDefault();
        }

        /// <summary>A mod read again (add-ons given or removed in the window): what the user had set on it stays, by option name,
        /// part and version key (names, pictures, one entry per outfit, parts and versions switched off).</summary>
        public static void CarryOver(Plan from, Plan to)
        {
            to.PackageName = from.PackageName;
            to.CustomPicture = from.CustomPicture;
            to.PictureChoice = from.PictureChoice;
            to.Pose = from.Pose;
            foreach (var kv in from.GroupNames) to.GroupNames[kv.Key] = kv.Value;
            foreach (var opt in to.Options)
            {
                var old = from.Options.FirstOrDefault(o => o.Name == opt.Name);
                if (old == null) continue;
                foreach (var o in opt.Outfits)
                {
                    var oo = old.Outfits.FirstOrDefault(x => x.Character == o.Character && x.Part == o.Part);
                    if (oo == null) continue;
                    o.Name = oo.Name; o.EachEntry = oo.EachEntry; o.Disabled = oo.Disabled;
                    foreach (var pv in o.Pickable)
                    {
                        var op = oo.Pickable.FirstOrDefault(x => x.Key == pv.Key);
                        if (op == null) continue;
                        pv.Name = op.Name; pv.Disabled = op.Disabled;
                        if (op.Picture != null && !op.PictureDrawn) pv.Picture = op.Picture; // (drawn ones were in the old plan's folder)
                    }
                }
            }
        }

        public static void Cleanup(Plan plan)
        {
            if (plan?.Extracted != null && plan.Extracted.Contains("CustomOutfitsConverter")) TryDelete(plan.Extracted);
            if (plan?.DrawDir != null && plan.DrawDir.Contains("CustomOutfitsConverter")) TryDelete(plan.DrawDir);
            foreach (var d in plan?.AddonDirs ?? new List<string>()) if (d.Contains("CustomOutfitsConverter")) TryDelete(d);
        }

        private static void TryDelete(string dir)
        {
            if (dir == null) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* file in use: stays in the temp folder */ }
        }
    }
}
