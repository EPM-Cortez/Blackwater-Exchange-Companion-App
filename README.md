# Blackwater Exchange app

The Windows companion to [blackwater.exchange](https://blackwater.exchange) runs in the notification area. It keeps
auction prices available in WoW Forever and can also upload your gold ledger or manage Forever Craft Assistant.

## Install on Windows

Download the MSI for your PC from [GitHub Releases](https://github.com/EPM-Cortez/Blackwater-Exchange-Companion-App/releases).
Choose `x64` for most Intel and AMD PCs or `arm64` for Windows on Arm. Windows Settings → System → About shows your
system type. The installer includes the .NET 10 Desktop Runtime, installs in Program Files and adds a Start menu
shortcut. Windows may request administrator approval for installation. Open the app from the Start menu afterward.

The preview MSI has no Authenticode signature. Windows or your organisation's policy may warn or block it.
Compare its SHA-256 with the release's `SHA256SUMS.txt` to check that you downloaded the published file.

```powershell
Get-FileHash .\BlackwaterExchange-0.1.1-x64.msi -Algorithm SHA256
```

Price sync requires a Forever Craft Assistant version that reads `ForeverCraftAssistantMarketData`. The companion
was tested with addon version 1.7.4. The public CurseForge 1.3.2 package predates that reader, and Blackwater's addon
download endpoint returned HTTP 404 during release verification. Preview testers need a compatible addon installed
separately.

Select your WoW Forever `_forever_` folder if the app has not found it. After Forever Craft Assistant has saved a
character's realm, faction and region, the app can find the right auction houses and write fresh prices. Use **Update
now** in the desktop app, then `/reload` in WoW to load the new data into Forever Craft Assistant. Price sync works
without a Blackwater account. If you use **Start with Windows**, turn it off in the app before uninstalling. The
Windows uninstall entry is **Blackwater Exchange**. App settings, your website account and your WoW addon files are
managed separately from the MSI.

## What the app does

- **uploads the gold ledger**: Forever Craft Assistant's saved file, after WoW saves it;
- **keeps addon prices fresh**: writes the `ForeverCraftAssistantMarketData` addon with every house the player's
  characters trade on;
- **manages Forever Craft Assistant**: use **Check now** to install a release or enable automatic updates in Settings;
- **can start with Windows** in the notification area if you enable the option.

.NET 10 and nothing else: no NuGet packages in the app (the test project uses xUnit, as `AuctionMarket.Tests`
does; nothing from it ships).

| Project | What it is |
| --- | --- |
| `src/Blackwater.Core` | The engine. No UI |
| `src/Blackwater.Cli` | `blackwater.exe`, a developer tool that drives the engine without a window |
| `tests/Blackwater.Core.Tests` | Unit tests with a fake website |
| `src/Blackwater.App` | `BlackwaterExchange.exe`, the WPF window and tray icon |

## How it works

| File | Job |
| --- | --- |
| `GameFolder.cs` | Finds WoW Forever: the launcher's registry entry (`HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft`, `InstallPath`), then `World of Warcraft` folders on each fixed drive. Lists `WTF\Account\*\SavedVariables\ForeverCraftAssistant.lua`, reads the installed addon version, and says whether `Wow.exe` is running from that folder |
| `ApiClient.cs` | HTTPS API (loopback HTTP for testing). Sends `User-Agent: BlackwaterExchangeApp/<version>` and `X-App-Version`. API requests reject redirects and send credentials only for their supported operations |
| `DeviceKey.cs` | The key and issuing origin, encrypted with Windows DPAPI for the current user (`device.key`) |
| `LedgerUploader.cs` | After linking and enabling uploads, sends each changed `ForeverCraftAssistant.lua` in full for game accounts under the selected WoW installation. Opens files with sharing, checks their actual path and reads at most 16 MiB per file. A file the website refuses is retried when the game writes a new one |
| `PriceUpdater.cs` | Works out the houses from each character's realm (ruleset from the name's end), faction and region, plus each ruleset's Blackwater house. Asks `/v1/markets` for scan times and downloads `/v1/markets/{house}/addon` only for houses with a newer scan from the last six hours. `MarketDataWriter` checks every value and writes schema 2 `MarketData.lua` atomically |
| `AddonInstaller.cs` | Compares `/v1/addon/latest` with the installed `.toc` version. Downloads only from the same server, checks size and SHA-256, and unpacks only entries under `ForeverCraftAssistant/` (no `..`, drive letters, more than 2,000 files or 128 MB). Unpacks to a hidden folder beside the addon and swaps it in with two renames, putting the old copy back if that fails |
| `Engine.cs` | Runs the jobs one at a time: uploads a few seconds after WoW saves (on logout, exit or any UI reload, including the addon's Sync button; a `FileSystemWatcher` on `WTF\Account`) and every 10 minutes, prices every 15 minutes, addon every 6 hours. Reports `EngineStatus` and `Notify` events for the window. A key removed on the website clears it and asks the player to link again |
| `AppStorage.cs`, `FileLog.cs` | `settings.json`, `state.json` and `app.log` in `%LOCALAPPDATA%\Blackwater Exchange` |

Linking uses the website's device flow. The app shows a code
and opens `/link?code=…`, the player approves it there, and the app collects its key.

For ledger uploads the website receives the whole SavedVariables file, which can contain addon settings and caches
in addition to ledger fields. Use the upload switch to control this transfer. Removing a device on the website
revokes its key. Read the [Blackwater Exchange privacy policy](https://blackwater.exchange/privacy).

## The app

`src/Blackwater.App` is WPF, with Windows Forms only for the tray icon (both part of .NET's Windows Desktop
runtime). One window, in the website's colours:

- **Account**: Link this PC (shows the code and opens `/link?code=…`), or who it is linked to.
- **Gold ledger uploads**, **Auction prices in the addon**, **Forever Craft Assistant**: each job's last result,
  with Upload now, Update now and Check now.
- **Settings**: the WoW Forever folder (Change… accepts `World of Warcraft` or its `_forever_` folder), a switch
  for each job, Start with Windows, and this PC's name on the website.
- **Footer**: Website, Activity log (`app.log`), Unlink this PC.

Closing the window hides it; the tray icon's menu has Open, Upload my ledger now, Update prices now, Open my
ledger on the website and Quit. Windows shows the app's notifications (an addon update, the key being removed)
as toasts. Only pages on the configured server are ever opened.

One copy runs per Windows user (`Local\BlackwaterExchangeApp`); starting it again opens the running copy's window.
Started at sign-in, it stays in the tray unless it needs the player (not linked, game not found).

Start with Windows: installed from the Store, the package's start-up task starts it (switched in Windows Settings,
Apps, Startup; the app links there). Run outside a package, the switch writes
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--background`. A package counts as started at
sign-in when Explorer started in the last three minutes, since the start-up task passes no arguments.

Development switches: `--data FOLDER` (another app folder), `--background`, and
`--preview FILE.png [--preview-linking]`, which runs each job once, draws the window off screen to a PNG and exits.

## Try it

```powershell
dotnet build src/Blackwater.Cli
$cli = '.\src\Blackwater.Cli\bin\Debug\net10.0-windows\blackwater.exe'
& $cli find                                    # where WoW Forever is
& $cli --server http://127.0.0.1:5180 --data .\scratch link   # then approve the code on /link
& $cli --data .\scratch upload
& $cli --data .\scratch prices
& $cli --data .\scratch addon
& $cli --data .\scratch run                    # the background loop, Ctrl+C to stop
```

`--server` requires a separate `--data` folder. Use `--game` with a scratch `_forever_` folder to keep developer
commands away from the real game. The app saves `--server` and `--game` in that data folder's `settings.json`.

## Build the GitHub MSI

Use the Windows .NET 10 SDK and WiX 6. WiX is packaging tooling. The app projects have no NuGet dependencies.

```powershell
dotnet tool install wix --version 6.0.2 --tool-path .\artifacts\tools
.\packaging\Build-Msi.ps1 -Wix .\artifacts\tools\wix.exe
```

The script publishes self-contained x64 and Arm64 packages to `artifacts/msi` with `SHA256SUMS.txt`. It stages each
build separately. The MSI registers files and a Start menu shortcut using Windows Installer. The app runs as the
current Windows user. Source for the MSI is [`packaging/BlackwaterExchange.wxs`](packaging/BlackwaterExchange.wxs).

## Security review

The desktop source review covered device linking, API transport, SavedVariables, Lua generation, addon archives and
packaging. The preview binds credentials to their issuing origin, refuses API redirects, limits response and file
sizes, checks managed game paths, and preserves backups when addon activation fails. The addon ZIP must match the size
and SHA-256 supplied by the configured service. The service provides both the manifest and the ZIP, so trust in that
service is part of the addon update model. Automatic addon replacement is off on fresh profiles.

## Package for the Microsoft Store

`packaging/Build-Package.ps1` publishes the app self-contained for x64 and Arm64 (the Store does not install .NET;
about 66 MB per architecture with English resources only), copies the logos, fills in
`packaging/AppxManifest.template.xml`, indexes the logos with `makepri` (every scale in one `resources.pri`) and
packs each `.msix` and the `.msixbundle` with `makeappx` from the Windows SDK. Output: `artifacts/package`.

```powershell
.\packaging\Build-Package.ps1                                   # test identity
.\packaging\Build-Package.ps1 -IdentityName '<Package/Identity/Name>' -Publisher '<Package/Identity/Publisher>' `
    -PublisherDisplayName '<Package/Properties/PublisherDisplayName>'   # from Partner Center, Product identity
```

The manifest declares `runFullTrust` (a desktop app reading the game's saved files and writing its AddOns folder),
`internetClient`, and a `windows.startupTask` that is on by default once the app has been opened. The version is
`Blackwater.App.csproj`'s `<Version>` plus `.0`; raise it for every Store submission.

To try the package on this PC before submitting, switch on Developer Mode (Settings, System, For developers), then
`.\packaging\Build-Package.ps1 -Architectures x64 -Register`. It installs the unsigned x64 layout for your Windows
user; remove it with `Get-AppxPackage BlackwaterExchange.Test | Remove-AppxPackage`. The Windows App Certification
Kit (`C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe`, run as administrator) checks a
package the way the Store does.

Logos and `app.ico` are drawn from the logo in `App.xaml`: `BlackwaterExchange.exe --assets src\Blackwater.App\Assets`.
Store screenshots and the listing text are in `store/` (`--store-shots store\screenshots` redraws them).

## Test

```powershell
dotnet test tests/Blackwater.Core.Tests
```
