// MainWindow.xaml.cs - converter UI. Accepts several mods at once; each one becomes its own package.
// The heavy work (opening the mod, reading the pak, creating the zip) runs in Task.Run so the window doesn't freeze.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CustomOutfits
{
    public sealed class MetaItem
    {
        public string Text { get; set; }
        public bool IsPill { get; set; }
    }

    /// <summary>One outfit of a pack (a body with several outfits): the game costume it replaces, its name and picture.</summary>
    public sealed class PickVM : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private readonly RowVM _row;
        public PlanVariant V { get; }
        public PickVM(PlanVariant v, RowVM row) { V = v; _row = row; }
        public string Slot => V.Slot;
        public string Name { get => V.Name; set => V.Name = value; }
        // grey text (and the name used) while the box is empty: a body's entry in the game menu is "<body> - <costume>";
        // a face/hair option is listed under its entry, by the costume alone
        public string Placeholder => _row.IsBody ? _row.Name + " - " + V.Slot : V.Slot;
        public bool PictureOn => _row.IsBody && _row.EachEntry;
        /// <summary>Switched on: in the converted mod. Off: left out (greyed in the window).</summary>
        public bool Enabled { get => !V.Disabled; set { V.Disabled = !value; Refresh(); _row.Changed(); } }
        public string EnabledTip => _row.IsBody ? "Include this outfit in the converted mod" : "Include this version in the converted mod";
        public bool HasPicture => V.Picture != null;
        public bool Drawing;          // its automatic picture is being drawn (spinner on the card)
        public bool NoPreview => Preview == null && !Drawing;
        public Visibility DrawingVisibility => Drawing ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The costume-menu picture the mod brings for this costume (.tex), or null.</summary>
        public string OwnPictureFile
        {
            get
            {
                if (!_ownDone) { _ownDone = true; _own = Converter.MenuPictureFile(V.Root, V.Owner?.Character, V.Costumes); }
                return _own;
            }
        }
        private string _own;
        private bool _ownDone;
        /// <summary>The automatic picture still has to be drawn (the mod has none for this costume).</summary>
        public bool NeedsDrawing => V.Picture == null && OwnPictureFile == null && (V.Drawn == null || !File.Exists(V.Drawn));

        public string PictureTip => V.Picture != null
            ? (V.PictureDrawn ? "Drawn from this outfit." : "Your image: " + Path.GetFileName(V.Picture)) + "\nClick to choose another picture."
            : OwnPictureFile != null ? "Automatic: the mod's own picture of this costume.\nClick to choose another picture."
            : "Automatic: drawn from this outfit.\nClick to choose a pose or an image of yours.";

        private ImageSource _preview;
        private string _previewOf;
        /// <summary>The picture of its entry: the one chosen, else the mod's picture of this costume, else the one drawn.</summary>
        public ImageSource Preview
        {
            get
            {
                string src = V.Picture ?? OwnPictureFile ?? V.Drawn;
                if (_previewOf == src) return _preview;
                _previewOf = src;
                _preview = src == null || !File.Exists(src) ? null
                    : System.Text.RegularExpressions.Regex.IsMatch(src, @"\.tex\.\d+$") ? ModVM.TexImage(src) : ModVM.LoadImage(src);
                return _preview;
            }
        }
        public void SetPicture(string file, bool drawn = false) { V.Picture = file; V.PictureDrawn = file != null && drawn; Refresh(); }
        public void Refresh() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
    }

    /// <summary>One option of a part that a package brings several times (the colours of a hair mod).</summary>
    public sealed class OptionVM : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private readonly PlanOutfit _o;
        public OptionVM(PlanOutfit o, string from) { _o = o; From = from; }
        public PlanOutfit Outfit => _o;
        public bool FromAddon { get; set; } // from an add-on given in the window: named (and switched) in the Add-ons tab
        public string From { get; }
        public string Name { get => _o.Name; set => _o.Name = value; }
        public bool Enabled
        {
            get => !_o.Disabled;
            set { _o.Disabled = !value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null)); }
        }
    }

    public sealed class RowVM : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private readonly PlanOutfit _o;

        // several outfits of the same part in one package: one row, with each outfit as an option
        // the name box of this row is the name of the group in the Wardrobe menu (the options are listed under it)
        private Plan _plan;
        private string _groupKey;
        private bool _hybrid; // the mod's own outfit row (pack, pictures) that add-ons from the window give more options

        /// <summary>The mod's own row of a part that add-ons given in the window add options to: it keeps its own look (the outfits
        /// of a pack, their pictures); its name box becomes the name of the group, and the options are listed (add-ons': a note).</summary>
        public void AttachGroup(List<KeyValuePair<PlanOutfit, string>> options, Plan plan)
        {
            _plan = plan;
            _hybrid = true;
            _groupKey = _o.Character + "|" + _o.Part;
            if (!plan.GroupNames.ContainsKey(_groupKey)) plan.GroupNames[_groupKey] = Core.GroupDefault(plan);
            foreach (var kv in options) Options.Add(new OptionVM(kv.Key, kv.Value) { FromAddon = plan.Options.Any(op => op.AddonInput != null && op.Outfits.Contains(kv.Key)) });
        }
        public RowVM(List<KeyValuePair<PlanOutfit, string>> options, Plan plan)
        {
            _o = options[0].Key;
            _plan = plan;
            _groupKey = _o.Character + "|" + _o.Part;
            if (!plan.GroupNames.ContainsKey(_groupKey)) plan.GroupNames[_groupKey] = Core.GroupDefault(plan);
            foreach (var kv in options) Options.Add(new OptionVM(kv.Key, kv.Value) { FromAddon = plan.Options.Any(op => op.AddonInput != null && op.Outfits.Contains(kv.Key)) });
        }
        public List<OptionVM> Options { get; } = new List<OptionVM>();
        public Visibility OptionsVisibility => Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string OptionsCount => "· " + Options.Count;
        // the options of the mod itself (the add-ons' are in the Add-ons tab)
        public List<OptionVM> VisibleOptions => Options.Where(o => !o.FromAddon).ToList();
        public string AddonOptionsNote => Options.Count(o => o.FromAddon) is int n && n > 0 ? "+ " + n + (n == 1 ? " option" : " options") + " from add-ons: name or switch them off in the Add-ons tab." : null;
        public Visibility AddonOptionsVisibility => AddonOptionsNote != null ? Visibility.Visible : Visibility.Collapsed;
        public RowVM(PlanOutfit o, string optName, bool multi)
        {
            _o = o;
            foreach (var pv in o.Pickable) PickRows.Add(new PickVM(pv, this));
            if (o.Pickable.Count > 1) { /* the outfits of a pack have their own panel below */ }
            else if (o.Variants.Count > 1)
            {
                // the stages of the campaign (clean/dirty, jacket/tank top...) or separate costumes (Noir, Elza Walker)
                bool stages = o.Variants.All(v => System.Text.RegularExpressions.Regex.IsMatch(v,
                    @"clean|dirty|dirtier|jacket|tank top|police|casual|injured|stage", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                Meta.Add(new MetaItem { Text = o.Variants.Count + (stages ? " story stages:" : " versions:") });
                foreach (var v in o.Variants) Meta.Add(new MetaItem { Text = v, IsPill = true });
            }
            else if (o.Variants.Count == 1 && o.Variants[0] != "all stages" && o.Variants[0] != "extra")
                Meta.Add(new MetaItem { Text = o.Variants[0], IsPill = true });
            if (o.Also.Count > 0) Meta.Add(new MetaItem { Text = "also used by " + string.Join(", ", o.Also) });
            if (multi) Meta.Add(new MetaItem { Text = "from “" + optName + "”" });
        }
        public string Part => _o.Part;
        public string PartLabel => _o.PartName.ToUpperInvariant();
        // switched off: the part (every option of it, for a group) is left out of the converted mod
        private IEnumerable<PlanOutfit> All => Options.Count > 0 ? Options.Select(x => x.Outfit) : new[] { _o };
        public bool Enabled
        {
            get => All.Any(x => !x.Disabled);
            set
            {
                foreach (var x in All) x.Disabled = !value;
                foreach (var op in Options) op.Enabled = value;
                Changed();
            }
        }
        public string EnabledTip => "Include this " + _o.PartName.ToLowerInvariant() + " in the converted mod";
        public string Off => "Left out: the converted mod won't have this " + _o.PartName.ToLowerInvariant() + " (the game's is worn).";
        public Visibility OffVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;
        public void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
        public string Name
        {
            get => _groupKey != null ? _plan.GroupNames[_groupKey] : _o.Name;
            set
            {
                if (_groupKey != null) { _plan.GroupNames[_groupKey] = value; return; }
                _o.Name = value;
                foreach (var p in PickRows) p.Refresh();
            }
        }
        public List<MetaItem> Meta { get; } = new List<MetaItem>();
        // body of a pack: one row per outfit (several outfits that replace different game costumes)
        public List<PickVM> PickRows { get; } = new List<PickVM>();
        public Visibility PickVisibility => PickRows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        public string PickCount => "· " + PickRows.Count;
        public bool IsBody => _o.Part == "body";
        public Visibility BodyVisibility => IsBody ? Visibility.Visible : Visibility.Collapsed;
        public string PickTitle => IsBody ? "OUTFITS IN THIS MOD" : "OPTIONS IN THIS MOD";
        public bool EachEntry
        {
            get => _o.EachEntry;
            set
            {
                if (_o.EachEntry == value) return;
                _o.EachEntry = value;
                foreach (var p in PickRows) p.Refresh();
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PickHint)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EachEntry)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SingleEntry)));
            }
        }
        // the other choice of the two-button switch (one entry for the whole mod)
        public bool SingleEntry { get => !EachEntry; set { if (value) EachEntry = false; } }

        /// <summary>This mod also brings a body for this character (a face/hair then goes with it on its own).</summary>
        public bool BodyInMod;
        public Visibility MetaVisibility => Meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WhereVisibility => string.IsNullOrEmpty(Where) ? Visibility.Collapsed : Visibility.Visible;
        public string NameTip => IsBody ? "Name of this outfit in the game's menus" : "Name of this " + _o.PartName.ToLowerInvariant() + " in the Wardrobe menu";
        /// <summary>Where the part shows in game, in plain words (the technical details stay under "Details").</summary>
        public string Where
        {
            get
            {
                if (_groupKey != null && !_hybrid) return null; // options: their own text
                if (!IsBody)
                {
                    string part = _o.PartName.ToLowerInvariant();
                    return BodyInMod ? "Worn with this mod's outfit on its own (Face/Hair on automatic in the Wardrobe window)."
                        : "Pick this " + part + " under " + _o.PartName + " in the Wardrobe window; it goes with any outfit.";
                }
                if (PickRows.Count > 1) return null; // a pack: its outfits below say it
                var v = _o.Variants.Where(x => x != "all stages" && x != "extra").ToList();
                if (v.Count == 0) return null;
                bool stages = v.All(x => StageWords.IsMatch(x));
                if (stages)
                {
                    bool alt = v.All(x => x.StartsWith("Classic", StringComparison.OrdinalIgnoreCase));
                    return (alt ? "Alternative Outfit" : "Normal Outfit") + (v.Count > 1 ? ": follows the story (" + string.Join(", ", v) + ")." : ": " + v[0] + ".");
                }
                return v.Count == 1 ? "Made for the " + v[0] + " costume." : "Made for these costumes: " + string.Join(", ", v) + ".";
            }
        }
        private static readonly System.Text.RegularExpressions.Regex StageWords = new System.Text.RegularExpressions.Regex(
            @"clean|dirty|dirtier|jacket|tank top|police|casual|injured|stage", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public string PickHint => !IsBody
            ? "In the Wardrobe menu, this " + _o.PartName.ToLowerInvariant() + " is one entry with these options. Left on automatic, each one goes with the costume it replaces; picked there, the chosen one goes with any costume. Empty name = the grey one."
            : EachEntry
            ? "Each outfit gets its own entry in the game's costume menu, with this name and picture. Empty name = the grey one."
            : Core.Re3Menu.ContainsKey(_o.Character ?? "")
            ? "The game's costume menu gets one entry for this mod, with the mod's picture: it wears the first outfit, or the one picked under \"Costume\" in the Wardrobe window. Empty name = the grey one."
            : "The game's costume menu gets one entry for this mod (it follows the story and uses the mod's picture). The names are used in the Wardrobe menu. Empty name = the grey one.";
    }

    public sealed class GroupVM
    {
        public string Name { get; set; }
        public string CountText { get; set; }
        public List<RowVM> Rows { get; } = new List<RowVM>();
    }

    /// <summary>A loaded mod (one package to create).</summary>
    public sealed class ModVM
    {
        private static readonly Dictionary<string, int> PartOrder = new Dictionary<string, int> { ["body"] = 0, ["face"] = 1, ["hair"] = 2 };
        public Plan Plan { get; }
        public ModVM(Plan plan, bool canRemove)
        {
            Plan = plan;
            RemoveVisibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
            bool multi = plan.Options.Count > 1;
            // several outfits of the same part in one package (the colours of a hair mod): in the game they are options of one entry
            var sameKind = plan.Options.SelectMany(p => p.Outfits.Select(o => new KeyValuePair<PlanOutfit, string>(o, p.Name)))
                .GroupBy(kv => kv.Key.Character + "|" + kv.Key.Part).Where(x => x.Count() > 1)
                .ToDictionary(x => x.Key, x => x.ToList());
            var grouped = new HashSet<string>();
            foreach (var opt in plan.Options)
            {
                foreach (var o in opt.Outfits)
                {
                    OutfitCount++;
                    string kind = o.Character + "|" + o.Part;
                    RowVM row;
                    if (sameKind.TryGetValue(kind, out var opts))
                    {
                        // the mod has one of this part and the rest comes from add-ons given in the window: its own row stays as it was
                        // (a pack keeps its outfits and pictures), with the add-ons as more options; else one row for the group
                        var own = opts.Where(kv => !plan.Options.Any(op => op.AddonInput != null && op.Outfits.Contains(kv.Key))).ToList();
                        if (own.Count == 1)
                        {
                            if (o != own[0].Key) continue;
                            row = new RowVM(o, opt.Name, multi);
                            row.AttachGroup(opts, plan);
                        }
                        else
                        {
                            if (!grouped.Add(kind)) continue; // already in its group's row
                            row = new RowVM(opts, plan);
                        }
                    }
                    else row = new RowVM(o, opt.Name, multi);
                    var g = Groups.FirstOrDefault(x => x.Name == o.CharacterName);
                    if (g == null) Groups.Add(g = new GroupVM { Name = o.CharacterName });
                    g.Rows.Add(row);
                }
            }
            foreach (var g in Groups)
            {
                bool body = g.Rows.Any(r => r.IsBody);
                foreach (var r in g.Rows) r.BodyInMod = body;
                var sorted = g.Rows.OrderBy(r => PartOrder.TryGetValue(r.Part, out var i) ? i : 9).ToList();
                g.Rows.Clear();
                g.Rows.AddRange(sorted);
                g.CountText = g.Rows.Count + (g.Rows.Count == 1 ? " part" : " parts");
            }
        }
        public string Input => Plan.Input;
        public string FileName => Path.GetFileName(Plan.Input.TrimEnd('\\', '/'));
        public string PackageName { get => Plan.PackageName; set => Plan.PackageName = value; }
        public List<GroupVM> Groups { get; } = new List<GroupVM>();
        public int OutfitCount { get; }
        public Visibility RemoveVisibility { get; }
        public Visibility WarningsVisibility => Plan.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // picture in the game's costume menu (RE2 Leon/Claire, RE3 Jill/Carlos outfits): the user's image, else the mod's own
        // costume-menu picture, else the mod's screenshot, else one drawn from the outfit, else a placeholder
        private string Screenshot => Plan.Options.Select(o => o.Screenshot).FirstOrDefault(s => s != null);
        private string MenuShot => Plan.Options.Select(o => o.Screenshot).FirstOrDefault(Core.FitsMenu);
        private string Drawn => (Core.MainOption(Plan)?.Outfits ?? new List<PlanOutfit>()).Concat(Plan.Options.SelectMany(o => o.Outfits))
            .Select(o => o.Drawn).FirstOrDefault(d => d != null && File.Exists(d));
        public Visibility PictureVisibility =>
            Plan.Options.Any(o => o.Outfits.Any(x => x.Part == "body" && (Plan.Game == "re2" ? (x.Character == "claire" || x.Character == "leon") : (x.Character == "jill" || x.Character == "carlos"))))
                ? Visibility.Visible : Visibility.Collapsed;
        public string PictureText =>
            Plan.CustomPicture != null ? "Your image: " + Path.GetFileName(Plan.CustomPicture)
            : Plan.PictureChoice == "pose" && Drawn != null ? "Drawn from the outfit."
            : Plan.PictureChoice == "mod" ? (Plan.HasMenuPicture ? "The mod's own costume menu picture." : "The mod's image.")
            : Plan.HasMenuPicture ? "The mod's own costume menu picture."
            : MenuShot != null ? "The mod's image."
            : Drawn != null && Screenshot != null ? "The mod's image isn't made for the costume menu, so this picture was drawn from the outfit."
            : Drawn != null ? "The mod has no image, so this picture was drawn from the outfit."
            : Screenshot != null ? "The mod's image."
            : "No image in this mod: the game shows a placeholder. Take a screenshot in game and choose it here.";

        private ImageSource _preview;
        private bool _previewDone;
        public ImageSource PicturePreview
        {
            get
            {
                if (_previewDone) return _preview;
                _previewDone = true;
                bool pose = Plan.CustomPicture == null && Plan.PictureChoice == "pose" && Drawn != null;
                bool mod = Plan.CustomPicture == null && Plan.PictureChoice == "mod";
                if (!pose && Plan.CustomPicture == null && Plan.HasMenuPicture) return _preview = TexImage(Plan.MenuPictureFile);
                string f = pose ? Drawn : mod ? MenuShot ?? Screenshot : Plan.CustomPicture ?? MenuShot ?? Drawn ?? Screenshot;
                if (f == null || !File.Exists(f)) return null;
                try
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file open
                    b.DecodePixelWidth = 240;
                    b.UriSource = new Uri(f);
                    b.EndInit();
                    b.Freeze();
                    return _preview = b;
                }
                catch { return null; }
            }
        }

        // the mod's costume-menu picture (.tex) as an image
        public static ImageSource TexImage(string file)
        {
            try
            {
                var rgba = TexDecode.Mip0(File.ReadAllBytes(file), out int w, out int h, out _);
                var bgra = new byte[rgba.Length];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
                }
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        public Visibility PreviewVisibility => PicturePreview != null ? Visibility.Visible : Visibility.Collapsed;
        // drawn pictures (RE2): the pose can be changed (the picture is drawn again)
        private bool ShowsDrawn => Plan.CustomPicture == null && Drawn != null
            && (Plan.PictureChoice == "pose" || Plan.PictureChoice == null && !Plan.HasMenuPicture && MenuShot == null);
        public Visibility PoseVisibility => ShowsDrawn && Core.PoseCount(Plan) > 0 ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>The mod's own picture (its costume-menu .tex, else its image), for the "Change picture" choices; null if none.</summary>
        public ImageSource ModPictureImage()
        {
            if (Plan.HasMenuPicture) return TexImage(Plan.MenuPictureFile);
            return LoadImage(MenuShot ?? Screenshot);
        }
        public static ImageSource LoadImage(string f)
        {
            if (f == null || !File.Exists(f)) return null;
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                b.DecodePixelWidth = 336;
                b.UriSource = new Uri(f);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
        /// <summary>Which choice is used now: "custom", "mod", "pose0", "pose1"...</summary>
        public string CurrentPictureKey =>
            Plan.CustomPicture != null ? "custom"
            : Plan.PictureChoice == "pose" || Plan.PictureChoice == null && Drawn != null && !Plan.HasMenuPicture && MenuShot == null ? "pose" + Plan.Pose
            : "mod";
        public string PoseText => "Pose: " + OutfitRender.PoseNames[Math.Min(Plan.Pose, OutfitRender.PoseNames.Length - 1)];
        public string WarningsText => string.Join("\n", Plan.Warnings.Select(w => "• " + w));

        /// <summary>Picture of the mod's tile in the grid: the costume-menu picture, else the mod's image (any part).</summary>
        private ImageSource _tile;
        private bool _tileDone;
        public ImageSource TilePicture { get { if (!_tileDone) { _tileDone = true; _tile = PicturePreview ?? LoadImage(Screenshot); } return _tile; } }
        public Visibility NoTilePictureVisibility => TilePicture == null ? Visibility.Visible : Visibility.Collapsed;
        public string WarningsTitle => Plan.Warnings.Count == 1 ? "1 warning" : Plan.Warnings.Count + " warnings";

        /// <summary>The mod in a few lines (left column of its screen).</summary>
        public List<KeyValuePair<string, string>> Facts
        {
            get
            {
                var f = new List<KeyValuePair<string, string>>();
                var main = Core.MainOption(Plan) ?? Plan.Options.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(main?.Author)) f.Add(new KeyValuePair<string, string>("Author", main.Author));
                if (!string.IsNullOrWhiteSpace(main?.Version)) f.Add(new KeyValuePair<string, string>("Version", main.Version));
                foreach (var g in Groups)
                    f.Add(new KeyValuePair<string, string>(g.Name, string.Join(", ", g.Rows.Select(r => r.PartLabel.Substring(0, 1) + r.PartLabel.Substring(1).ToLowerInvariant()))));
                if (Plan.Options.Count > 1) f.Add(new KeyValuePair<string, string>("Folders", Plan.Options.Count + " (" + string.Join(", ", Plan.Options.Select(o => o.Name)) + ")"));
                if (Plan.ModBuild != null) f.Add(new KeyValuePair<string, string>("Made for", GameBuilds.Name(Plan.ModBuild)));
                f.Add(new KeyValuePair<string, string>("File", FileName));
                return f;
            }
        }
        /// <summary>"3 outfits · Claire, Leon"</summary>
        public string Summary => OutfitCount + (OutfitCount == 1 ? " outfit" : " outfits") + (Groups.Count > 0 ? " · " + string.Join(", ", Groups.Select(g => g.Name)) : "");
    }

    /// <summary>A tile of the mods grid: a mod waiting to be read, being read, read (Plan/VM) or that could not be read (Error).</summary>
    public sealed class ModItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        public ModItem(string input, string gameBuild) { Input = input; GameBuild = gameBuild; }
        public string Input { get; }
        public string GameBuild;      // the installed game's version ("rt" / "nonrt" / null)
        /// <summary>The mod is only for the other game version (DX11 vs DX12): converted anyway, but it won't show in this game.</summary>
        public bool WrongBuild => Plan != null && GameBuilds.Mismatch(Plan.ModBuild, GameBuild);
        public Visibility WrongBuildVisibility => WrongBuild ? Visibility.Visible : Visibility.Collapsed;
        public string WrongBuildText => WrongBuild ? "For " + GameBuilds.Name(Plan.ModBuild) + " — not your game version" : null;
        public Plan Plan;
        public ModVM VM;
        public string Error;
        public bool Started;          // being read now (else waiting its turn)
        public bool Loading = true;
        public bool Removed;          // removed while being read: the result is thrown away
        public bool PicturePending;   // read, its picture still being drawn (spinner over the picture only)
        public Visibility SpinnerVisibility => Loading || PicturePending ? Visibility.Visible : Visibility.Collapsed;

        public string FileName => Path.GetFileName(Input.TrimEnd('\\', '/'));
        // the name given in Fluffy, without the " (Wardrobe)" every package ends with
        public string Name => Plan != null
            ? System.Text.RegularExpressions.Regex.Replace(Plan.PackageName ?? "", @"\s*\(Wardrobe\)\s*$", "") is string n && n.Trim().Length > 0 ? n.Trim() : Plan.BaseName
            : Core.CleanName(FileName);
        private ImageSource _picture;
        private bool _pictureDone;
        public ImageSource Picture
        {
            get
            {
                if (VM == null) return null;
                if (!_pictureDone) { _pictureDone = true; _picture = VM.TilePicture; }
                return _picture;
            }
        }
        public Visibility LoadingVisibility => Loading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ErrorVisibility => Error != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoPictureVisibility => !Loading && !PicturePending && Error == null && Picture == null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WarnVisibility => Plan != null && Plan.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // a mod of another game says so on the tile ("Not a Resident Evil 3 mod"); other errors: click for the reason
        public string Status => Loading ? (Started ? "Reading…" : "Waiting…")
            : Error != null ? (Error.StartsWith("Not a ") ? Error.Split('(')[0].Trim() : Error.StartsWith("Already converted") ? "Already converted"
                : Error.StartsWith("No ") && Error.Length < 70 ? Error.Split('(', ':')[0].Trim() : "Could not read (click for why)")
            : VM.Summary;
        public Brush StatusBrush => Error != null ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x98)) : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6));
        public System.Windows.Input.Cursor Cursor => Loading ? null : System.Windows.Input.Cursors.Hand;
        public string Tip => Error ?? Input;
        /// <summary>New VM (after a picture change) or a new name: redraw the tile.</summary>
        public void Refresh(bool picture = false)
        {
            if (picture) _pictureDone = false;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
        }
    }

    public partial class MainWindow : Window
    {
        // (folder name kept from the old program name, so updating keeps the saved game folder)
        private static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RE2R Custom Outfits Converter");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.txt");

        private readonly List<Plan> _plans = new List<Plan>();                             // the mods read so far (ready to save)
        private readonly System.Collections.ObjectModel.ObservableCollection<ModItem> _items = new System.Collections.ObjectModel.ObservableCollection<ModItem>(); // the grid
        private ModItem _current;                                                          // the mod open on its screen
        private Task _queue;                                                               // reads the mods of the grid, one by one
        private int _generation;                                                           // +1 when the list is cleared: a read in progress is thrown away
        private List<string> _lastOutputs = new List<string>();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };

        public MainWindow()
        {
            InitializeComponent();
            Tiles.ItemsSource = _items;
            _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            SourceInitialized += (s, e) => DarkTitleBar();
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => SetDropHighlight(false);
            Drop += OnDrop;
            Closing += (s, e) => ClearPlans();
            System.Threading.Tasks.Task.Run(() => Core.SweepOldTemp()); // leftovers of earlier runs, in the background
            LoadGameDir();
            Show(GameView); // the first screen: pick the game
            if (Environment.GetEnvironmentVariable("CO_SNAPSHOT") != null) SnapshotMode();
        }

        // Development mode (only with the CO_SNAPSHOT variable): opens off-screen, loads CO_SNAPSHOT_INPUT
        // (optional; several separated by |), saves an image of the window to CO_SNAPSHOT and closes.
        private void SnapshotMode()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = -20000;
            ShowInTaskbar = false;
            if (int.TryParse(Environment.GetEnvironmentVariable("CO_SNAPSHOT_HEIGHT"), out int tall) && tall > 0) { MaxHeight = tall; Height = tall; }
            Loaded += async (s, e) =>
            {
                string input = Environment.GetEnvironmentVariable("CO_SNAPSHOT_INPUT");
                // (CO_SNAPSHOT_GAME=re2/re3: picks that game first; with an input and no game, RE2. Without either: the game picker)
                string game = Environment.GetEnvironmentVariable("CO_SNAPSHOT_GAME") ?? (string.IsNullOrEmpty(input) ? null : "re2");
                if (game != null) await SelectGame(game, quiet: true);
                // (CO_SNAPSHOT_EARLY: the image is taken while the mods are still being read, to see the spinners)
                if (!string.IsNullOrEmpty(input))
                {
                    var open = OpenMods(input.Split('|'), false);
                    if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_EARLY") != null) await Task.Delay(1500); else await open;
                }
                await Task.Delay(300);
                // (CO_SNAPSHOT_DETAIL: the screen of the first mod read)
                var firstReady = _items.FirstOrDefault(i => i.VM != null);
                if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_DETAIL") != null && firstReady != null) { OpenDetail(firstReady); await Task.Delay(300); }
                // (CO_SNAPSHOT_ADDONS=<a>|<b>: given to the first mod as add-ons; CO_SNAPSHOT_TAB=outfits: back to its outfits tab;
                // CO_SNAPSHOT_OFF=<n>: switches off the n-th outfit of its pack)
                if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_ADDONS") != null && firstReady != null)
                {
                    await AddAddons(firstReady, Environment.GetEnvironmentVariable("CO_SNAPSHOT_ADDONS").Split('|'));
                    if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_TAB") == "outfits") TabOutfits.IsChecked = true;
                    await Task.Delay(300);
                }
                if (int.TryParse(Environment.GetEnvironmentVariable("CO_SNAPSHOT_OFF"), out int offN) && firstReady?.VM != null)
                {
                    var row = firstReady.VM.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.PickRows.Count > offN);
                    if (row != null) row.PickRows[offN].Enabled = false;
                    await Task.Delay(300);
                }
                // (CO_SNAPSHOT_WAIT=<ms>: waits before the image, e.g. for the pictures drawn in the background)
                if (int.TryParse(Environment.GetEnvironmentVariable("CO_SNAPSHOT_WAIT"), out int wait)) await Task.Delay(wait);
                if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_END") != null) { PlanView.ScrollToEnd(); DetailScroll.ScrollToEnd(); await Task.Delay(200); }
                // (CO_SNAPSHOT_DIALOG=<png>: also an image of the "Change picture" choices)
                if (Environment.GetEnvironmentVariable("CO_SNAPSHOT_DIALOG") != null && firstReady != null) await ChangePicture(firstReady.VM);
                var root = (FrameworkElement)Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(Environment.GetEnvironmentVariable("CO_SNAPSHOT"))) enc.Save(fs);
                Close();
            };
        }

        // ------------------------------------------------------------ window
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void DarkTitleBar()
        {
            try
            {
                int on = 1;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4); // older Win10 uses 19
            }
            catch { /* unsupported Windows: light title bar */ }
        }

        private void Show(UIElement view)
        {
            // leaving the result screen: its notes go with it (they don't close by themselves)
            if (DoneView.Visibility == Visibility.Visible && view != DoneView) { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; }
            foreach (var v in new UIElement[] { GameView, EmptyView, PlanView, DetailView, DoneView }) v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
            Actions.Visibility = view == PlanView ? Visibility.Visible : Visibility.Collapsed;
            TopBar.Visibility = view == GameView ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetBusy(bool on, string text = null)
        {
            Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (text != null) BusyText.Text = text;
            var sb = (Storyboard)FindResource("Spin");
            if (on) sb.Begin(this, true); else sb.Stop(this);
        }

        // warning = only notes, nothing went wrong: orange like the warnings box of the list (red reads as a failure at a glance)
        // stay = the notes of the result screen: they stay until that screen is left (they vanished before anyone read them)
        private void ToastClose_Click(object sender, RoutedEventArgs e) { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; }

        private void ShowToast(string msg, bool warning = false, bool stay = false)
        {
            Toast.Background = new SolidColorBrush(warning ? Color.FromRgb(0x22, 0x1D, 0x12) : Color.FromRgb(0x2A, 0x14, 0x18));
            Toast.BorderBrush = new SolidColorBrush(warning ? Color.FromRgb(0x5A, 0x44, 0x15) : Color.FromRgb(0x6B, 0x25, 0x31));
            ToastText.Foreground = new SolidColorBrush(warning ? Color.FromRgb(0xE6, 0xD3, 0xA8) : Color.FromRgb(0xFF, 0xD5, 0xDB));
            ToastText.Text = msg;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            if (!stay) _toastTimer.Start();
        }

        // ------------------------------------------------------------ games
        // First screen: the cover of each game. The program reads original game files (materials the mod doesn't ship,
        // story stages), so picking a game makes sure its folder is known: the saved one, else found through Steam, else
        // asked for. Then its costume DLCs are checked, and only mods of that game are accepted.
        private static readonly string[] Games = { "re2", "re3" };                          // supported
        private static readonly string[] AllGames = { "re2", "re3", "re4", "re9" };         // on the picker (re4, re9: coming)
        private readonly Dictionary<string, string> _gameDirs = new Dictionary<string, string>();
        private string _game;                                                               // the chosen game (null on the picker)
        private string _build;                                                              // its version: "rt", "nonrt" or null (unknown)
        private List<DlcStatus> _dlcs = new List<DlcStatus>();

        // RE2 keeps the old settings file (updating keeps the saved folder)
        private static string SettingsFileOf(string game) => game == "re2" ? SettingsFile : Path.Combine(SettingsDir, "settings-" + game + ".txt");

        private void LoadGameDir()
        {
            foreach (var g in Games)
            {
                string d = null;
                try { if (File.Exists(SettingsFileOf(g))) d = File.ReadAllText(SettingsFileOf(g)).Trim(); } catch { }
                _gameDirs[g] = Core.IsGameDir(d, g) ? d : null;
            }
            RenderGame();
        }

        private static System.Windows.Media.Imaging.BitmapSource Cover(string game, bool grey)
        {
            var b = new System.Windows.Media.Imaging.BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri("pack://application:,,,/Assets/covers/" + game + ".jpg");
            b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            if (!grey) return b;
            var g = new System.Windows.Media.Imaging.FormatConvertedBitmap(b, PixelFormats.Gray8, null, 0);
            g.Freeze();
            return g;
        }

        // one card per game: cover, name and state; the games not supported yet are grey and can't be picked
        private void BuildGameCards()
        {
            GameCards.Children.Clear();
            foreach (var g in AllGames)
            {
                bool ready = Games.Contains(g);
                _gameDirs.TryGetValue(g, out var dir);
                string state = !ready ? "Coming soon" : Core.IsGameDir(dir, g) ? "Ready" : "Folder not set yet";
                var img = new Image { Source = Cover(g, !ready), Width = 200, Height = 300, Stretch = Stretch.UniformToFill };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var pic = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img, Opacity = ready ? 1 : 0.45 };
                // (the rounded corners of the cover: a clip that follows the border)
                pic.Loaded += (s, e) => pic.Clip = new RectangleGeometry(new Rect(0, 0, pic.ActualWidth, pic.ActualHeight), 8, 8);
                var stack = new StackPanel();
                stack.Children.Add(pic);
                stack.Children.Add(new TextBlock
                {
                    Text = Core.GameNames[g], FontSize = 14.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 0), Foreground = ready ? (Brush)FindResource("Text") : new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x75)),
                });
                stack.Children.Add(new TextBlock
                {
                    Text = state, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
                    Foreground = !ready ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64)) : state == "Ready" ? (Brush)FindResource("Ok") : (Brush)FindResource("Muted"),
                });
                var card = new Button { Content = stack, Tag = g, IsEnabled = ready, Margin = new Thickness(10, 0, 10, 0), Template = CardTemplate(), Cursor = ready ? System.Windows.Input.Cursors.Hand : null };
                if (ready) card.ToolTip = Core.IsGameDir(dir, g) ? dir : "The converter looks for the game folder (Steam) when you pick it.";
                card.Click += GameCard_Click;
                GameCards.Children.Add(card);
            }
        }

        // a card: no button chrome; a red frame and a small lift on hover
        private static ControlTemplate CardTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "F");
            border.SetValue(Border.PaddingProperty, new Thickness(8));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            border.SetValue(UIElement.RenderTransformProperty, new TranslateTransform());
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            t.VisualTree = border;
            var hover = new MultiTrigger();
            hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
            hover.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xE0, 0x28, 0x3F)), "F"));
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x14, 0xC8, 0x10, 0x2E)), "F"));
            hover.Setters.Add(new Setter(UIElement.RenderTransformProperty, new TranslateTransform(0, -3), "F"));
            t.Triggers.Add(hover);
            return t;
        }

        private async void GameCard_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string g) await SelectGame(g);
        }

        /// <summary>Picks a game: makes sure its folder is known (saved, Steam, else asked), checks its DLCs, opens the drop screen.</summary>
        private async Task<bool> SelectGame(string game, bool quiet = false)
        {
            string dir = GameDir(game);
            if (dir == null)
            {
                if (quiet) return false;
                string name = Core.GameNames[game];
                bool pick = ConfirmDialog.Show(this, name + " not found",
                    $"The {name} folder was not found automatically.",
                    $"The converter reads some original files from the game. Choose the {name} folder (the one with {game}.exe).",
                    "Choose folder", "Cancel");
                if (!pick || !ChooseGame(game)) return false;
                dir = GameDir(game);
            }
            await CheckGame(game, dir);
            _game = game;
            ClearPlans();
            RenderGame();
            Show(EmptyView);
            return true;
        }

        private void BackToGames_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count > 0 && !ConfirmDialog.Show(this, "Choose another game",
                    _items.Count == 1 ? "The loaded mod will be closed." : "The " + _items.Count + " loaded mods will be closed.",
                    "Nothing was saved yet. The converter works on the mods of one game at a time.", "Close them", "Cancel"))
                return;
            ClearPlans();
            _game = null;
            RenderGame();
            Show(GameView);
        }

        private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null || !ChooseGame(_game)) return;
            // another copy of the game can have other DLCs and be the other version
            await CheckGame(_game, GameDir(_game));
            RenderGame();
            foreach (var it in _items) { it.GameBuild = _build; it.Refresh(); }
            RenderPlans();
        }

        /// <summary>The costume DLCs and the version (DX11 non-RT / DX12 RT) of the game in dir.</summary>
        private async Task CheckGame(string game, string dir)
        {
            SetBusy(true, "Checking your game version and DLCs...");
            try
            {
                var r = await Task.Run(() => (Dlcs.Scan(game, dir), GameBuilds.Detect(game, dir)));
                _dlcs = r.Item1;
                _build = r.Item2;
            }
            catch { _dlcs = new List<DlcStatus>(); _build = null; }
            SetBusy(false);
        }

        /// <summary>The folder of a game: the saved one, else found through Steam (and saved), else null.</summary>
        private string GameDir(string game)
        {
            _gameDirs.TryGetValue(game, out var d);
            if (Core.IsGameDir(d, game)) return d;
            d = Core.DetectGameDir(game);
            if (d != null) SaveGameDir(game, d);
            return d;
        }

        private void SaveGameDir(string game, string dir)
        {
            _gameDirs[game] = dir;
            try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(SettingsFileOf(game), dir ?? ""); } catch { }
            RenderGame();
        }

        // the picker's cards, the top bar and the DLC list of the chosen game
        private void RenderGame()
        {
            BuildGameCards();
            if (_game == null) return;
            _gameDirs.TryGetValue(_game, out var dir);
            TopCover.Source = Cover(_game, false);
            TopGame.Text = Core.GameNames[_game];
            BuildText.Text = _build == null ? "version unknown" : GameBuilds.Name(_build);
            BuildChip.ToolTip = _build == null
                ? "The converter could not tell if your game is the DX11 (non-RT) or the DX12 (RT) version."
                : "Your game is the " + GameBuilds.Name(_build) + " version. Mods made for the other version don't work in it:\n"
                  + "the converter warns about them (Steam: game Properties > Betas switches the version).";
            TopFolder.Text = dir ?? "folder not set";
            DropTitle.Text = "Drop " + Core.GameNames[_game] + " skin mods here";

            DlcList.Children.Clear();
            DlcCard.Visibility = _dlcs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            int have = _dlcs.Count(d => d.Installed);
            DlcCount.Text = have + " of " + _dlcs.Count + " installed";
            DlcNote.Visibility = have < _dlcs.Count ? Visibility.Visible : Visibility.Collapsed;
            foreach (var d in _dlcs.OrderBy(x => x.Installed ? 0 : 1))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(d.Installed ? "M0,5 L3.5,8.5 L10,1" : "M1,1 L9,9 M9,1 L1,9"),
                    Stroke = d.Installed ? (Brush)FindResource("Ok") : new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x86)),
                    StrokeThickness = 1.8, Width = 10, Height = 10, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
                row.Children.Add(new TextBlock
                {
                    Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = d.Installed ? (Brush)FindResource("Text") : new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x86)),
                });
                DlcList.Children.Add(new Border
                {
                    Child = row, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 5, 12, 5), CornerRadius = new CornerRadius(14),
                    Background = d.Installed ? new SolidColorBrush(Color.FromArgb(0x1F, 0x2F, 0xBF, 0x71)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x23)),
                    BorderBrush = d.Installed ? new SolidColorBrush(Color.FromArgb(0x55, 0x2F, 0xBF, 0x71)) : (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    ToolTip = d.Installed ? "Installed" : "Not installed (Steam app " + d.Id + ")",
                });
            }
        }

        private bool ChooseGame(string game)
        {
            string name = Core.GameNames[game];
            string dir = FolderPicker.Pick(this, $"{name} game folder (with {game}.exe)");
            if (dir == null) return false;
            if (!Core.IsGameDir(dir, game))
            {
                ConfirmDialog.Show(this, "Not the game folder", $"This folder does not have {game}.exe and re_chunk_000.pak.",
                    $"Choose the {name} folder inside Steam\\steamapps\\common.", "OK", null);
                return false;
            }
            SaveGameDir(game, dir);
            return true;
        }

        // ------------------------------------------------------------ open mods
        private void OnDragOver(object sender, DragEventArgs e)
        {
            // (on the game picker nothing can be dropped: the game comes first)
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && Busy.Visibility != Visibility.Visible && _game != null;
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (files && (EmptyView.Visibility == Visibility.Visible || DetailView.Visibility == Visibility.Visible)) SetDropHighlight(true);
            e.Handled = true;
        }

        private void SetDropHighlight(bool on)
        {
            // (on a mod's screen, what is dropped is an add-on of that mod: its drop area lights up)
            foreach (var r in new[] { DropBorder, AddonDropBorder })
            {
                bool lit = on && (r == AddonDropBorder ? DetailView.Visibility == Visibility.Visible : EmptyView.Visibility == Visibility.Visible);
                r.Stroke = lit ? (Brush)FindResource("Accent2") : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44));
                r.Fill = lit ? new SolidColorBrush(Color.FromArgb(0x14, 0xC8, 0x10, 0x2E)) : new SolidColorBrush(Color.FromArgb(0x99, 0x1B, 0x1B, 0x1F));
            }
            if (on && DetailView.Visibility == Visibility.Visible) TabAddons.IsChecked = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            if (Busy.Visibility == Visibility.Visible || _game == null) return;
            // a mod's screen is open: the files are add-ons of that mod
            if (DetailView.Visibility == Visibility.Visible && _current?.Plan != null && e.Data.GetData(DataFormats.FileDrop) is string[] addons && addons.Length > 0)
            {
                await AddAddons(_current, addons);
                return;
            }
            // on the loaded-mods screen, dropping more adds them to the list
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                await OpenMods(paths, PlanView.Visibility == Visibility.Visible || DetailView.Visibility == Visibility.Visible);
        }

        private string[] PickFiles()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Skin mods",
                Multiselect = true,
                Filter = "Mod archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*",
            };
            return d.ShowDialog(this) == true ? d.FileNames : null;
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var files = PickFiles();
            if (files != null) await OpenMods(files, false);
        }

        private async void AddMods_Click(object sender, RoutedEventArgs e)
        {
            var files = PickFiles();
            if (files != null) await OpenMods(files, true);
        }

        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = FolderPicker.Pick(this, "Mod folder");
            if (dir != null) await OpenMods(new[] { dir }, false);
        }

        /// <summary>Empties the grid (a read in progress is thrown away when it ends).</summary>
        private void ClearPlans()
        {
            _generation++;
            foreach (var p in _plans) Core.Cleanup(p);
            _plans.Clear();
            foreach (var it in _items) it.Removed = true;
            _items.Clear();
            _current = null;
        }

        // Each mod gets its tile at once (with a spinner) and the grid shows right away; the mods are read one by one in the
        // background, so the window can be used meanwhile (open the mods already read, drop more). The task ends when every
        // mod of the grid is read.
        private Task OpenMods(IEnumerable<string> inputs, bool append)
        {
            var list = inputs.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0) return Task.CompletedTask;
            if (!append) ClearPlans();
            foreach (var input in list)
                if (!_items.Any(i => string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase))) // already in the grid
                    _items.Add(new ModItem(input, _build));
            if (_items.Count == 0) return Task.CompletedTask;
            RenderPlans();
            Show(PlanView);
            if (_queue == null || _queue.IsCompleted) _queue = ReadQueue();
            return _queue;
        }

        private async Task ReadQueue()
        {
            ModItem item;
            while (true)
            {
                item = _items.FirstOrDefault(i => i.Loading && !i.Started && !i.Removed);
                if (item == null)
                {
                    // every mod read: now the pictures still to draw (the mods can be opened meanwhile)
                    var pic = _items.FirstOrDefault(i => i.PicturePending && !i.Removed);
                    if (pic == null) break;
                    var p = pic.Plan;
                    int g = _generation;
                    await Task.Run(() => Core.DrawMissingPictures(p));
                    pic.PicturePending = false;
                    if (g != _generation || pic.Removed) continue;
                    pic.VM = new ModVM(p, false);
                    pic.Refresh(picture: true);
                    if (_current == pic) DetailSide.Content = pic.VM;
                    continue;
                }
                item.Started = true;
                item.Refresh();
                int gen = _generation;
                // only the chosen game: a mod of another game is refused at once (from the archive's list of files),
                // the folders of a pack for other games are left out
                string game = _game, dir = GameDir(game), input = item.Input;
                Plan plan = null;
                string error = null;
                try { plan = await Task.Run(() => Core.Analyze(input, game, dir, drawLater: true)); }
                catch (Exception ex) { error = ex.Message; }
                if (gen != _generation || item.Removed)
                {
                    if (plan != null) Core.Cleanup(plan); // removed (or another game picked) while it was read
                    continue;
                }
                item.Loading = false;
                if (plan != null) { item.Plan = plan; item.VM = new ModVM(plan, false); _plans.Add(plan); item.PicturePending = Core.NeedsDrawnPictures(plan); }
                else item.Error = error;
                item.Refresh();
                if (DetailView.Visibility == Visibility.Visible && _current != null) RenderNav(); // "2 of 5" grows as mods are read
                RenderPlans();
            }
        }

        /// <summary>The title and the action bar of the grid.</summary>
        private void RenderPlans()
        {
            int reading = _items.Count(i => i.Loading), failed = _items.Count(i => i.Error != null);
            int outfits = _items.Where(i => i.VM != null).Sum(i => i.VM.OutfitCount);
            ModsTitle.Text = _items.Count == 1 ? "1 mod" : _items.Count + " mods";
            var parts = new List<string>();
            if (reading > 0) parts.Add("reading " + reading + (reading == 1 ? " mod…" : " mods…"));
            if (_plans.Count > 0 || reading == 0 && failed == 0) parts.Add(outfits + (outfits == 1 ? " outfit" : " outfits") + (_plans.Count > 1 ? " in " + _plans.Count + " mods" : " found"));
            if (failed > 0) parts.Add(failed + " could not be read");
            int wrong = _items.Count(i => i.WrongBuild);
            if (wrong > 0) parts.Add(wrong + (wrong == 1 ? " is" : " are") + " for the other game version");
            Summary.Text = string.Join(" · ", parts);
            // saving waits until every mod is read
            bool ready = reading == 0 && _plans.Count > 0;
            bool allArchives = _plans.All(p => p.IsArchive);
            SaveNewButton.IsEnabled = ready;
            SaveOverButton.IsEnabled = ready && allArchives;
            SaveOverButton.ToolTip = allArchives ? "Replace the original files with the converted ones" : "Only when every mod is a .zip / .rar / .7z file";
            SaveNewButton.ToolTip = reading > 0 ? "Wait until every mod is read" : null;
            SaveNewButton.Content = _plans.Count > 1 ? "Save as new mods" : "Save as new mod";
        }

        // a tile: a mod read opens its screen; one that could not be read says why
        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is ModItem item) || item.Loading) return;
            if (item.Error != null) ShowToast(item.FileName + ":\n" + item.Error);
            else OpenDetail(item);
        }

        private void OpenDetail(ModItem item, bool keepTab = false)
        {
            _current?.Refresh(); // the one left (its name may have changed)
            _current = item;
            DetailTitle.Text = item.Name;
            DetailFile.Text = item.FileName;
            DetailFile.ToolTip = item.Input;
            DetailFile.Visibility = item.FileName == item.Name ? Visibility.Collapsed : Visibility.Visible; // (a folder named like the mod)
            DetailSide.Content = item.VM;
            Detail.Content = item.VM;
            BuildWarn.Visibility = item.WrongBuild ? Visibility.Visible : Visibility.Collapsed;
            if (item.WrongBuild)
            {
                string modB = GameBuilds.Name(item.Plan.ModBuild), gameB = GameBuilds.Name(item.GameBuild);
                BuildWarnTitle.Text = "This mod is for " + modB + ", but your game is " + gameB;
                BuildWarnText.Text = "It can be converted, but its outfits won't show in your game: the two versions use different files. "
                    + "Look for the " + gameB + " version of this mod on its page, or switch your game to " + modB
                    + " in Steam (game Properties > Betas).";
            }
            DetailScroll.ScrollToTop();
            if (!keepTab) TabOutfits.IsChecked = true;
            RenderAddons(item);
            RenderNav();
            Show(DetailView);
            DrawPackPictures(item);
        }

        // ------------------------------------------------------------ add-ons of a mod
        // A separate download made for a mod (other colours...): the user opens the mod and drops it (or chooses it) there. The mod
        // is read again with its add-ons (its extraction reused); what the user had set on it stays (Core.CarryOver).
        private void DetailTab_Checked(object sender, RoutedEventArgs e)
        {
            if (DetailScroll == null || AddonsScroll == null) return; // (while the window is being built)
            bool addons = TabAddons.IsChecked == true;
            DetailScroll.Visibility = addons ? Visibility.Collapsed : Visibility.Visible;
            AddonsScroll.Visibility = addons ? Visibility.Visible : Visibility.Collapsed;
        }

        public sealed class AddonVM
        {
            public string Input { get; set; }
            public string Name { get; set; }
            public string File { get; set; }
            public string Status { get; set; }
            public Brush StatusBrush { get; set; }
            public List<OptionVM> Parts { get; set; } // its outfits: name in the Wardrobe menu, switch
            public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RenderAddons(ModItem item)
        {
            var plan = item?.Plan;
            var list = (plan?.Addons ?? new List<PlanAddon>()).Select(a =>
            {
                var opts = plan.Options.Where(o => o.AddonInput == a.Input).ToList();
                var outfits = opts.SelectMany(o => o.Outfits).ToList();
                var parts = outfits.Select(o => o.CharacterName + " " + o.PartName.ToLowerInvariant()).Distinct().ToList();
                return new AddonVM
                {
                    Input = a.Input, Name = a.Name, File = Path.GetFileName(a.Input.TrimEnd('\\', '/')),
                    Status = a.Error != null ? "Could not be added: " + a.Error
                        : parts.Count > 0 ? null
                        : "Nothing to add: no outfit of this mod changes with it.",
                    Parts = outfits.Select(o => new OptionVM(o, o.CharacterName + " " + o.PartName.ToLowerInvariant()) { FromAddon = true }).ToList(),
                    StatusBrush = a.Error != null || parts.Count == 0 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x98)) : (Brush)FindResource("Muted"),
                };
            }).ToList();
            AddonList.ItemsSource = list;
            TabAddons.Content = list.Count > 0 ? "Add-ons (" + list.Count + ")" : "Add-ons";
            AddonDropTitle.Text = list.Count > 0 ? "Drop another add-on for this mod here" : "Drop an add-on for this mod here";
        }

        private Task AddAddons(ModItem item, IEnumerable<string> inputs)
        {
            var list = item.Plan.Addons.Select(a => a.Input).ToList();
            foreach (var i in inputs.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!string.Equals(i, item.Input, StringComparison.OrdinalIgnoreCase) && !list.Any(x => string.Equals(x, i, StringComparison.OrdinalIgnoreCase)))
                    list.Add(i);
            TabAddons.IsChecked = true;
            return RereadWithAddons(item, list);
        }

        private async void RemoveAddon_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is string input) || _current?.Plan == null) return;
            await RereadWithAddons(_current, _current.Plan.Addons.Select(a => a.Input).Where(x => x != input).ToList());
        }

        private async void ChooseAddon_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Plan == null) return;
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Add-ons for " + _current.Name, Multiselect = true, Filter = "Mod archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*" };
            if (d.ShowDialog(this) == true) await AddAddons(_current, d.FileNames);
        }

        private async void ChooseAddonFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Plan == null) return;
            string dir = FolderPicker.Pick(this, "Add-on folder for " + _current.Name);
            if (dir != null) await AddAddons(_current, new[] { dir });
        }

        private async Task RereadWithAddons(ModItem item, List<string> addons)
        {
            var old = item.Plan;
            SetBusy(true, addons.Count > old.Addons.Count ? "Reading the add-on..." : "Updating the mod...");
            string game = _game, dir = GameDir(game), input = item.Input, extracted = old.Extracted;
            Plan plan = null;
            try { plan = await Task.Run(() => Core.Analyze(input, game, dir, drawLater: true, addons: addons, reuseExtracted: extracted)); }
            catch (Exception ex) { ShowToast("Could not read the mod with this add-on:\n" + ex.Message); }
            SetBusy(false);
            if (plan == null || item.Removed) { if (plan != null) { plan.Extracted = null; Core.Cleanup(plan); } return; }
            Core.CarryOver(old, plan);
            old.Extracted = null; // (the new plan uses the same extraction)
            Core.Cleanup(old);
            int at = _plans.IndexOf(old);
            if (at >= 0) _plans[at] = plan; else _plans.Add(plan);
            item.Plan = plan;
            item.VM = new ModVM(plan, false);
            item.PicturePending = Core.NeedsDrawnPictures(plan);
            item.Refresh(picture: true);
            if (_current == item) OpenDetail(item, keepTab: true);
            var bad = plan.Addons.Where(a => a.Error != null).ToList();
            if (bad.Count > 0) ShowToast(string.Join("\n", bad.Select(a => a.Name + ": " + a.Error)));
            RenderPlans();
            if (item.PicturePending && (_queue == null || _queue.IsCompleted)) _queue = ReadQueue(); // (the pictures of the new options)
        }

        // ‹ 2 of 5 ›: the mods already read, in the grid's order
        private List<ModItem> ReadyItems => _items.Where(i => i.VM != null).ToList();

        private void RenderNav()
        {
            var ready = ReadyItems;
            int at = ready.IndexOf(_current);
            DetailNav.Visibility = ready.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            DetailPos.Text = (at + 1) + " of " + ready.Count;
            PrevMod.IsEnabled = at > 0;
            NextMod.IsEnabled = at >= 0 && at < ready.Count - 1;
        }

        private void PrevMod_Click(object sender, RoutedEventArgs e) => StepMod(-1);
        private void NextMod_Click(object sender, RoutedEventArgs e) => StepMod(+1);

        private void StepMod(int by)
        {
            var ready = ReadyItems;
            int at = ready.IndexOf(_current) + by;
            if (at >= 0 && at < ready.Count) OpenDetail(ready[at]);
        }

        private void BackToMods_Click(object sender, RoutedEventArgs e)
        {
            _current?.Refresh(); // the name may have changed
            _current = null;
            RenderPlans();
            Show(_items.Count > 0 ? PlanView : EmptyView);
        }

        private void TileRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ModItem item) RemoveItem(item);
        }

        private void RemoveDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_current != null) RemoveItem(_current);
        }

        private void RemoveItem(ModItem item)
        {
            item.Removed = true;
            if (item.Plan != null) { Core.Cleanup(item.Plan); _plans.Remove(item.Plan); }
            _items.Remove(item);
            if (_current == item) _current = null;
            if (_items.Count == 0) Show(EmptyView);
            else { RenderPlans(); if (DetailView.Visibility == Visibility.Visible) Show(PlanView); }
        }

        // the picture of one outfit of a pack: the same choices as the mod's picture (the mod's picture of that costume,
        // each pose drawn from this outfit, an image of the user's)
        private async void VariantPicture_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is PickVM pv) || _current?.Plan == null) return;
            var plan = _current.Plan;
            int poses = Core.PoseCount(plan);
            var drawn = new string[poses];
            if (poses > 0)
            {
                SetBusy(true, poses > 1 ? "Drawing the poses..." : "Drawing the picture...");
                try { await Task.Run(() => { for (int i = 0; i < poses; i++) drawn[i] = Core.DrawVariant(plan, pv.V, i); }); }
                catch { }
                SetBusy(false);
            }
            string own = pv.OwnPictureFile;
            int drawnAt = pv.V.Picture != null && pv.V.PictureDrawn ? Array.IndexOf(drawn, pv.V.Picture) : -1;
            string cur = pv.V.Picture == null ? (own != null ? "mod" : "pose" + Math.Min(plan.Pose, Math.Max(0, poses - 1)))
                : drawnAt >= 0 ? "pose" + drawnAt : "custom";
            var items = new List<PictureChoiceItem>();
            if (own != null) items.Add(new PictureChoiceItem { Key = "mod", Title = "Mod's picture", Detail = "The mod's picture of this costume", Image = ModVM.TexImage(own), Current = cur == "mod" });
            for (int i = 0; i < poses; i++)
                if (drawn[i] != null)
                    items.Add(new PictureChoiceItem
                    {
                        Key = "pose" + i, Title = "Pose " + (i + 1),
                        Detail = OutfitRender.PoseNames[Math.Min(i, OutfitRender.PoseNames.Length - 1)],
                        Image = ModVM.LoadImage(drawn[i]), Current = cur == "pose" + i,
                    });
            bool userImage = pv.V.Picture != null && !pv.V.PictureDrawn;
            items.Add(new PictureChoiceItem
            {
                Key = "custom", Title = "Upload image", Detail = userImage ? Path.GetFileName(pv.V.Picture) : "A picture of your own",
                Image = userImage ? ModVM.LoadImage(pv.V.Picture) : null, Current = cur == "custom",
            });
            string pick = PictureDialog.Show(this, "Picture of \"" + pv.Slot + "\"", items);
            if (pick == null) return;
            if (pick == "custom")
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Picture for \"" + pv.Slot + "\" in the game's costume menu",
                    Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
                };
                if (dlg.ShowDialog(this) != true) return;
                if (Tex.LoadRgba(dlg.FileName, 64, out _, out _) == null) { ShowToast("This image could not be read."); return; }
                pv.SetPicture(dlg.FileName);
            }
            else if (pick == "mod") pv.SetPicture(null);
            else pv.SetPicture(drawn[int.Parse(pick.Substring(4))], drawn: true);
        }

        // the automatic pictures of a pack's outfits (the mod has none for their costumes): drawn one by one in the background
        // while its screen is open, each card with a spinner until its picture is ready
        private readonly HashSet<ModItem> _drawingPack = new HashSet<ModItem>();

        private async void DrawPackPictures(ModItem item)
        {
            var plan = item?.Plan;
            if (plan == null || plan.DrawReports == null || Core.PoseCount(plan) == 0 || !_drawingPack.Add(item)) return;
            // (every card waiting for its picture shows the spinner, not only the one being drawn)
            List<PickVM> Waiting() => item.VM.Groups.SelectMany(g => g.Rows).Where(r => r.IsBody && r.PickRows.Count > 1)
                .SelectMany(r => r.PickRows).Where(p => p.NeedsDrawing && p.Enabled && !_failedDraw.Contains(p.V)).ToList(); // (switched off: not drawn)
            try
            {
                while (_current == item)
                {
                    var waiting = Waiting();
                    if (waiting.Count == 0) break;
                    foreach (var w in waiting.Where(w => !w.Drawing)) { w.Drawing = true; w.Refresh(); }
                    var card = waiting[0];
                    var pv = card.V;
                    // (the mod in use now: it is read again when an add-on is given, with new outfits to draw)
                    var cur = item.Plan;
                    if (cur?.DrawReports == null) break;
                    string file = await Task.Run(() => Core.DrawVariant(cur, pv, cur.Pose));
                    if (cur != item.Plan) { card.Drawing = false; card.Refresh(); continue; } // (read again meanwhile: draw the new ones)
                    if (file != null) pv.Drawn = file; else _failedDraw.Add(pv); // (can't be drawn: no retry)
                    card.Drawing = false;
                    card.Refresh();
                }
            }
            finally
            {
                _drawingPack.Remove(item);
                // left the mod's screen before the end: the rest are drawn when it is opened again
                foreach (var w in item.VM.Groups.SelectMany(g => g.Rows).SelectMany(r => r.PickRows).Where(p => p.Drawing)) { w.Drawing = false; w.Refresh(); }
            }
        }
        private readonly HashSet<PlanVariant> _failedDraw = new HashSet<PlanVariant>();

        private void VariantPictureReset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PickVM pv) pv.SetPicture(null);
        }

        // "Change picture": the mod's picture, each pose drawn from the outfit, or an image of the user's
        private async void ChangePicture_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ModVM vm) await ChangePicture(vm);
        }

        private async Task ChangePicture(ModVM vm)
        {
            var plan = vm.Plan;
            int poses = Core.PoseCount(plan);
            var drawn = new string[poses];
            if (poses > 0)
            {
                SetBusy(true, poses > 1 ? "Drawing the poses..." : "Drawing the picture...");
                try { await Task.Run(() => { for (int i = 0; i < poses; i++) drawn[i] = Core.PosePreview(plan, i); }); }
                catch { }
                SetBusy(false);
            }
            string cur = vm.CurrentPictureKey;
            var items = new List<PictureChoiceItem>();
            var modImg = vm.ModPictureImage();
            if (modImg != null) items.Add(new PictureChoiceItem { Key = "mod", Title = "Mod's picture", Detail = "The one that comes with the mod", Image = modImg, Current = cur == "mod" });
            for (int i = 0; i < poses; i++)
                if (drawn[i] != null)
                    items.Add(new PictureChoiceItem
                    {
                        Key = "pose" + i, Title = "Pose " + (i + 1),
                        Detail = OutfitRender.PoseNames[Math.Min(i, OutfitRender.PoseNames.Length - 1)],
                        Image = ModVM.LoadImage(drawn[i]), Current = cur == "pose" + i,
                    });
            items.Add(new PictureChoiceItem
            {
                Key = "custom", Title = "Upload image",
                Detail = plan.CustomPicture != null ? Path.GetFileName(plan.CustomPicture) : "A picture of your own",
                Image = plan.CustomPicture != null ? ModVM.LoadImage(plan.CustomPicture) : null, Current = cur == "custom",
            });
            string pick = PictureDialog.Show(this, "Change picture", items);
            if (pick == null) return;
            if (pick == "custom")
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Picture for the game's costume menu",
                    Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
                };
                if (dlg.ShowDialog(this) != true) return;
                if (Tex.LoadRgba(dlg.FileName, 64, out _, out _) == null) { ShowToast("This image could not be read."); return; }
                plan.CustomPicture = dlg.FileName;
            }
            else if (pick == "mod") { plan.CustomPicture = null; plan.PictureChoice = "mod"; }
            else
            {
                int n = int.Parse(pick.Substring(4));
                SetBusy(true, "Drawing the picture...");
                try { await Task.Run(() => Core.Redraw(plan, n)); plan.CustomPicture = null; plan.PictureChoice = "pose"; }
                catch (Exception ex) { ShowToast("Could not draw the picture: " + ex.Message); }
                SetBusy(false);
            }
            // a new picture: the mod's screen and its tile are drawn again
            var item = _items.FirstOrDefault(i => i.Plan == plan);
            if (item != null)
            {
                item.VM = new ModVM(plan, false);
                item.Refresh(picture: true);
                if (_current == item) { Detail.Content = item.VM; DetailSide.Content = item.VM; }
            }
        }

        private void StartOver_Click(object sender, RoutedEventArgs e)
        {
            ClearPlans();
            Show(EmptyView);
        }

        // ------------------------------------------------------------ save
        private static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim().Length > 0 ? s.Trim() : "Wardrobe";
        }

        private void NormalizeNames()
        {
            foreach (var p in _plans)
                if (string.IsNullOrWhiteSpace(p.PackageName)) p.PackageName = p.BaseName + " (Wardrobe)";
        }

        /// <summary>Mods for the other game version: converting them is allowed, after one clear confirmation.</summary>
        private bool ConfirmWrongBuilds()
        {
            var wrong = _items.Where(i => i.WrongBuild).ToList();
            if (wrong.Count == 0) return true;
            string game = GameBuilds.Name(_build);
            return ConfirmDialog.Show(this, "Other game version",
                wrong.Count == 1 ? "This mod is not for your game version (" + game + "):" : "These " + wrong.Count + " mods are not for your game version (" + game + "):",
                string.Join("\n", wrong.Select(i => "• " + i.Name + "  —  made for " + GameBuilds.Name(i.Plan.ModBuild)))
                    + "\n\nThey can be converted, but their outfits won't show in your game. Use the " + game
                    + " version of each mod, or switch the game version in Steam (game Properties > Betas).",
                "Convert anyway", "Cancel");
        }

        private async void SaveNew_Click(object sender, RoutedEventArgs e)
        {
            if (_plans.Count == 0 || !ConfirmWrongBuilds()) return;
            NormalizeNames();
            var jobs = new List<KeyValuePair<Plan, string>>();
            if (_plans.Count == 1)
            {
                var plan = _plans[0];
                var d = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save converted mod",
                    Filter = "Zip (*.zip)|*.zip",
                    InitialDirectory = Path.GetDirectoryName(plan.Input.TrimEnd('\\', '/')),
                    FileName = SafeName(plan.PackageName) + ".zip",
                };
                if (d.ShowDialog(this) != true) return;
                if (string.Equals(Path.GetFullPath(d.FileName), Path.GetFullPath(plan.Input), StringComparison.OrdinalIgnoreCase))
                {
                    ShowToast("That is the original file. Use \"Save over original\" to replace it.");
                    return;
                }
                jobs.Add(new KeyValuePair<Plan, string>(plan, d.FileName));
            }
            else
            {
                // several: pick a folder; one .zip per mod, never overwriting (an existing name gets " (2)")
                string start = Path.GetDirectoryName(_plans[0].Input.TrimEnd('\\', '/'));
                string folder = FolderPicker.Pick(this, "Folder to save the converted mods", start);
                if (folder == null) return;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in _plans)
                {
                    string baseName = SafeName(p.PackageName), target = Path.Combine(folder, baseName + ".zip");
                    for (int n = 2; File.Exists(target) || used.Contains(target); n++) target = Path.Combine(folder, $"{baseName} ({n}).zip");
                    used.Add(target);
                    jobs.Add(new KeyValuePair<Plan, string>(p, target));
                }
            }
            await Save(jobs, false);
        }

        private async void SaveOver_Click(object sender, RoutedEventArgs e)
        {
            if (_plans.Count == 0 || !_plans.All(p => p.IsArchive) || !ConfirmWrongBuilds()) return;
            NormalizeNames();
            var jobs = _plans.Select(p => new KeyValuePair<Plan, string>(p,
                Path.Combine(Path.GetDirectoryName(p.Input), Path.GetFileNameWithoutExtension(p.Input) + ".zip"))).ToList();
            string detail = _plans.Count == 1
                ? jobs[0].Key.Input + "\n\nwill be deleted and replaced by\n\n" + jobs[0].Value
                : "These " + _plans.Count + " files will be deleted and replaced by the converted mods (same name, .zip):\n\n"
                  + string.Join("\n", _plans.Select(p => "• " + Path.GetFileName(p.Input)));
            bool yes = ConfirmDialog.Show(this, "Save over original",
                _plans.Count == 1 ? "Replace the original mod with the converted one?" : "Replace the original mods with the converted ones?",
                detail + "\n\nThis cannot be undone. If an original mod is installed in Fluffy Mod Manager, uninstall it there first.",
                _plans.Count == 1 ? "Replace original" : "Replace originals", "Cancel");
            if (yes) await Save(jobs, true);
        }

        private async Task Save(List<KeyValuePair<Plan, string>> jobs, bool overwrite)
        {
            var outputs = new List<string>();
            var warnings = new List<string>();
            var failed = new List<string>();
            int outfits = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var plan = jobs[i].Key;
                string outZip = jobs[i].Value;
                SetBusy(true, jobs.Count > 1 ? $"Converting mod {i + 1} of {jobs.Count}... (big mods take a while)" : "Converting and packing the mod... (big mods take a while)");
                try
                {
                    var r = await Task.Run(() => Core.Build(plan, outZip, overwrite));
                    outputs.Add(r.Output);
                    outfits += r.Outfits;
                    warnings.AddRange(r.Warnings);
                    Core.Cleanup(plan);
                    _plans.Remove(plan);
                    var done = _items.FirstOrDefault(it => it.Plan == plan);
                    if (done != null) _items.Remove(done);
                }
                catch (Exception ex) { failed.Add(Path.GetFileName(plan.Input.TrimEnd('\\', '/')) + ": " + ex.Message); }
            }
            SetBusy(false);
            if (outputs.Count == 0)
            {
                ShowToast("Conversion failed:\n" + string.Join("\n", failed));
                return;
            }
            _lastOutputs = outputs;
            DoneTitle.Text = outputs.Count == 1 ? "Mod converted" : outputs.Count + " mods converted";
            DoneText.Text = outfits + (outfits == 1 ? " outfit" : " outfits") + " saved to:";
            DonePath.Text = string.Join("\n", outputs);
            var notes = new List<string>();
            if (failed.Count > 0) notes.Add("Not converted:\n" + string.Join("\n", failed));
            if (warnings.Count > 0) notes.Add("Warnings:\n" + string.Join("\n", warnings));
            if (notes.Count > 0) ShowToast(string.Join("\n\n", notes), warning: failed.Count == 0, stay: true);
            if (_items.Count > 0) RenderPlans(); // the ones that failed stay loaded
            Show(DoneView);
        }

        private void ShowFile_Click(object sender, RoutedEventArgs e)
        {
            string first = _lastOutputs.FirstOrDefault(File.Exists);
            if (first != null) Process.Start("explorer.exe", "/select,\"" + first + "\"");
        }

        private void Another_Click(object sender, RoutedEventArgs e) => Show(_items.Count > 0 ? PlanView : EmptyView);
    }
}
