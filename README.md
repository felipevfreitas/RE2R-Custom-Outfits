# RE2R Custom Outfits

Wear skin mods for **Resident Evil 2 Remake** without replacing any costume.

Skin mods normally put their files on top of a costume's files, so they replace that costume, and two mods for the same costume can't be installed together. Custom Outfits moves each skin to its own folder and applies it in game with a REFramework script. Nothing is replaced, and skins made for DLC costumes can be worn over the costumes you already have.

This repository has two parts:

| Part | What it is |
|---|---|
| [`mod/`](mod/Custom%20Outfits) | The mod: a REFramework Lua script plus tables of the game's costumes. |
| [`converter-cs/`](converter-cs) | **Custom Outfits Converter**, a Windows program that converts skin mods into Custom Outfits packages for Fluffy Mod Manager. |

Downloads are on the [Releases](../../releases) page.

## Features

- Install as many skins as you want, even several for the same costume.
- Pick **Body**, **Face** and **Hair** per character from the REFramework menu.
- **Body - Normal / Body - Alternative**: a skin for each costume family. Skins follow the story (dirt, damage, jacket on/off) when the mod includes those versions.
- **Specific costume**: lock a skin to one costume of the game's menu, with the face and hair that go with it.
- Applied before the character appears (no flash of the original costume).
- Works on NPCs (Claire in Leon's campaign and vice versa) and on Ada, Sherry, Hunk, Tofu, Robert Kendo, Katherine and the Forgotten Soldier.
- Supports both builds of the game: DX11 (non-RT) and DX12 (RT).

## Installing the mod

1. Install [REFramework](https://github.com/praydog/REFramework).
2. Install `Custom Outfits.zip` with Fluffy Mod Manager (or extract its `reframework` folder into the game folder).
3. Convert your skin mods with the converter and install the converted packages with Fluffy.
4. Disable the original (unconverted) version of each skin you converted.

In game, press **Insert** to open the REFramework menu. The **Custom Outfits** window opens with it.

## Using the converter

Run `RE2R-Custom-Outfits-Converter.exe` (no install needed; .NET Framework 4.8 is already part of Windows 10/11).

1. Drag one or more skin mods (`.zip`, `.rar`, `.7z` or a folder) onto the window.
2. Check the outfit names.
3. Save. Each mod becomes a `.zip` ready for Fluffy Mod Manager.

The converter needs the game installed: it reads some original files (materials a mod doesn't ship, the story costume tables) from the game's `.pak` files. The game folder is found automatically through Steam; you can pick it by hand if needed.

### What the converter does

- Copies the mod's meshes, textures and physics files to `natives/x64/custom_outfits/<character>/<outfit>/`, keeping each file's version (non-RT and RT files are both kept).
- Rewrites the texture paths inside each `.mdf2` material so they point to the new folder.
- Reads the game's costume prefabs to know which story stage each file belongs to.
- Writes one JSON per outfit part in `reframework/data/CustomOutfits/outfits/`, which the script reads.
- Writes `modinfo.ini` (name, author, version, screenshot, Fluffy category) and the final `.zip`.

The original mod is never modified unless you choose **Save over original**.

## Building from source

Requirements: Windows and the [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer). The program targets .NET Framework 4.8; the reference assemblies come from NuGet, so no extra targeting pack is needed.

```
git clone https://github.com/felipevfreitas/RE2R-Custom-Outfits.git
cd RE2R-Custom-Outfits/converter-cs/App
dotnet build -c Release
```

The program is created at `converter-cs/App/bin/Release/net48/RE2R-Custom-Outfits-Converter.exe`. It is a single file: [Costura.Fody](https://github.com/Fody/Costura) embeds the libraries into the `.exe`.

`converter-cs/Cli` is a small command-line version used for testing (`co-cli analyze|convert|build`).

### Libraries

- [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.7: reading `.zip`, `.rar` and `.7z`, and zstd decompression for the game's `.pak` files.
- [Costura.Fody](https://github.com/Fody/Costura) / [Fody](https://github.com/Fody/Fody): single-file `.exe`.

## Credits

- **praydog** for [REFramework](https://github.com/praydog/REFramework).
- **FluffyQuack** for Fluffy Mod Manager.
- **Kiyushia**, whose [Custom Outfit System](https://www.nexusmods.com/residentevilrequiem/mods/623) for Resident Evil Requiem showed this idea works on the RE Engine.
- Every skin author in the community.

This project doesn't include or unlock any paid content. Resident Evil is a trademark of Capcom; this is an unofficial fan project.

## License

[MIT](LICENSE)
