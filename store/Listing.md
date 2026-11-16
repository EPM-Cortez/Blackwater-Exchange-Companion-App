# Microsoft Store listing: Blackwater Exchange

Everything Partner Center asks for, ready to paste. The listing language is **English (United Kingdom)**, matching
the package's `en-GB` resource. Customers in other markets see this listing too.

## Product

| Field | Value |
| --- | --- |
| Name to reserve | Blackwater Exchange |
| Category | Utilities & tools |
| Price | Free |
| Markets | All |
| Package | `artifacts/package/BlackwaterExchange_<version>.msixbundle` (x64 and Arm64) |
| Privacy policy URL | https://blackwater.exchange/privacy |
| Website | https://blackwater.exchange |
| Support contact | Fill in: an email address or support page you are happy to publish |
| Copyright | © 2026 (your company's legal name) |

## Description

Blackwater Exchange is a Windows app for WoW Forever players who use the Forever Craft Assistant addon and the
blackwater.exchange website. It runs in the notification area and does three things:

- Uploads the gold ledger the addon keeps (auction sales and purchases, mail, vendors, quests and trades) to your
  blackwater.exchange account, where the Ledger page shows it by day, source, item and character.
- Writes current auction prices from blackwater.exchange into the addon for the auction houses your characters use,
  so item tooltips show them without a full scan.
- Installs Forever Craft Assistant and keeps it on the latest version.

WoW only saves addon data when you log out, exit or reload the interface, and only reads new data at those times.
While you play, press Sync in the addon's Ledger tab (or type /reload) and the app uploads your latest ledger a few
seconds later; the same reload loads the newest prices.

To link the app to your account, click Link this PC, sign in with Discord on blackwater.exchange and approve the code
the app shows. The app keeps its own key, encrypted for your Windows user, and you can remove it from your account
page at any time.

The app reads the addon's saved file and writes only to Forever Craft Assistant's folders in WoW Forever's AddOns
folder. It does not change the game.

Blackwater Exchange is a fan project and is not affiliated with or endorsed by Blizzard Entertainment. World of
Warcraft and WoW are trademarks of Blizzard Entertainment, Inc.

## What's new in this version

First release.

## Product features (one per line)

- Uploads your Forever Craft Assistant gold ledger to blackwater.exchange
- Uploads a few seconds after you press Sync in the addon or type /reload, without closing WoW
- Writes auction prices into the addon for each auction house you use
- Installs Forever Craft Assistant and keeps it updated
- Links to your account with a code; the app never asks for a password
- Runs in the notification area and starts with Windows

## Search terms (up to 7)

WoW Forever, Forever Craft Assistant, gold ledger, auction house, addon updater, crafting, Blackwater

## Screenshots (Desktop, 1920 × 1080)

| File | Caption |
| --- | --- |
| `screenshots/1-overview.png` | The main window |
| `screenshots/2-linking.png` | Linking the app to an account |

They show the window alone, with sample data. Rebuild them with
`BlackwaterExchange.exe --store-shots store\screenshots` after changing the window.

## Store logos

Optional in Partner Center; the package's own logos (`src/Blackwater.App/Assets`) are used otherwise. Upload
`src/Blackwater.App/Assets/StoreLogo.scale-400.png` (200 × 200) or a 300 × 300 render if Partner Center asks for a
square logo.

## System requirements

Windows 10 version 1809 or later, x64 or Arm64. WoW Forever (from the Battle.net launcher) with Forever Craft
Assistant for the ledger and prices. An account on blackwater.exchange (sign in with Discord) for uploads.

## Age rating questionnaire

Answer as a utility with no violence, no user-to-user communication in the app, no purchases, and no sharing of
location. It links to a website where players sign in with Discord. Expected result: suitable for all ages
(PEGI 3 / ESRB Everyone).

## Restricted capability: runFullTrust

Partner Center asks why the app needs it. Paste:

> Blackwater Exchange is a WPF desktop app. It needs full trust to read the Forever Craft Assistant addon's saved
> file in the user's World of Warcraft folder (WTF\Account\…\SavedVariables) and to install and update that addon in
> the game's Interface\AddOns folder, wherever the user installed the game. It does not run elevated and writes
> nowhere else outside its own app data.

## Notes for certification

> The app is a companion for the game WoW Forever (Battle.net). Without the game installed, the window opens and
> says "WoW Forever was not found" with a Change… button; this is expected. To test linking without the game: click
> Link this PC; the browser opens https://blackwater.exchange/link with the code filled in; sign in with any Discord
> account and approve; the app shows "Linked to <name>". Closing the window keeps the app in the notification area;
> Quit is in the tray icon's menu. The app starts with Windows through its startup task, which can be switched off
> in Settings, Apps, Startup.

The website must be deployed with device linking (`/link`, `/api/devices`) and addon releases
(`/v1/addon/latest`) before submitting, or certification testers cannot link.
