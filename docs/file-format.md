# Art folder and `.artgander.json` format

ArtGander's own files are **hidden**: `.artgander.json` in every piece folder, its upgrade backups, and the `.artgander` library folder. The leading dot hides them on macOS and Linux, and ArtGander sets the Hidden attribute on Windows (again on every load, because a Mac rewriting the file over the network can drop it). Visible `artgander.json` files from early builds are renamed and hidden automatically. To look at them, enable hidden files in Explorer (View → Show → Hidden items) or Finder (⌘⇧.).

ArtGander treats **the file system as the source of truth**. There is no central database. Everything about an art piece lives in its own folder, so the library can be read, backed up, synced and fixed with ordinary file tools.

## Folder layout

```
{Art folder}/
  .artgander/                            library-level files; always inside the art folder
    library.json                         shared settings (currency, collections, channels, which Printify/Shopify shops)
    products/
      Goose_poster_A3-9e46e898.json      one file per product; links to any art pieces and their files
    orders/
      1001.json                          one file per order: items, payment, tax, production bill, shipping, tracking
    sync/
      state.json                         when orders were last imported
      log/2026-11.log                    what each sync did
    sync.lock                            only while a computer is syncing or upgrading the library
  Goose On The Loose/                    one folder per art piece (the folder name comes from the title)
    .artgander.json                       all details about this piece
    .artgander.json.v1.bak                backup made automatically when the file format was upgraded
    Goose_On_The_Loose-photo.jpg         {Art_Piece_Name}-{version}.{ext}
    Goose_On_The_Loose-master.tif
    Goose_On_The_Loose-transparent.png
    Goose_On_The_Loose-print-a4.pdf
```

### Naming rules

The library may sit on a network share used by both Windows and macOS, so the strictest rules of both are applied everywhere:

| Part | Rule | Example |
|---|---|---|
| Folder | The title, with `< > : " / \ \| ? *`, control characters and `_` replaced by spaces; whitespace collapsed; no leading dot or trailing dot/space; max 80 characters; Windows reserved names (`CON`, `NUL`…) get " Art" appended; Unicode NFC | `Goose: On the Loose?` → `Goose On the Loose` |
| File prefix | The folder name with spaces turned into `_` | `Goose_On_the_Loose` |
| Version | Lowercase letters and digits, other characters become `-` | `Print A4 (v2)` → `print-a4-v2` |
| Extension | Lowercase | `.JPG` → `.jpg` |

Name comparisons ignore case and Unicode normalization, because Windows and most macOS volumes are case-insensitive and macOS SMB clients may write decomposed (NFD) names.

### How the file system leads

* Every subfolder of the art folder is an art piece. Folders without `.artgander.json` show up as "not tracked yet", and one click adds the file. Existing files are never renamed without asking.
* Files present on disk but missing from `.artgander.json` show up as **new files** to track (and optionally rename).
* Entries in `.artgander.json` whose file is gone are flagged as **missing**.
* Tracked files that don't follow the naming convention are flagged with a **Fix name** button.
* Renaming a piece renames its folder, every file that follows the convention, and the product links to those files, all as one operation with rollback.
* Thumbnails are cached in the app's local cache folder, never inside the art folder.

## Art pieces and projects

The library holds two kinds of items, and the `type` in `.artgander.json` says which one a folder is:

* **Art piece** (`"type": "piece"`): one artwork with its files: photo, master, transparent version, prints…
* **Project** (`"type": "project"`): work that belongs together, such as the illustrations for a book. A project folder contains
  project-only files (book text, description, cover…) under the same naming convention, plus **one subfolder per art piece**:

```
Art/
  The Goose Book/
    .artgander.json                    { "type": "project", "pieceOrder": [ids…] }
    The_Goose_Book-book-text.docx
    The_Goose_Book-cover.png           a "cover" file becomes the project's thumbnail
    Goose Wakes Up/
      .artgander.json                  { "type": "piece" }
      Goose_Wakes_Up-master.tif
    Page 12/                           a planned piece: no files yet, status "planned"
      .artgander.json
```

* A piece belongs to at most one project. Projects can't be nested. Subfolders inside a *piece* (e.g. `exports/`) are ignored.
* `pieceOrder` stores the pieces' order (e.g. the page order of a book) by piece id, so renaming a folder doesn't break it.
  Pieces that aren't listed come after, in natural order ("Page 2" before "Page 10").
