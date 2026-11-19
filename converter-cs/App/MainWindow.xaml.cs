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

    public sealed class RowVM
    {
        private readonly PlanOutfit _o;
        public RowVM(PlanOutfit o, string optName, bool multi)
        {
            _o = o;
            if (o.Variants.Count > 1)
            {
                Meta.Add(new MetaItem { Text = o.Variants.Count + " story stages:" });
                foreach (var v in o.Variants) Meta.Add(new MetaItem { Text = v, IsPill = true });
            }
            else if (o.Variants.Count == 1 && o.Variants[0] != "all stages" && o.Variants[0] != "extra")
                Meta.Add(new MetaItem { Text = o.Variants[0], IsPill = true });
            if (o.Also.Count > 0) Meta.Add(new MetaItem { Text = "also used by " + string.Join(", ", o.Also) });
            if (multi) Meta.Add(new MetaItem { Text = "from “" + optName + "”" });
        }
        public string Part => _o.Part;
        public string PartLabel => _o.PartName.ToUpperInvariant();
        public string Name { get => _o.Name; set => _o.Name = value; }
        public List<MetaItem> Meta { get; } = new List<MetaItem>();
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
            foreach (var opt in plan.Options)
            {
                foreach (var o in opt.Outfits)
                {
                    var g = Groups.FirstOrDefault(x => x.Name == o.CharacterName);
                    if (g == null) Groups.Add(g = new GroupVM { Name = o.CharacterName });
                    g.Rows.Add(new RowVM(o, opt.Name, multi));
                    OutfitCount++;
                }
            }
            foreach (var g in Groups)
            {
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
        private string Drawn => (Core.MainOption(Plan)?.Outfits ?? new List<PlanOutfit>()).Concat(Plan.Options.SelectMany(o => o.Outfits))
            .Select(o => o.Drawn).FirstOrDefault(d => d != null && File.Exists(d));
        public Visibility PictureVisibility =>
            Plan.Options.Any(o => o.Outfits.Any(x => x.Part == "body" && (Plan.Game == "re2" ? (x.Character == "claire" || x.Character == "leon") : (x.Character == "jill" || x.Character == "carlos"))))
                ? Visibility.Visible : Visibility.Collapsed;
        public string PictureText =>
            Plan.CustomPicture != null ? "Your image: " + Path.GetFileName(Plan.CustomPicture)
            : Plan.HasMenuPicture ? "The mod's own costume menu picture."
            : Screenshot != null ? "The mod's image."
            : Drawn != null ? "The mod has no image, so this picture was drawn from the outfit."
            : "No image in this mod: the game shows a placeholder. Take a screenshot in game and choose it here.";

        private ImageSource _preview;
        private bool _previewDone;
        public ImageSource PicturePreview
        {
            get
            {
                if (_previewDone) return _preview;
                _previewDone = true;
                if (Plan.CustomPicture == null && Plan.HasMenuPicture) return _preview = TexImage(Plan.MenuPictureFile);
                string f = Plan.CustomPicture ?? Screenshot ?? Drawn;
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
        private static ImageSource TexImage(string file)
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
        public Visibility DefaultPictureVisibility => Plan.CustomPicture != null ? Visibility.Visible : Visibility.Collapsed;
        public string WarningsText => string.Join("\n", Plan.Warnings.Select(w => "• " + w));
    }

    public partial class MainWindow : Window
    {
        // (folder name kept from the old program name, so updating keeps the saved game folder)
        private static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RE2R Custom Outfits Converter");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.txt");

        private readonly List<Plan> _plans = new List<Plan>();
        private List<string> _lastOutputs = new List<string>();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };

        public MainWindow()
        {
            InitializeComponent();
            _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            SourceInitialized += (s, e) => DarkTitleBar();
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => SetDropHighlight(false);
            Drop += OnDrop;
            Closing += (s, e) => ClearPlans();
            System.Threading.Tasks.Task.Run(() => Core.SweepOldTemp()); // leftovers of earlier runs, in the background
            LoadGameDir();
            if (Environment.GetEnvironmentVariable("CO_SNAPSHOT") != null) SnapshotMode();
        }

        // Development mode (only with the CO_SNAPSHOT variable): opens off-screen, loads CO_SNAPSHOT_INPUT
        // (optional; several separated by |), saves an image of the window to CO_SNAPSHOT and closes.
        private void SnapshotMode()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = -20000;
            ShowInTaskbar = false;
            Loaded += async (s, e) =>
            {
                string input = Environment.GetEnvironmentVariable("CO_SNAPSHOT_INPUT");
                if (!string.IsNullOrEmpty(input)) await OpenMods(input.Split('|'), false);
                await Task.Delay(300);
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
            foreach (var v in new UIElement[] { EmptyView, PlanView, DoneView }) v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
            Actions.Visibility = view == PlanView ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool on, string text = null)
        {
            Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (text != null) BusyText.Text = text;
            var sb = (Storyboard)FindResource("Spin");
            if (on) sb.Begin(this, true); else sb.Stop(this);
        }

        private void ShowToast(string msg)
        {
            ToastText.Text = msg;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ------------------------------------------------------------ game folders
        // The program reads original game files (materials the mod doesn't ship, story stages). Each mod says
        // which game it is for, and only that game's folder is needed: the saved one, else found through Steam,
        // else asked for when a mod of that game is opened. Nobody needs RE2 to convert RE3 mods (or the reverse).
        private static readonly string[] Games = { "re2", "re3" };
        private readonly Dictionary<string, string> _gameDirs = new Dictionary<string, string>();

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

        private void RenderGame()
        {
            foreach (var (g, link) in new[] { ("re2", GameLink2), ("re3", GameLink3) })
            {
                _gameDirs.TryGetValue(g, out var d);
                link.ToolTip = Core.IsGameDir(d, g) ? d + " (click to change)"
                    : "Found automatically when a mod of this game is opened (Steam). Click to choose it yourself.";
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

        private void ChooseGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkContentElement)?.Tag is string game) ChooseGame(game);
        }

        // ------------------------------------------------------------ open mods
        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && Busy.Visibility != Visibility.Visible;
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (files && EmptyView.Visibility == Visibility.Visible) SetDropHighlight(true);
            e.Handled = true;
        }

        private void SetDropHighlight(bool on)
        {
            DropBorder.Stroke = on ? (Brush)FindResource("Accent2") : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44));
            DropBorder.Fill = on ? new SolidColorBrush(Color.FromArgb(0x14, 0xC8, 0x10, 0x2E)) : new SolidColorBrush(Color.FromArgb(0x99, 0x1B, 0x1B, 0x1F));
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            if (Busy.Visibility == Visibility.Visible) return;
            // on the loaded-mods screen, dropping more adds them to the list
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                await OpenMods(paths, PlanView.Visibility == Visibility.Visible);
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

        private void ClearPlans()
        {
            foreach (var p in _plans) Core.Cleanup(p);
            _plans.Clear();
        }

        private async Task OpenMods(IEnumerable<string> inputs, bool append)
        {
            var list = inputs.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0) return;
            if (!append) ClearPlans();
            var failed = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                string input = list[i];
                if (_plans.Any(p => string.Equals(p.Input, input, StringComparison.OrdinalIgnoreCase))) continue; // already loaded
                SetBusy(true, list.Count > 1 ? $"Reading mod {i + 1} of {list.Count}..." : "Reading the mod... (mods without an image get a picture drawn, a few seconds)");
                // the folders known now (found through Steam the first time); the mod's game decides which one is used
                var dirs = Games.ToDictionary(g => g, GameDir);
                try { _plans.Add(await Task.Run(() => Core.Analyze(input, g => dirs.TryGetValue(g, out var d) ? d : null))); }
                catch (GameFolderMissingException gm)
                {
                    // a mod of a game whose folder is unknown: ask for that folder and read the mod again
                    SetBusy(false);
                    string name = Core.GameNames[gm.Game];
                    bool pick = ConfirmDialog.Show(this, name + " not found",
                        $"{Path.GetFileName(input.TrimEnd('\\', '/'))} is a {name} mod.",
                        $"The converter reads some original files from the game. Choose the {name} folder (the one with {gm.Game}.exe).",
                        "Choose folder", "Skip this mod");
                    if (pick && ChooseGame(gm.Game)) { i--; continue; }
                    failed.Add(Path.GetFileName(input.TrimEnd('\\', '/')) + ": " + gm.Message);
                }
                catch (Exception ex) { failed.Add(Path.GetFileName(input.TrimEnd('\\', '/')) + ": " + ex.Message); }
            }
            SetBusy(false);
            if (_plans.Count > 0) { RenderPlans(); Show(PlanView); }
            else Show(EmptyView);
            if (failed.Count > 0) ShowToast((failed.Count == 1 ? "Could not read this mod:\n" : "Could not read these mods:\n") + string.Join("\n", failed));
        }

        private void RenderPlans()
        {
            var vms = _plans.Select(p => new ModVM(p, _plans.Count > 1)).ToList();
            Mods.ItemsSource = vms;
            int outfits = vms.Sum(v => v.OutfitCount);
            ModsTitle.Text = _plans.Count == 1 ? "1 mod" : _plans.Count + " mods";
            Summary.Text = outfits + (outfits == 1 ? " outfit" : " outfits") + (_plans.Count > 1 ? " in " + _plans.Count + " mods" : " found");
            bool allArchives = _plans.All(p => p.IsArchive);
            SaveOverButton.IsEnabled = allArchives;
            SaveOverButton.ToolTip = allArchives ? "Replace the original files with the converted ones" : "Only when every mod is a .zip / .rar / .7z file";
            SaveNewButton.Content = _plans.Count > 1 ? "Save as new mods" : "Save as new mod";
        }

        private void RemoveMod_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ModVM vm)
            {
                Core.Cleanup(vm.Plan);
                _plans.Remove(vm.Plan);
                if (_plans.Count == 0) Show(EmptyView); else RenderPlans();
            }
        }

        private void ChoosePicture_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is ModVM vm)) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Picture for the game's costume menu",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (dlg.ShowDialog(this) != true) return;
            if (Tex.LoadRgba(dlg.FileName, 64, out _, out _) == null) { ShowToast("This image could not be read."); return; }
            vm.Plan.CustomPicture = dlg.FileName;
            RenderPlans();
        }

        private void DefaultPicture_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ModVM vm) { vm.Plan.CustomPicture = null; RenderPlans(); }
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

        private async void SaveNew_Click(object sender, RoutedEventArgs e)
        {
            if (_plans.Count == 0) return;
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
            if (_plans.Count == 0 || !_plans.All(p => p.IsArchive)) return;
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
            if (notes.Count > 0) ShowToast(string.Join("\n\n", notes));
            if (_plans.Count > 0) RenderPlans(); // the ones that failed stay loaded
            Show(DoneView);
        }

        private void ShowFile_Click(object sender, RoutedEventArgs e)
        {
            string first = _lastOutputs.FirstOrDefault(File.Exists);
            if (first != null) Process.Start("explorer.exe", "/select,\"" + first + "\"");
        }

        private void Another_Click(object sender, RoutedEventArgs e) => Show(_plans.Count > 0 ? PlanView : EmptyView);
    }
}
