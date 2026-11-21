# Connecting Printify and Shopify

ArtGander can import your Printify and Shopify products and orders, and keep your listings up to date from ArtGander.
ArtGander is leading for texts, tags and prices, but it never silently overwrites changes made in the shops: you always
see a preview first, and anything changed on both sides is shown so you can choose.

The keys you enter are stored in **this computer's keychain** (Windows Credential Locker / macOS Keychain), never in the
art folder. Enter them once on every computer you use. Which shops belong to the library is stored in the art folder.

## Printify

1. In Printify, open **My profile → Connections → Personal access tokens** and create a token. Give it access to
   shops, products, orders and uploads. (Tokens are valid for a year.)
2. In ArtGander, open **Settings → Shops**, paste the token under Printify and press **Connect**.
3. Pick the Printify shop that is connected to your Shopify store and press **Use this shop**.

## Shopify

Since 2026, Shopify gives API access through an app in the **Shopify Dev Dashboard** that you install on your own store.

1. Open the [Dev Dashboard](https://dev.shopify.com/dashboard) with the account that owns the store, and create an app
   (e.g. "ArtGander"). The app and the store must belong to the same organization.
2. In the app's version settings, select these Admin API access scopes:
   * `read_products`, `write_products`
   * `read_orders` (and `read_all_orders` for orders older than 60 days)
   * `read_inventory`, `write_inventory`, `read_locations`
   * `read_fulfillments`, `write_merchant_managed_fulfillment_orders` (to mark items you ship yourselves as shipped)
   * optional: `read_customers` for customer names on orders
3. Release the version and **install the app** on your store.
4. Copy the **Client ID** and **Client secret** from the app's Settings page.
5. In ArtGander, open **Settings → Shops**, enter your store address (`your-store.myshopify.com`), the Client ID and the
   Client secret, and press **Save and test**.

ArtGander gets a fresh access token automatically (they last 24 hours).

## Importing everything

On the **Shops** page, **Import all products** reads every product in Printify and Shopify and imports them in one go.
The same product is never imported twice:

* A Printify product and the Shopify listing it was published as become one ArtGander product with both links.
  Without that link, a Printify and a Shopify product with the same SKUs (or else the same title) are combined too.
* A product you already have in ArtGander (entered by hand, or from an older ArtGander) with the same SKUs or name is
  joined instead of duplicated. In the first sync after that, every difference between the two is shown as a choice;
  nothing is sent automatically.
* The art piece each product shows is suggested from its title; you can change it later on the product page.

Use **Find products and choose** to pick products one by one instead.

## How syncing stays safe

* **Reading never changes anything.** Finding products and checking orders only reads.
* **Preview first.** Every change to a shop is listed before it's sent, and only what you confirm is sent.
* **Your edits win, outside edits aren't lost.** For every synced field ArtGander remembers the value both sides agreed on
  at the last sync. A field changed only in ArtGander is pushed; a field changed only in the shop is shown so you can take
  it over; a field changed on both sides is a conflict you decide.
* **No double writes.** Printify products are updated in Printify, which then publishes the changes to Shopify.
  ArtGander never writes those fields to Shopify directly. Products you make yourselves are written to Shopify directly.
* **Things ArtGander doesn't manage are never touched:** collections, SEO, product type, theme templates, Printify mockups,
  print areas and shipping profiles.
* **Changed meanwhile?** Right before writing, ArtGander fetches the product again. If it changed since the preview, it's
  skipped and shown for review instead.
* **One computer at a time.** While syncing, ArtGander holds a small lock file (`.artgander/sync.lock`) so a second
  computer on the same art folder waits.
* **Stock** of products you make yourselves is updated with Shopify's compare-and-set, so a sale that happens during
  a sync is never overwritten.
