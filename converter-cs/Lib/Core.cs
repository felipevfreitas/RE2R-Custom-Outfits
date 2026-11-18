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
    public sealed class PlanOutfit
    {
        public string Character, CharacterName, Part, PartName, Name;
        public List<string> Variants = new List<string>();
        public List<string> Also = new List<string>();
        public string Drawn; // body: picture drawn from the outfit (PNG), used when the mod has no image
    }

    public sealed class PlanOption
    {
        public string Root, Name, Slug, Author, Version, Screenshot;
        public bool IsAddon; // modinfo.ini with "addonfor=": add-on (face/hair) of another option of the mod
        public List<PlanOutfit> Outfits = new List<PlanOutfit>();
        public List<string> Warnings = new List<string>();
    }

    public sealed class Plan
    {
        public string Input, Extracted, Game = "re2", GameDir, BaseName, PackageName;
        public bool IsArchive;
        public string CustomPicture;   // image the user chose for the game costume menu (wins over the mod's)
        public bool HasMenuPicture;    // the mod brings its own costume-menu picture (ui0601_*_iam.tex)
        public string MenuPictureFile; // one of them (preview in the window)
        public string DrawDir;         // temporary folder of the drawn pictures
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
        public static readonly Dictionary<string, string> GameNames = new Dictionary<string, string> { ["re2"] = "Resident Evil 2", ["re3"] = "Resident Evil 3" };
        private static readonly Dictionary<string, string> GameFolders = new Dictionary<string, string>
        {
            ["re2"] = "RESIDENT EVIL 2  BIOHAZARD RE2", ["re3"] = "RE3",
        };

        public static bool IsGameDir(string d, string game = "re2") =>
            !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, game + ".exe")) && File.Exists(Path.Combine(d, "re_chunk_000.pak"));

        /// <summary>Which game a mod is for: RE3 keeps its characters under natives/*/escape/.</summary>
        public static string GameOfMod(string modRoot) => Re3.IsRe3Mod(modRoot) ? "re3" : "re2";

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

        // target of an archive entry, or null to skip (folder, empty name, or outside root = "zip slip")
        private static string SafeTarget(string root, string key, bool isDir)
        {
            if (isDir || string.IsNullOrEmpty(key)) return null;
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

        // ------------------------------------------------------------------ analyze
        /// <summary>re2Dir: the RE2 folder; the RE3 folder is detected through Steam.</summary>
        public static Plan Analyze(string input, string re2Dir) => Analyze(input, g => g == "re2" ? re2Dir : DetectGameDir(g));

        /// <summary>gameDirOf("re2"/"re3") gives the game folder (null when unknown).</summary>
        public static Plan Analyze(string input, Func<string, string> gameDirOf)
        {
            if (!File.Exists(input) && !Directory.Exists(input)) throw new IOException("File or folder not found: " + input);
            bool isArchive = File.Exists(input);
            if (isArchive && !Archive.IsMatch(input)) throw new IOException("Unsupported file. Use .zip, .rar, .7z or a folder.");
            string workDir = input, extracted = null;
            if (isArchive)
            {
                extracted = TempDir("extract");
                try { Extract(input, extracted); }
                catch (Exception e) { TryDelete(extracted); throw new IOException("Could not open the archive: " + e.Message); }
                workDir = extracted;
            }
            var roots = new List<string>();
            FindModRoots(workDir, 0, roots);
            if (roots.Count == 0) { TryDelete(extracted); throw new IOException("No mod found: there is no 'natives' folder inside."); }
            string game = roots.Any(r => GameOfMod(r) == "re3") ? "re3" : "re2";
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
            var usedSlugs = new HashSet<string>();
            var reports = new List<KeyValuePair<PlanOption, ConvertReport>>();
            foreach (var root in roots)
            {
                var info = ReadModinfo(root);
                string optName = CleanName(info.TryGetValue("name", out var mn) && mn.Length > 0 ? mn : roots.Count == 1 ? baseName : Path.GetFileName(root));
                string slugBase = Slugify(CharWords.Replace(roots.Count == 1 ? baseName : optName, " "));
                if (game == "re3" && slugBase.Length > 24) slugBase = slugBase.Substring(0, 24).TrimEnd('_'); // RE3 paths are deeper
                string slug = slugBase + "_" + ShortHash(Path.GetFileName(input.TrimEnd('\\', '/')) + "|" + optName);
                while (usedSlugs.Contains(slug)) slug += "_2";
                usedSlugs.Add(slug);
                ConvertReport rep;
                try { rep = ConvertOption(game, root, null, slug, optName, new ConvertOptions { DryRun = true, GameDir = gameDir }); }
                catch (Exception e) { plan.Warnings.Add(optName + ": " + e.Message); continue; }
                string shot = info.TryGetValue("screenshot", out var sc) && sc.Length > 0 && File.Exists(Path.Combine(root, sc)) ? Path.Combine(root, sc) : null;
                var opt = new PlanOption
                {
                    Root = root, Name = optName, Slug = slug, Screenshot = shot,
                    Author = info.TryGetValue("author", out var au) ? au : "",
                    Version = info.TryGetValue("version", out var ve) ? ve : "",
                    IsAddon = info.ContainsKey("addonfor"),
                    Warnings = rep.Warnings,
                };
                foreach (var o in rep.Outfits)
                {
                    opt.Outfits.Add(new PlanOutfit
                    {
                        Character = o.Character, CharacterName = CharNames.TryGetValue(o.Character, out var cn) ? cn : o.Character,
                        Part = o.Part, PartName = PartNames.TryGetValue(o.Part, out var pn) ? pn : o.Part,
                        Name = OutfitName(optName, o.Part),
                        Variants = o.VariantLabels,
                        Also = o.Also.Select(c => CharNames.TryGetValue(c, out var an) ? an : c).ToList(),
                    });
                }
                if (opt.Outfits.Count == 0) plan.Warnings.Add(optName + ": no character outfit found (only other files), skipped.");
                else { plan.Options.Add(opt); reports.Add(new KeyValuePair<PlanOption, ConvertReport>(opt, rep)); }
                foreach (var w in rep.Warnings) plan.Warnings.Add(optName + ": " + w);
            }
            plan.MenuPictureFile = roots.SelectMany(r => Directory.EnumerateFiles(r, "ui0601_*_iam.tex.*", SearchOption.AllDirectories))
                .OrderBy(f => f.EndsWith(".10") ? 0 : 1).FirstOrDefault();
            plan.HasMenuPicture = plan.MenuPictureFile != null;
            // no image at all in the mod: the picture of each body is drawn from the outfit itself
            if (game == "re2" && !plan.HasMenuPicture && plan.Options.All(o => o.Screenshot == null))
            {
                try { DrawPictures(plan, reports); }
                catch { /* no picture: the game shows a placeholder */ }
            }
            if (plan.Options.Count == 0)
            {
                TryDelete(extracted);
                throw new IOException("This mod has no character outfits that can be converted.\n" + string.Join("\n", plan.Warnings));
            }
            return plan;
        }

        // ------------------------------------------------------------------ drawn pictures
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

        private static RenderPart DrawPart(string root, string gameDir, GamePaks paks, string mdf, string mesh)
        {
            if (mdf == null || mesh == null) return null;
            var mdfB = GameFile(root, gameDir, paks, mdf);
            var meshB = GameFile(root, gameDir, paks, mesh);
            if (mdfB == null || meshB == null || Mdf2.IsRT(mdfB)) return null;
            // textures: the streaming copy (full size) first; the mod's, then the game's
            return new RenderPart
            {
                Mdf = mdfB, Mesh = meshB,
                Texture = t => GameFile(root, null, null, t, "streaming/") ?? GameFile(root, null, null, t)
                    ?? GameFile(null, gameDir, paks, t, "streaming/") ?? GameFile(null, gameDir, paks, t),
            };
        }

        /// <summary>Draws every body of the mod (characters with a thumbnail pose) with the face and hair it is worn
        /// with in game: the same mod's (same option first), else the game's for the costume it is shown in.</summary>
        private static void DrawPictures(Plan plan, List<KeyValuePair<PlanOption, ConvertReport>> reports)
        {
            var paks = Converter.Paks(plan.GameDir);
            foreach (var kv in reports)
                foreach (var body in kv.Value.Outfits.Where(o => o.Part == "body" && o.Variants != null))
                {
                    var pose = OutfitRender.Pose(body.Character);
                    var po = kv.Key.Outfits.FirstOrDefault(x => x.Character == body.Character && x.Part == "body");
                    if (pose == null || po == null) continue;
                    try
                    {
                        var bv = body.Variants[body.Default];
                        int costume = bv.Costumes.Count > 0 ? bv.Costumes[0] : 0;
                        var first = DrawPart(kv.Key.Root, plan.GameDir, paks, bv.Replaces, bv.SrcMesh);
                        if (first == null) continue;
                        var parts = new List<RenderPart> { first };
                        var table = Costumes.Table(paks, plan.GameDir, body.Group);
                        foreach (var p in new[] { "face", "hair" })
                        {
                            var mine = reports.OrderBy(r => r.Key == kv.Key ? 0 : 1)
                                .SelectMany(r => r.Value.Outfits.Where(o => o.Character == body.Character && o.Part == p && o.Variants != null)
                                    .Select(o => new { r.Key.Root, O = o }))
                                .FirstOrDefault();
                            RenderPart rp = null;
                            if (mine != null)
                            {
                                var v = mine.O.Variants.FirstOrDefault(x => x.Costumes.Contains(costume)) ?? mine.O.Variants[mine.O.Default];
                                rp = DrawPart(mine.Root, plan.GameDir, paks, v.Replaces, v.SrcMesh);
                            }
                            else if (table.TryGetValue(costume, out var cps) && cps.TryGetValue(p, out var cp))
                                rp = DrawPart(null, plan.GameDir, paks, cp.Mdf, cp.Mesh);
                            if (rp != null) parts.Add(rp);
                        }
                        var rgba = OutfitRender.Render(parts, pose, 1024, 1024);
                        if (rgba == null) continue;
                        if (plan.DrawDir == null) plan.DrawDir = TempDir("draw");
                        string file = Path.Combine(plan.DrawDir, kv.Key.Slug + "_" + body.Character + ".png");
                        OutfitRender.SavePng(rgba, 1024, 1024, file);
                        po.Drawn = file;
                    }
                    catch { /* this outfit can't be drawn (RT-only files, unknown format): placeholder */ }
                }
        }

        private static ConvertReport ConvertOption(string game, string root, string outRoot, string slug, string name, ConvertOptions o) =>
            game == "re3" ? Re3.Convert(root, outRoot, slug, name, o) : Converter.Convert(root, outRoot, slug, name, o);

        // ------------------------------------------------------------------ build
        /// <summary>overwrite: writes the new file and deletes the original (no backup; the user already confirmed a warning).</summary>
        public static BuildResult Build(Plan plan, string outZip, bool overwrite)
        {
            string staging = TempDir("build");
            try
            {
                string pkgName = SafeFileName(plan.PackageName);
                // staged under a short folder name ("p"): the package name is only given inside the .zip, so the
                // staging paths stay under Windows' 260-character limit
                string pkgDir = Path.Combine(staging, "p");
                Directory.CreateDirectory(pkgDir);
                var result = new BuildResult();
                foreach (var opt in plan.Options)
                {
                    var names = new Dictionary<string, string>();
                    foreach (var o in opt.Outfits) names[o.Character + "|" + o.Part] = (string.IsNullOrWhiteSpace(o.Name) ? opt.Name : o.Name).Trim();
                    var rep = ConvertOption(plan.Game, opt.Root, pkgDir, opt.Slug, opt.Name, new ConvertOptions { Names = names, GameDir = plan.GameDir, Package = ShortHash(Path.GetFileName(plan.Input.TrimEnd('\\', '/'))), Thumbnail = plan.CustomPicture ?? opt.Screenshot ?? MainOption(plan)?.Screenshot, ThumbnailForced = plan.CustomPicture != null,
                        Drawn = opt.Outfits.Where(o => o.Drawn != null).GroupBy(o => o.Character).ToDictionary(g => g.Key, g => g.First().Drawn) });
                    result.Outfits += rep.Outfits.Count;
                    foreach (var w in rep.Warnings) result.Warnings.Add(opt.Name + ": " + w);
                }
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

        public static void Cleanup(Plan plan)
        {
            if (plan?.Extracted != null && plan.Extracted.Contains("CustomOutfitsConverter")) TryDelete(plan.Extracted);
            if (plan?.DrawDir != null && plan.DrawDir.Contains("CustomOutfitsConverter")) TryDelete(plan.DrawDir);
        }

        private static void TryDelete(string dir)
        {
            if (dir == null) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* file in use: stays in the temp folder */ }
        }
    }
}
