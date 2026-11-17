# Camo Connector browser submissions

**Short description**  
Sync Call of Duty camo progress to the MW4 tracker using the local Camo Connector desktop app.

**Full description**  
Connect the Dark Matter Solutions MW4 Camo Tracker to Camo Connector on Windows. Pair the browser with your desktop app to sync supported Multiplayer camo progress and save a local tracker backup. From the tracker backup panel you can restore that backup after confirming the action. The extension updates the tracker from events supplied by the desktop app. The Windows companion app is required.

**Support**  
[GitHub issues](https://github.com/JoshuaRibeiro/camo-connector/issues)

**Privacy policy**  
[Extension privacy policy](extension-privacy.md). After pushing the repository, confirm that the policy URL is public and resolves before entering it in each store dashboard. Use the public repository file URL or a public tracker privacy page containing the same text.

## Build and verify

Run from the repository root in Windows PowerShell or PowerShell 7.

```powershell
./scripts/build-extensions.ps1
node --test tests/bridge.test.cjs tests/extensions.test.cjs
npx --yes web-ext@9 lint --source-dir dist/Firefox --warnings-as-errors
```

Chrome, Edge, and Opera use the same MV3 service worker. Firefox uses an MV3 background script and a stable Gecko ID. All packages contain the same readable source with no remote scripts. The manifest uses `storage` for the pairing key and progress cursors. A content script runs only on the tracker host. The loopback host permission allows the paired bridge to read events, send focus and write or restore local backups. No Native Messaging registration, broad browsing permission or externally connectable web page is required.

The Firefox build declares website data, local bridge authentication and potentially identifying player labels because the backup and bearer key leave the browser for the local desktop process. Explain this local-only transfer in the review notes.

Firefox host permission patterns cannot specify a port. The Firefox manifest therefore grants access to the loopback host, while the shared client sends requests only to port 47150 for all builds.

## Reviewer and acceptance test

For development, use **Load unpacked** on Chrome, Edge, or Opera (`opera://extensions`) and the unpacked Firefox manifest through `about:debugging`. Test with separate clean profiles. The current development companion is the matching repository checkout and needs Windows PowerShell and Node.js. Use the compiled portable companion download for reviewer instructions once it is available. Attach no real user pairing key, sync code, or progress file.

1. Start Camo Connector, choose a writable data folder and click **Start connector**. Click **Copy browser pairing key**.
2. Open the extension popup, enter the key and click **Connect**. Open the tracker in Multiplayer. Use a disposable tracker profile and a supported in-game event to verify sync.
3. Verify that a Multiplayer backup is created. Open the tracker’s **Overlay & sync → Backup** panel and use the extension’s restore control on test data.
4. Stop the app and verify the extension reports it offline. Restart with the same folder and verify reconnection. Restart the browser and repeat. Repeat after updating the extension while keeping the same browser profile.
5. Repeat independently in Chrome, Firefox, Edge, and Opera. Include Opera GX in the Opera test. Record browser versions, permission prompts and any loopback handling differences.

## Listings

| Browser | First submission |
| --- | --- |
| Chrome | Register a publisher in the [Chrome Web Store developer dashboard](https://chrome.google.com/webstore/devconsole). Add a new item, upload `CamoConnector-Chrome-vX.Y.Z.zip`, complete Store listing and Privacy, enter reviewer instructions, and submit for review. [Chrome publishing guide](https://developer.chrome.com/docs/webstore/publish) |
| Firefox | Sign in to the [AMO Developer Hub](https://addons.mozilla.org/developers/), choose **Submit a New Add-on**, then choose listed distribution **On this site**. Upload `CamoConnector-Firefox-vX.Y.Z.zip`, provide the listing, policy and reviewer notes, and submit to Mozilla. AMO handles signing for public distribution. [Mozilla submission guide](https://extensionworkshop.com/documentation/publish/submitting-an-add-on/) |
| Edge | Register for Edge extensions in [Partner Center](https://partner.microsoft.com/dashboard/), create a new extension, upload `CamoConnector-Edge-vX.Y.Z.zip`, complete availability, properties and listing, provide certification notes, and submit. [Edge publishing guide](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/publish-extension) |
| Opera | Sign in to [Opera Add-ons](https://addons.opera.com/en/extensions/), use the Upload Extensions form linked in the publishing guide, and submit the Opera package, listing details, and screenshots for moderation. Opera recommends 612×408 screenshots. [Opera publishing guide](https://help.opera.com/en/extensions/publishing-guidelines/) |

Use the repository’s `icon-128.png` as the source for listing icons. Capture genuine screenshots of the paired popup and the Multiplayer tracker after a successful sync. Use a clean profile and keep the pairing key, tracker sync code and player details out of the captures. For Chrome prepare a 1280×800 screenshot and use the included [440×280 small promotional tile](chrome-promo-440x280.png). Follow the current dashboard specifications for optional graphics and other store-specific sizes. Real screenshots and any listing media beyond the included assets still require capture and review.

## GitHub release

Set the release version in `manifest.json`, push the changes, and create a tag matching the version, for example `git tag extension-v1.9.37` followed by `git push origin extension-v1.9.37`. The workflow builds, validates and attaches the four ZIPs to an extension prerelease. The extension prerelease is excluded from the desktop updater’s latest-release feed.

The workflow publishes GitHub artifacts only. First store submissions are manual after owner approval. Later, use each store’s official API with its store-issued listing ID and repository or environment secrets. Keep store jobs in an approval-protected environment before enabling them. Use the store-distributed packages for public installs and store-managed extension updates.

The tracker site is maintained outside this repository. Copy [the installation links module](tracker-extension-links.mjs) into the tracker's install/help UI. Pass only the approved listing URLs to it. Publish links once the stores make the listings publicly visible.
