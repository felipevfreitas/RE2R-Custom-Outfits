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
        public string Input, Extracted, GameDir, BaseName, PackageName;
        public bool IsArchive;
        public List<PlanOption> Options = new List<PlanOption>();
        public List<string> Warnings = new List<string>();
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
            ["claire"] = "Claire", ["leon"] = "Leon", ["ada"] = "Ada", ["sherry"] = "Sherry", ["hunk"] = "Hunk", ["tofu"] = "Tofu",
            ["kendo"] = "Robert Kendo", ["katherine"] = "Katherine (Runaway)", ["ghost"] = "Forgotten Soldier",
        };
        public static readonly Dictionary<string, string> PartNames = new Dictionary<string, string>
        {
            ["body"] = "Body", ["face"] = "Face", ["hair"] = "Hair",
        };
        private static readonly Regex Archive = new Regex(@"\.(zip|rar|7z)$", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------------ game folder
        private const string GameFolder = "RESIDENT EVIL 2  BIOHAZARD RE2";

        public static bool IsGameDir(string d) =>
            !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "re2.exe")) && File.Exists(Path.Combine(d, "re_chunk_000.pak"));

        /// <summary>Default Steam path or any Steam library (libraryfolders.vdf).</summary>
        public static string DetectGameDir()
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
                string d = Path.Combine(lib, "steamapps", "common", GameFolder);
                if (IsGameDir(d)) return d;
            }
            return null;
        }

        // ------------------------------------------------------------------ utilities
        private static string TempDir(string prefix)
        {
            string d = Path.Combine(Path.GetTempPath(), "CustomOutfitsConverter", prefix + "_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(d);
            return d;
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
        private static readonly Regex CharWords = new Regex(@"\b(claire( redfield)?|leon( s\.?)?( kennedy)?|ada( wong)?|sherry( birkin)?)\b", RegexOptions.IgnoreCase);
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
            return n.Length > 0 ? n : "Custom Outfits";
        }

        // ------------------------------------------------------------------ analyze
        public static Plan Analyze(string input, string gameDir)
        {
            if (!IsGameDir(gameDir)) throw new IOException("Game folder not found. Choose the Resident Evil 2 folder (the one with re2.exe).");
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

            string baseName = CleanName(Path.GetFileName(input.TrimEnd('\\', '/')));
            var plan = new Plan
            {
                Input = input, IsArchive = isArchive, Extracted = extracted, GameDir = gameDir, BaseName = baseName,
                PackageName = baseName + " (Custom Outfits)",
            };
            var usedSlugs = new HashSet<string>();
            foreach (var root in roots)
            {
                var info = ReadModinfo(root);
                string optName = CleanName(info.TryGetValue("name", out var mn) && mn.Length > 0 ? mn : roots.Count == 1 ? baseName : Path.GetFileName(root));
                string slug = Slugify(CharWords.Replace(roots.Count == 1 ? baseName : optName, " ")) + "_" + ShortHash(Path.GetFileName(input.TrimEnd('\\', '/')) + "|" + optName);
                while (usedSlugs.Contains(slug)) slug += "_2";
                usedSlugs.Add(slug);
                ConvertReport rep;
                try { rep = Converter.Convert(root, null, slug, optName, new ConvertOptions { DryRun = true, GameDir = gameDir }); }
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
                else plan.Options.Add(opt);
                foreach (var w in rep.Warnings) plan.Warnings.Add(optName + ": " + w);
            }
            if (plan.Options.Count == 0)
            {
                TryDelete(extracted);
                throw new IOException("This mod has no character outfits that can be converted.\n" + string.Join("\n", plan.Warnings));
            }
            return plan;
        }

        // ------------------------------------------------------------------ build
        /// <summary>overwrite: writes the new file and deletes the original (no backup; the user already confirmed a warning).</summary>
        public static BuildResult Build(Plan plan, string outZip, bool overwrite)
        {
            string staging = TempDir("build");
            try
            {
                string pkgName = SafeFileName(plan.PackageName);
                string pkgDir = Path.Combine(staging, pkgName);
                Directory.CreateDirectory(pkgDir);
                var result = new BuildResult();
                foreach (var opt in plan.Options)
                {
                    var names = new Dictionary<string, string>();
                    foreach (var o in opt.Outfits) names[o.Character + "|" + o.Part] = (string.IsNullOrWhiteSpace(o.Name) ? opt.Name : o.Name).Trim();
                    var rep = Converter.Convert(opt.Root, pkgDir, opt.Slug, opt.Name, new ConvertOptions { Names = names, GameDir = plan.GameDir });
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
                    "description=Converted for the Custom Outfit System: adds the outfits without replacing any game file. Original mod: " + plan.BaseName,
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
                ZipFile.CreateFromDirectory(pkgDir, tmpZip, CompressionLevel.Optimal, true);
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
        }

        private static void TryDelete(string dir)
        {
            if (dir == null) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* file in use: stays in the temp folder */ }
        }
    }
}
