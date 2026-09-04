# Multi-Win
small tool for making bootable USB sticks with Windows and Linux. made it because i got tired of rufus and buggy ventoy

pick an OS from the list (or throw in your own .iso), pick a USB stick, hit Flash. that's it.

## what it does

- built-in catalog: Vista / 7 / 8.1 / 10 (20H2–22H2) / 11 (23H2–25H2), direct download links included
- custom ISO support: point it at any .iso and it sniffs *inside* the image to figure out what's in there (`.disk/info`, `install.wim`, bootloaders — not just the file name)
- hardware check, tells you straight up when your machine won't survive win11
- win11 TPM / SecureBoot / RAM bypass through autounattend
- live progress while it works: speed, downloaded/total, ETA, step-by-step log
- windows goes on file-copy style (MBR + FAT32 so it boots on BIOS and UEFI, huge `install.wim` gets split automatically), linux goes on raw (dd-style) so it actually boots

## usage

1. choose image
2. choose usb drive — everything on it gets wiped, you get a warning first
3. flash, wait, done

needs admin rights (it formats disks, duh). runs on win10/11 x64.

## build

you need the .NET 8 SDK.

normal build:

```
dotnet build
```

self-contained single exe (~190MB, runs on a clean windows without .NET installed):

```
dotnet publish -c Release -p:PublishProfile=win-x64-selfcontained
```

output lands in `bin\publish\win-x64\`. keep the `Data` and `Assets` folders next to the exe, it reads the iso catalog and logos from there.

want your own logos in the OS list? drop pngs into `Assets/` — `windows.png`, `kali.png`, `linux.png`, `generic.png`. app icon is `Assets/app.ico`, regenerate it from your png with `Assets/make-icon.ps1`.

## notes / disclaimer

- iso links in `Data/catalog.json` point to archive.org mirrors. check hashes when you can, flash at your own risk.
- the 21H2 in the catalog is an Enterprise Eval (90-day trial), it's labeled as such.
- this is a hobby project, don't blame me if it eats your flash drive. it does warn you first though.
