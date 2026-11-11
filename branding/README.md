# Branding files

The files here are Wisko's logo and the icons made from it. They are reserved branding, not covered by the Apache
License: see [BRANDING.md](../BRANDING.md).

Every file here is made from the golden W artwork, `Source/Wisko-GoldenW-original.png` (below).

| File | Used for |
|---|---|
| `Source/Wisko-GoldenW-original.png` | the artwork itself, unchanged: the golden angular W on black, 568 x 617 px |
| `Master/Wisko-Master-Transparent.png` | the logo itself, transparent, at the artwork's own resolution |
| `AppIcon/Wisko-AppIcon-1024.png` | the 1024 px icon (embedded in the editor; the macOS app icon is made from it) |
| `AppIcon/Wisko-AppIcon.ico` | `Wisko.exe` (Explorer, taskbar, shortcuts, the Apps list) and the installer |
| `AppIcon/Wisko-AppIcon.icns` | macOS icon file (16 to 1024 px) |
| `Tiny/Wisko-TinyMark-16.png`, `-32`, `-64` | the editor window's icon (title bar, taskbar button) |
| `Tiny/Wisko-TinyMark-512.png` | a large window-icon size |
| `Installer/Wisko-WizardImage-<w>x<h>.png` | the installer's large wizard image (its Finished page), 202 x 386 to 534 x 1022 px |
| `Installer/Wisko-WizardSmallImage-<n>.png` | the installer's small wizard image (top right of its other pages), 58 to 159 px |
| `manifest.json` | which artwork and settings made the files, with checksums (written by the tool) |

## The golden W

The artwork is a screenshot (`Source/`, kept byte for byte; SHA-256
`359420843fddb30e0d2bb67ad2b244a501cd14b8b2513383d973d5446f507dea`). The files were made with:

```powershell
dotnet run --project tools/Wisko.BrandingAssets -- generate branding/Source/Wisko-GoldenW-original.png --out branding --allow-upscale
```

- The black screenshot background is removed. The black gap between the W's right stroke and its extruded panel is part
  of the design: it is enclosed by the logo, so it stays opaque black (no `--remove-enclosed`).
- The logo is 453 x 377 px in the artwork, so the 512 px and 1024 px sizes (`Tiny/…-512`, `AppIcon/…-1024`, the 512 and
  1024 px .icns entries) are upscaled and slightly soft; accepted for 0.1.0. Every other size is made smaller than the
  artwork. A larger original would make them sharp: replace `Source/` and run the command again.
- The wizard images show the logo as the artwork does, on its black. The installer's pages are white, and the light W
  would barely show on them.
- On light backgrounds (Explorer and the Start menu in light mode) the transparent icons are a pale W with little
  contrast; on dark and mid-grey backgrounds they read clearly. That is the logo as drawn.

## Making the files from the artwork

`tools/Wisko.BrandingAssets` makes every file from one image of the logo. It never redraws, recolours or reshapes
the logo: it separates it from its background, crops it to its own bounds and scales it uniformly onto transparent
squares.

```powershell
dotnet run --project tools/Wisko.BrandingAssets -- inspect  <artwork.png>
dotnet run --project tools/Wisko.BrandingAssets -- generate <artwork.png> --out branding
dotnet run --project tools/Wisko.BrandingAssets -- validate branding --sheet <contact-sheet.png>
```

1. **inspect** first. It reports the image's size, whether it has its own transparency, the background it found, the
   logo's size, and whether the logo touches the image's edge. A logo smaller than 1024 px across is reported as a
   screenshot or a thumbnail: the larger icons would be blurred, and **generate** refuses unless `--allow-upscale` is
   given. A border that is not one colour is reported too (then `--crop x,y,w,h` to the logo, or
   `--background #RRGGBB`).
2. **generate** writes the files listed above.
   - A transparent source is used as it is.
   - On a solid background (a screenshot), the background is removed by colour. At the logo's edge, each partly
     covered pixel gets the logo's own colour at the right transparency, so no light or dark halo of the old
     background remains. Every pixel inside the logo keeps its exact colour.
   - Background-coloured areas the logo encloses are kept (they may be part of the artwork); `--remove-enclosed`
     makes them transparent.
   - Scaling averages in premultiplied alpha (no colour bleeding at the edges, no sharpening). Every icon size is made
     straight from the logo. The .ico holds 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px; the .icns holds every
     macOS icon type from 16 to 1024 px.
   - `--padding` sets the space around the logo in the icons (default 0.04 of each side).
   - The installer's wizard images: the logo centred on the artwork's own background colour (on transparency for a
     transparent source), one file for each display scaling from 100% to 250%, so Inno Setup picks an exact fit.
3. **validate** checks sizes, transparent corners, halos (edge pixels much darker than the darkest, or much lighter
   than the lightest, logo pixels next to them) and the wizard images (sizes, the logo clear of the edge). It writes a
   contact sheet of the small sizes, at real size and enlarged, on light, grey and dark backgrounds. Look at it: the
   small sizes must still read as the logo.

The same artwork and options always give byte-identical files. The tool's behaviour is covered by the headless tests
(`Branding:` section, on synthetic test shapes).

## Where the branding is used

| Place | What |
|---|---|
| `Game/Wisko.Editor.csproj` `ApplicationIcon` | `AppIcon/Wisko-AppIcon.ico`, the icon inside `Wisko.exe` |
| `Game/Wisko.Editor.csproj` embedded resources and `Game/BrandingIcon.cs` | `Tiny/…-16/32/64.png` and `AppIcon/…-1024.png`, the window icon |
| `scripts/build-macos-app.sh` `ICON_SOURCE` | `AppIcon/Wisko-AppIcon-1024.png`, from which the app bundle's `Wisko.icns` is made |
| `packaging/windows/Wisko.iss` (`/DIconFile`) | `AppIcon/Wisko-AppIcon.ico`, the setup program's icon |
| `packaging/windows/Wisko.iss` (`/DWizardImages`, `/DWizardSmallImages`) | `Installer/…`, the setup wizard's images |

After new files are made, rebuild (`scripts/build-windows-app.ps1`, `scripts/build-macos-app.sh`) and check the
icon in Explorer, the taskbar, a shortcut, the title bar, the installer and its wizard pages, and the Dock.
