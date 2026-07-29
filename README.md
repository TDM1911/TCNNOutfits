# TCNN Outfits
<img width="1919" height="1079" alt="image" src="https://github.com/user-attachments/assets/b84e7d82-2d6b-4cba-b10f-62b983df4d54" />

A base mod for **Third Crisis: Neon Nights** that lets other mods add custom Spine outfits.

It **builds and registers** outfits. It does **not** decide how the player gets them — that's up
to your mod (shop, NPC, quest, console command, whatever). Multiple mods can add outfits without
conflicting.

Requires [TCModLoader](https://github.com/hashxl/ModLoaderNN).

---

## Two kinds of outfit

**Reskin** — repaint an existing skin's art (same geometry). The simplest path; documented below.

**Custom mesh** — inject your *own* drawn meshes, not just a recolor. This is fully **data-driven**:
no per-outfit code. You build a package with the companion **TCNN Mesh Editor** (draw parts →
auto-fit donor mesh → dilate / drag vertices → preview the idle deform → export), drop the folder
into `assets/`, and the mod loads it automatically. A package is one folder per outfit, with
**one subfolder per surface** (skeleton) — e.g. `portrait/` and `overworld/`:

```
assets/<id>/
├── outfit.json          ← { "id", "name", "base", "cloneBase"?, "icon"?, "description"?, "slots"? }
├── icon.png             ← inventory icon
├── portrait/            ← one surface = one skeleton's worth of art
│   ├── <page>.png       ← packed art page (name is whatever single.json's "page" says)
│   ├── single.json      ← meshes: weighted geometry + page UVs
│   └── hide.json        ← body slots to hide under the outfit   (optional)
└── overworld/           ← the chibi/overworld surface (same shape, its own page)
    ├── <page>.png
    ├── single.json
    └── hide.json
```

The mod injects whichever surface's meshes resolve on a given live skeleton, so one outfit covers
the portrait **and** the overworld chibi. Subfolder names are free — matching is by geometry, not
name. A flat package (`single.json` at the outfit root, single surface) is still accepted, but don't
mix the two: a root `single.json` shadows any subfolders.

Everything under `assets/` that is a package (a `single.json` at its root, or in a subfolder) is
auto-registered at boot. In the console:

```
outfit.meshes            → list registered custom-mesh outfits
outfit.wear <id>         → register + build + give + equip that outfit
```

From another mod, register your own package (id comes from its `outfit.json`):

```csharp
OutfitApi.RegisterMeshOutfit(Path.Combine(manifest.ModPath, "assets", "my_outfit"));
OutfitApi.WearMeshOutfit("my_outfit");   // or hand it out however you like
```

`"slots"` lists the equipment slots the outfit occupies (`EquipmentSlot` names: `Head, Bra,
Underwear, Shirt, Pants, Jacket, MainHand, Offhand, Accessory1, Accessory2, Shoes, NipplePiercing,
Necklace, Tattoo, Gloves, NeckChip`). Occupying a slot displaces whatever's in it, so a full-body
outfit like `["Bra","Underwear","Shirt","Pants","Jacket","Shoes"]` blocks tops/bottoms/coats from
coexisting and clashing with it. Omit to keep the cloned base item's slots.

The bundled `assets/racer/` (the "Neon Racer" bodysuit) is a ready example — `outfit.wear racer`.
See `Core/MeshOutfit.cs` for the generic loader. (Live skeletons are import-scale 0.01, so the
exporter's raw bone-local coords are scaled at load.)

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
outfit.export outfits/default/coat    → exports every surface that uses it
```

You get one folder per outfit, covering all surfaces:

```
exports/outfits_default_coat/
├── PortraitZoey.png                          ← portrait art
├── Zoey_overworld_18.png                     ← overworld/chibi art
├── icon.png                                  ← inventory icon
├── mapping.PortraitZoey_SkeletonData.json    ← region info (reference only)
└── mapping.Zoey_overworld_SkeletonData.json
```

Repaint the PNGs. Keep the **same size and layout** — only change pixels. Page file names must
stay as exported; that's how each skeleton finds its own art.

### 2. Lay out your mod

```
MyOutfitMod/
├── MyOutfitMod.dll
├── manifest.json
└── assets/my_coat/
    ├── PortraitZoey.png        ← portrait (optional)
    ├── Zoey_overworld_18.png   ← overworld/chibi (optional)
    └── icon.png                ← inventory icon (optional)
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

One `Create` call covers **portrait, overworld and icon** — pages are matched by file name, so each
skeleton picks up only the page it uses. Supply just the pages you want to change; anything you
omit keeps the original art.

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
| `outfit.export <skin>` | Export a skin's art for every surface + mapping files |
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
