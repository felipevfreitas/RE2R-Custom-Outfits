# Wardrobe Converter

A Windows program that converts **Resident Evil 2 Remake** and **Resident Evil 3 Remake** skin mods so they no longer replace any costume.

Skin mods normally put their files on top of a costume's files, so they replace that costume, and two mods for the same costume can't be installed together. The converter moves a mod's files to a folder of their own and describes them for the **Wardrobe** mod (a REFramework script), which applies them in game. Nothing is replaced, and skins made for DLC costumes can be worn over the costumes you already have.

The Wardrobe mod itself is distributed on Nexus Mods (one page per game). This repository only contains the converter.

Downloads are on the [Releases](../../releases) page.

## Using the converter

Run `Wardrobe-Converter.exe` (no install needed; .NET Framework 4.8 is already part of Windows 10/11).

1. Drag one or more skin mods (`.zip`, `.rar`, `.7z` or a folder) onto the window.
2. Check the outfit names and the picture the outfit gets in the game's Costumes menu (RE2 and RE3). **Choose image…** uses another image; **Use default** goes back.
3. Save. Each mod becomes a `.zip` ready for Fluffy Mod Manager.

The game is recognized from the mod's files. The converter needs that game installed: it reads some original files (materials a mod doesn't ship, the costume tables) from the game's `.pak` files. Only the game the mod is for is needed (you can convert RE3 mods without having RE2, and the reverse). Its folder is found automatically through Steam; if it isn't, the converter asks for it. Both folders can be changed with the "Game folders" links on the start screen.

### What the converter does

- Copies the mod's meshes, materials, textures and physics files to `natives/<platform>/custom_outfits/<character>/<outfit>/`, keeping each file's version (RE2 non-RT and RT files are both kept).
- Rewrites the texture paths inside each `.mdf2` material so they point to the new folder (three material layouts are supported: RE2 non-RT, RE2 RT and RE3).
- Works out which costume, body, face or hair each file belongs to, and the story stage it is used in (dirty, injured, without jacket...).
- Writes one JSON per outfit part in `reframework/data/CustomOutfits/outfits/`, which the script reads.
- Copies new master materials and high-resolution (`streaming/`) textures the mod ships, and warns when a material needs files that neither the mod nor the game has.
- Makes the picture for the game's Costumes menu (`.tex`): the mod's own costume-menu picture, else its image, else one drawn from the outfit itself (a small software renderer: the body, face and hair in a pose made in Blender, with the mod's textures; Claire and Jill for now: Leon and Carlos outfits without an image get a placeholder).
- Writes `modinfo.ini` (name, author, version, screenshot, Fluffy category) and the final `.zip`.

The original mod is never modified unless you choose **Save over original**.

## Building from source

Requirements: Windows and the [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer). The program targets .NET Framework 4.8; the reference assemblies come from NuGet, so no extra targeting pack is needed.

```
git clone https://github.com/felipevfreitas/RE2R-Custom-Outfits.git
cd RE2R-Custom-Outfits/converter-cs/App
dotnet build -c Release
```

The program is created at `converter-cs/App/bin/Release/net48/Wardrobe-Converter.exe`. It is a single file: [Costura.Fody](https://github.com/Fody/Costura) embeds the libraries into the `.exe`.

`converter-cs/Cli` is a small command-line version used for testing (`co-cli analyze|convert|build|re3tables`).

### Code layout

| Path | What it is |
|---|---|
| `converter-cs/Lib/Core.cs` | Opening mods, plans, building the package |
| `converter-cs/Lib/Convert.cs` | RE2 conversion |
| `converter-cs/Lib/Re3.cs` | RE3 costume tables and conversion |
| `converter-cs/Lib/Mdf2.cs` | Reading and rewriting texture paths in `.mdf2` materials |
| `converter-cs/Lib/Pak.cs` | Reading original files from the game's `.pak` archives |
| `converter-cs/Lib/Costumes.cs` | RE2 costume tables from the game's prefabs |
| `converter-cs/App` | The WPF window |

### Libraries

- [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.7: reading `.zip`, `.rar` and `.7z`, and zstd decompression for the game's `.pak` files.
- [Costura.Fody](https://github.com/Fody/Costura) / [Fody](https://github.com/Fody/Fody): single-file `.exe`.

## Credits

- **praydog** for [REFramework](https://github.com/praydog/REFramework).
- **FluffyQuack** for Fluffy Mod Manager.
- **Ekey** for the RE Engine file name lists in [REE.PAK.Tool](https://github.com/Ekey/REE.PAK.Tool).
- **Kiyushia**, whose [Custom Outfit System](https://www.nexusmods.com/residentevilrequiem/mods/623) for Resident Evil Requiem showed this idea works on the RE Engine.

This project doesn't include or unlock any paid content. Resident Evil is a trademark of Capcom; this is an unofficial fan project.

## License

[MIT](LICENSE)
