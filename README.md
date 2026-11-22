# ArtGander

*Artistic Goose On The Loose*: a desktop app for artists to manage their art pieces, all the files and edited versions of each piece, products (originals, self-printed copies, print-on-demand), prices, production costs, listings, sales and copyright.

**Your art folder is the database.** Every art piece is a folder with neatly named files and one `.artgander.json` with all its details:

```
Art/
  Goose On The Loose/
    .artgander.json
    Goose_On_The_Loose-photo.jpg
    Goose_On_The_Loose-master.tif
    Goose_On_The_Loose-transparent.png
```

The art folder can live on a network drive and be used from Windows and macOS at the same time. See [docs/file-format.md](docs/file-format.md).

## Install

Download the latest installer from the repository's **Releases** page, or from the artifacts of the latest **Build** run:

* **Windows 10/11:** `ArtGander-Setup-<version>-x64.exe` (or `-arm64`). Windows SmartScreen may warn because the installer isn't code-signed yet: click **More info → Run anyway**.
* **macOS 14+ (Apple silicon and Intel):** open `ArtGander-<version>-macOS.dmg` and drag ArtGander to Applications. The first time, right-click the app and choose **Open** (or allow it under System Settings → Privacy & Security), because it isn't notarized yet.

## Features

* **Art pieces and projects:** a project (e.g. a book) is a folder with its own files plus one folder per art piece, with page order, planned pieces, status, and bundle products; folders are recognized as piece or project automatically
* Library in three views: a raster of images in their own shape (like an image search), large rows, and a sortable details table; search (also inside projects), type, collection and status filters, multi-select to track, add to a collection, make a project or move into one
* Per piece: description, collections, tags, details of the original, notes
* Files: add or drop files, which are copied in under the naming convention; rename a version, which renames the file; detects new, missing and misnamed files
* **Protection against AI training** with [Mistcoat](https://github.com/DamionGans/mistcoat): every image file gets *Protect* (one click, the preset from Settings) and *Options…* (preset, strength, techniques, hardening, format, embedded credit and copyright, or open it in Mistcoat's own window). The protected copy is added to the piece as `{Name}-{version}-protected.{ext}`; the original is never changed. Point ArtGander at Mistcoat in Settings → Image protection (once per computer)
* Products, managed on their own and linked to any art pieces and files: variants, prices, production cost and breakdown, margin, edition, stock, bundles. Each product has its own folder (grouped per art piece under `Products/`) with its own pictures, each optionally showing an art piece or project; those pictures also appear on the piece's page
* **Printify and Shopify**: import all products and orders; keep titles, descriptions, tags, prices and stock in sync from ArtGander with a per-field preview that never silently overwrites shop edits; create new products in Printify (published to Shopify) or, for things you make yourselves, directly in Shopify; mark self-shipped orders as shipped
* Orders: Shopify and Printify orders combined into one; what the customer paid (+ tax), production cost (+ tax), shipping cost (+ tax), delivery provider and tracking code, who ships each item, and an overview with revenue, costs, fees and profit
* Copyright registration tracking
* Safe on network shares: atomic saves, conflict detection between computers, automatic format upgrades with backups

## Develop

Requirements: .NET 11 SDK, plus the MAUI workload (`dotnet workload install maui-windows` on Windows, `maui-maccatalyst` and Xcode on macOS).

```powershell
dotnet test --project tests/ArtGander.Core.Tests
dotnet build src/ArtGander.App
```

Local test build (tests plus the optimized app in `artifacts/local`; add `-Installer` for an installer, which needs Inno Setup 6 on Windows):

```powershell
./scripts/build-local.ps1 -Run
```

Architecture and how to add platforms (iOS, Android, Linux): [docs/architecture.md](docs/architecture.md).

CI runs the tests on every push. The Windows and macOS installers are built only for release tags: push a tag like `v0.2.0` and CI attaches them to a GitHub Release.
