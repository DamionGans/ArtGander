# Architecture

```
ArtGander.slnx
src/
  ArtGander.Core/        net11.0 library: no UI, all business rules. Reused by every app head.
    Model/               ArtPiece, FileEntry, Product, Listing, Sale… (observable, so UIs bind directly)
    Schema/              JSON settings (source-generated, trim/AOT safe), schema version, migrations
    Storage/             Atomic, network-safe load/save; concurrent-edit detection
    Naming/              Cross-platform name rules and the {Name}-{version}.{ext} convention
    Library/             Folder scanner (pieces and projects with their pieces), piece/project classifier, change watcher, image sizes and raster layout
    Integrations/        Printify and Shopify clients, product/order import, field-by-field sync planner and runner, product creation
    Services/            Operations that touch disk (import, rename, track…) and sales reporting
  ArtGander.App/         .NET MAUI app: native WinUI 3 on Windows, Mac Catalyst (UIKit) on macOS
    Views/ ViewModels/   Pages and MVVM view models (CommunityToolkit.Mvvm)
    Services/            App state, thumbnails, dialogs, IPlatformServices
    Platforms/Windows/   WinUI host, folder picker, Shell thumbnails, drag and drop
    Platforms/MacCatalyst/ App delegate, folder picker, QuickLook thumbnails
tests/
  ArtGander.Core.Tests/  xUnit tests for everything in Core
installers/
  windows/ArtGander.iss  Inno Setup installer
  macos/make-dmg.sh      DMG packaging (+ optional signing/notarization)
.github/workflows/build.yml  CI: tests, Release builds, installers, GitHub Releases on v* tags
```

## Why .NET MAUI

* Real native controls on every platform (WinUI 3 on Windows, UIKit/Mac Catalyst on macOS), native menus and shortcuts.
* One C# codebase that also targets iOS and Android later: add `net11.0-ios;net11.0-android` to `ArtGander.App.csproj` and a `Platforms/iOS` or `Platforms/Android` folder.
* Linux isn't an official MAUI target. Because all logic is in `ArtGander.Core`, a Linux head (e.g. `src/ArtGander.Linux` with GTK) only needs to implement the UI.

## Adding a platform

1. Add the target framework to `ArtGander.App.csproj`, conditioned on the host OS that can build it.
2. Implement `Services/IPlatformServices` in `Platforms/<OS>/PlatformServices.cs` (folder picker, reveal in file manager, thumbnails, drag and drop).
3. Add a job to `.github/workflows/build.yml` and, if needed, a packaging script under `installers/<os>/`.

## Release builds

* Windows: self-contained, unpackaged WinUI, ReadyToRun-compiled, no debug symbols, packed by Inno Setup (per-user install without admin).
* macOS: universal (Apple silicon + Intel) `.app` with the linker on, packed in a DMG. It is ad-hoc signed unless signing secrets are set.

## Testing against sample data

Set the `ARTGANDER_LIBRARY` environment variable to open a specific art folder for one run without changing the saved choice.
