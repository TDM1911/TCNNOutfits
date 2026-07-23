# TCNN Outfits

A base mod for **Third Crisis: Neon Nights** that lets other mods add custom Spine outfits.

It **builds and registers** outfits. It does **not** decide how the player gets them — that's up
to your mod (shop, NPC, quest, console command, whatever). Multiple mods can add outfits without
conflicting.

Requires [TCModLoader](https://github.com/hashxl/ModLoaderNN).

---

## Install

```
<game folder>/
├── BepInEx/plugins/TCModLoader.dll
└── TCNNOutfits/
    ├── TCNNOutfits.dll
    └── manifest.json
```

---

## Making your own outfit mod

### 1. Get the art

Launch the game, open a dialogue so a character is on screen, then in the console:

```
outfit.skins coat                     → find the skin you want, e.g. outfits/default/coat
outfit.export outfits/default/coat    → writes the atlas page + mapping.json
```

Repaint the exported PNG. Keep the **same size and layout** — only change pixels.

### 2. Lay out your mod

```
MyOutfitMod/
├── MyOutfitMod.dll
├── manifest.json
└── assets/my_coat/
    ├── PortraitZoey.png    ← your repainted page (named after the page it replaces)
    └── icon.png            ← inventory icon (optional)
```

`manifest.json`:

```json
{
  "Name": "My Outfit Mod",
  "Author": "you",
  "Version": "1.0.0",
  "UniqueIdentifier": "you.myoutfitmod",
  "PathToDLL": "MyOutfitMod.dll"
}
```

### 3. Write the mod

Reference `TCNNOutfits.dll` and `TCModLoader.dll`, then:

```csharp
using System.Collections.Generic;
using System.IO;
using ANToolkit.Debugging;
using Asuna.Dialogues;
using Modding;
using TCNNOutfits;
using TCNNOutfits.Core;

public class MyOutfitMod : ITCMod
{
    private ModManifest _manifest;

    public void OnModLoaded(ModManifest manifest)
    {
        _manifest = manifest;

        // Mod load order isn't guaranteed, so don't call the API directly here.
        // WhenReady runs now if TCNN Outfits has booted, otherwise as soon as it does.
        OutfitApi.WhenReady(Register);

        ConCommand.Add("mymod.give", args => OutfitApi.GiveToPlayer("my_coat"));
    }

    private void Register()
    {
        OutfitApi.Create(
            baseSkin: "outfits/default/coat",                            // skin to derive from
            id:       "my_coat",                                         // your unique id
            assetFolder: Path.Combine(_manifest.ModPath, "assets", "my_coat"),
            options: new OutfitOptions
            {
                Title       = "My Coat",
                Description = "Added by My Outfit Mod.",
                Icon        = "icon.png",                                // relative to assetFolder
            });
    }

    public void OnModUnLoaded() { }
    public void OnFrame() { }
    public void OnDialogueStarted(Dialogue dialogue) { }
    public void OnLineStarted(DialogueLine line) { }
}
```

That's it. `Create` registers the outfit as a real inventory item; **your** mod decides when the
player receives it (`GiveToPlayer`, a shop, a quest reward, …).

> **Register on every launch.** Saves store items by name and rebuild them from the registry, so
> if your mod doesn't register at load, an item in an existing save can't be resolved. Calling
> `Create` from `WhenReady` (as above) handles this — it's safe to call repeatedly.

---

## API

| Member | Purpose |
|---|---|
| `OutfitApi.WhenReady(Action)` | Run work once this mod has booted (load-order safe) |
| `OutfitApi.Create(baseSkin, id, assetFolder, options)` | Build + register an outfit; returns the `Equipment` |
| `OutfitApi.Get(id)` / `.All` | Look up registered outfits |
| `OutfitApi.OnOutfitCreated` | Event fired per created outfit |
| `OutfitApi.GiveToPlayer(id, equip)` | Convenience — gives the player a copy |
| `OutfitApi.Ready` | Whether the base mod has booted |

`OutfitOptions`: `Title`, `Description`, `Icon`.

---

## Console commands

Arguments are separated by **commas**, not spaces.

| Command | Purpose |
|---|---|
| `outfit.skins [filter]` | List skins on the visible character |
| `outfit.export <skin>` | Export a skin's atlas page(s) + `mapping.json` |
| `outfit.import <skin>` | Reapply edited page art to an existing skin |
| `outfit.create <baseSkin>, <id> [, <assetFolder>]` | Create an outfit without writing a mod |
| `outfit.created` | List registered outfits |
| `outfit.give <id> [, equip]` | Give yourself one |
| `outfit.clearimports` | Revert imported art |

---

## How it works

The game rebuilds a character's skin **by name** every time anything changes (dialogue line,
inventory, scene). Anything applied *after* that gets wiped, so this mod edits the data the game
composes *from* instead:

- **Create** derives a *new* named skin from an existing one and clones the `Equipment` item that
  references it. The original outfit is untouched, so both coexist.
- Only the target skin's attachments are repointed at your texture. The body and other outfits
  keep using the original atlas page, so **outfits from different mods don't clobber each other**.

---

## Building

Set your game path once, then build:

```
dotnet build -c Release -p:GameDir="C:\Path\To\Third Crisis Neon Nights"
```

Or create `GameDir.props` next to the `.csproj` (gitignored):

```xml
<Project>
  <PropertyGroup>
    <GameDir>C:\Path\To\Third Crisis Neon Nights</GameDir>
  </PropertyGroup>
</Project>
```

Requires the game's `Managed` DLLs, BepInEx, and `TCModLoader.dll` (from `BepInEx/plugins`).

> **Note:** mod-to-mod dependencies require TCModLoader to load mod assemblies from a file path
> (not `Assembly.Load(byte[])`) and to run all `OnModLoaded` callbacks after every mod assembly is
> loaded. Without that, a mod depending on this one binds to a second copy of the assembly and
> `OutfitApi` appears uninitialised.