* **Collections** are about selling and presenting; **projects** are about the work itself. Both pieces and projects can be in collections.

### Smart defaults when you start tracking a folder

* A folder with **at least two subfolders that contain art** (images, psd, kra, procreate…, or tracked pieces) is suggested as a **project**.
  Its art subfolders are tracked as its pieces.
* Any other folder is suggested as an **art piece**.
* You can always choose the other option. A tracked art piece that later gets several art subfolders shows **Convert to project**.

## `.artgander.json`

UTF-8 without BOM, LF line endings, 2-space indentation, camelCase property names, enum values as camelCase strings. Unknown properties are preserved, so an older ArtGander never deletes data that a newer one wrote.

```jsonc
{
  "schemaVersion": 3,
  "type": "piece",                      // piece | project
  "status": "finished",                 // idea | planned | inProgress | finished | published
  "id": "b766106e-…",                   // stable identity; survives renames
  "title": "Goose On The Loose",
  "description": "",
  "tags": ["goose", "watercolor"],
  "collections": ["Birds"],
  "createdOn": "2026-11-20",
  "updatedAt": "2026-11-20T14:43:06-08:00",
  "updatedBy": "STUDIO-PC",              // machine that saved last
  "updatedWith": "ArtGander 0.1.0",
  "artwork": {                           // the physical original
    "medium": "", "dimensions": "", "isDigitalOnly": false,
    "originalStatus": "available",       // available | reserved | sold | notForSale | gifted
    "originalPrice": null, "soldTo": "", "soldOn": null
  },
  "copyright": {
    "owner": "", "year": 2026,
    "status": "unregistered",            // unregistered | pending | registered
    "registrationNumber": "", "registeredOn": null, "office": "", "notes": ""
  },
  "files": [
    { "fileName": "Goose_On_The_Loose-transparent.png", "version": "transparent",
      "kind": "transparent",             // one of the library's file kinds (Settings); defaults: photo, scan, master, edit, transparent, print, mockup, other
      "notes": "", "addedAt": "…" }
  ],
  "notes": "",
  "pieceOrder": []                       // projects only: piece ids in order
}
```

Products and sales are **not** stored in the piece (since format version 3): products have their own files and point to
the pieces they show, so one product can show several pieces and a piece can be in many products.

## `.artgander/products/{name}-{id}.json`

```jsonc
{
  "schemaVersion": 1,
  "id": "9e46e898-…",
  "name": "Goose poster A3",               // the title in the shop
  "description": "<p>Honk</p>",
  "tags": ["goose", "poster"],
  "type": "printOnDemand",                 // printOnDemand (made and shipped by Printify) | selfPrint | handmade | original | digital | other
  "status": "active",                      // draft | active | archived
  "provider": "Printify", "variant": "A3 matte",
  "pieces": ["b766106e-…"],                // the art pieces (or projects) it shows
  "files": [{ "pieceId": "b766106e-…", "fileName": "Goose_On_The_Loose-transparent.png" }],
  "price": 25, "productionCost": 11, "currency": "EUR",   // defaults for variants without their own
  "costBreakdown": [{ "label": "paper", "amount": 1.2 }],
  "editionSize": null, "stock": null,
  "variants": [
    { "key": "101", "title": "A3", "sku": "GP-A3", "price": 25, "cost": 11, "enabled": true, "stock": null,
      "printifyVariantId": 101, "shopifyVariantId": "gid://shopify/ProductVariant/5001" }
  ],
  "listings": [{ "channel": "fair", "url": "", "externalId": "", "price": 25, "status": "active" }],  // other places, by hand
  "includes": [],                          // bundles: [{ "pieceId": "…", "productId": "…", "quantity": 1 }]
  "relations": [],                         // reserved: related products, e.g. { "productId": "…", "kind": "sameDesign" }
  "channels": {
    "printify": { "shopId": 42, "productId": "…", "blueprintId": 282, "printProviderId": 99, "publishPending": false,
                  "remoteUpdatedAt": "…", "lastSyncedAt": "…", "base": { /* the values agreed at the last sync */ } },
    "shopify":  { "productId": "gid://shopify/Product/777", "handle": "goose-poster", "url": "…", "inventoryLocationId": null,
                  "remoteUpdatedAt": "…", "lastSyncedAt": "…",
                  "base": { "title": "…", "description": "…", "tags": [], "status": "active",
                            "variants": [{ "key": "101", "price": 25, "enabled": true, "stock": null }] } }
  },
  "notes": ""
}
```

The `base` is what makes syncing safe: a field that differs from its base was changed on that side since the last sync.
Changed only in ArtGander → sent to the shop. Changed only in the shop → shown so you can take it over. Changed on both
sides → a conflict you decide; skipped conflicts keep their old base, so they come back. See [integrations.md](integrations.md).

**Bundle products**, such as a "Set of 3 prints", list the products they include. When a bundle has no production cost of its own,
"Use their cost" sums the included products' costs.

## `.artgander/orders/{order}.json`

One file per order. The file name is the order number as a label (`#1001` → `1001.json`). Every tax field is optional.
Orders imported from Shopify and Printify are combined: a Printify order that fulfills a Shopify order becomes the same order.

```jsonc
{
  "schemaVersion": 2,
  "id": "…",
  "orderRef": "#1001",
  "date": "2026-11-20",
  "channel": "shopify",
  "status": "shipped",                   // open | paid | shipped | delivered | cancelled | refunded
  "customer": "",
  "currency": "EUR",
  "amountPaid": 30.0,                    // what the customer paid in total (incl. tax and shipping charged); null = sum of items
  "amountPaidTax": 5.0,                  // tax included in amountPaid (optional)
  "productionCost": 8.0,                 // actual production bill, e.g. Printify; null = products' production costs
  "productionTax": 1.6,                  // optional
  "shippingCost": 4.0,                   // what shipping cost you
  "shippingTax": 0.8,                    // optional
  "carrier": "USPS",
  "trackingCode": "9400…",
  "shippedOn": "2026-11-21",
  "notes": "",
  "lines": [
    { "id": "…", "productId": "9e46e898-…", "variantKey": "101", "pieceId": null, "title": "Goose poster - A3", "sku": "GP-A3",
      "quantity": 1, "unitPrice": 25, "unitCost": null, "fees": 0,
      "fulfillment": "printify",           // printify | self (you ship it) | digital
      "notes": "", "shopifyLineId": "gid://shopify/LineItem/1", "printifyLineId": null }
  ],
  "sources": { "shopify": { "id": "gid://shopify/Order/555", "name": "#1001", "updatedAt": "…", "importedAt": "…" },
               "printify": { "id": "…", "name": "#1001", "updatedAt": "…", "importedAt": "…" } },
  "importBase": { "customer": "Honk B.", "amountPaid": "30.00" }   // last imported value per field: your own edits are kept on re-import
}
```

Profit per order = (amount paid − tax in it) − (production + production tax) − (shipping + shipping tax) − fees.

## Working from several computers

* Every save first writes a temporary file in the same folder and then renames it over `.artgander.json`, which is atomic on NTFS, APFS and SMB. No file locks are held.
* Before saving, ArtGander checks whether the file changed since it was opened (by hash). If another computer changed it, you choose between **Load their version** and **Keep mine**. An open piece with no local edits reloads automatically.
* Only per-computer items live outside the art folder: which art folder this computer opens, the thumbnail cache, and the crash log.
* The library view refreshes through file-system notifications plus periodic polling, because notifications don't work reliably for changes made by another computer on a network share.

## Format versions and automatic upgrades

`schemaVersion` records the structure version of each file. When ArtGander opens a file with an older version, it runs the migration steps in `src/ArtGander.Core/Schema/Migrations` one at a time, keeps a backup as `.artgander.json.v{old}.bak`, and writes the upgraded file. A file written by a **newer** ArtGander opens read-only with a message to update the app, so nothing is lost when one computer is updated before the other.

| Version | Changes |
|---|---|
| 1 | First format. |
| 2 | Projects: `type` (piece/project), `status`, `pieceOrder`, bundle `includes`. Upgrading sets `"type": "piece"` and `"status": "finished"`. ArtGander 0.1 opens version-2 files read-only, so update every computer. |
| 3 | Products and orders get their own files. Upgrading moves each piece's `products` to `.artgander/products/` and its `sales` to order lines in `.artgander/orders/` (ids are kept; safe to interrupt and rerun), then removes them from the piece. ArtGander 0.2 opens version-3 files read-only, so update every computer. |

To change the format: bump `CurrentSchema.Version`, add an `IMigration` with `FromVersion` = the previous version, register it in `MigrationCatalog.All`, and add a row to this table.
