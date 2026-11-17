MW4 Sunburn Connector

Windows app

Open Launch Camo Connector.cmd in the extracted folder. Choose a monitor or
capture device and check its live preview. Use Start connector to launch the
detector and tunnel. The Logs tab shows detector, tunnel, and capture output
together. Enter an optional clan tag and player name in Setup. Once the
connector runs and the browser extension is active on Multiplayer, Pinned
weapon displays the tracker's current Now Grinding weapon.

Monitors use Windows desktop capture. The capture-device list includes Elgato,
USB capture cards, webcams and virtual cameras exposed as DirectShow video
inputs. Install FFmpeg on PATH or place ffmpeg.exe beside the launcher to use
these inputs. Other apps using a capture device may need to release it first.
For the game's current OCR layout, use an uncropped 16:9 game feed.

Choose your existing data folder (for example D:\CamoGrind) to keep using its
event log and backup. Node.js 18 or later is required for the tunnel. Load the
extracted folder as an unpacked browser extension if it is a new install or
you have moved it. Keep a Multiplayer Armoury tab open while syncing.

App updates

Enter a public GitHub repository as owner/repo on the Updates tab. The app
checks that repository on launch. Use Check for updates to check again and
Update and restart to install an available release in the existing folder.
After the update, reload the unpacked extension in the browser and refresh
the tracker page. The app remembers the selected data folder and settings.

Publish a normal GitHub release with a tag such as v1.9.34 and attach the
matching camo-connector-v1.9.34.zip as a release asset. The archived
camo-connector folder contains the app files. Use a tag and archive name
that match the version in manifest.json.

Manual command-line setup

Requirements: Node.js 18 or later, Opera GX, and the working sunburn.ps1 detector.

1. Extract this ZIP. Put the extracted camo-connector folder beside sunburn.ps1,
   and replace that copy of sunburn.ps1 with the updated copy inside the folder.
2. In Opera GX open opera://extensions, enable Developer mode and choose Load
   unpacked. Select the extracted camo-connector folder.
3. Open or reload https://mw4.darkmattersolutions.uk and select Multiplayer.
   Keep the main Armoury tab open. The connector uses Grind this to follow the
   equipped weapon. It may select All weapons to find a hidden card.
4. Open PowerShell in the folder containing sunburn.ps1. Start the bridge:

   node .\camo-connector\bridge.cjs .\sunburn-events.jsonl

5. A separate PowerShell window in the same folder runs your detector:

   powershell.exe -NoProfile -File .\camo-connector\sunburn.ps1 -Live -Monitor 1 -Log .\sunburn-events.jsonl

The detector holds the last accepted weapon while the HUD is unreadable.
It publishes a weapon after two matching reads from the Armoury roster and
ignores recognised replay HUDs. The roster is embedded in sunburn.ps1.

The tracker footer should show Ready and the current Sunburn count. A new
live detection should add one to the displayed counter. The site's existing
sync sends that change to OBS if the OBS source is already linked.
The connector counts Signal 50 Sunburn longshots and recognises Sang 9mm
Military Camo unlocks. It ticks any missing Military tiers in sequence
through the confirmed tier and checks the tracker after each click.
Keep the in-game CAMOS tracked panel open for two reads. Numeric camo
rows catch up when their weapon, target and objective uniquely match an
editable tracker counter. Weapon-level rows are logged separately.
Older queued Sunburn longshots are covered by the snapshot. For an absolute
snapshot the connector enters one exact value in the matching details row
and verifies that the tracker saved it. Completed rows stay checked. If the
connector cannot open the details automatically, click the weapon name.

Sang 9mm also recognises the completion banners for 25 ADS kills and three
kills without dying five times. The connector matches the tracker row by
its objective and verifies the tick. Preview a saved full-screen frame with
-Image .\frame.png -ChallengeBanner -ShowOcr.
On Camo Select, hovering an unfinished camo also syncs a visible objective
fraction. Prerequisite fractions like Gold's completed specials are ignored.
Completionist and Prestige section counts are informational.
For Hardcore or a replay with no weapon HUD, select Grind this on Sang 9mm
in the tracker before starting the clip. A visible or recently confirmed
in-game weapon takes precedence. -PinnedWeapon remains optional.

Opening the Camo Select progression tab for two scans catches up
Military tiers on Sang 9mm and catalogued weapons. A verified Special 3/3
marks all three named Specials. For partial totals, the reader checks the
three tile padlocks, including their shifted hover positions, and matches
the unlocked slots to special-catalog.json only when the total agrees.
The menu can be previewed with -Image .\frame.png -CamoMenu -ShowOcr.
The catalogue's RGB artwork samples are references for future live banner
matching. Menu catch-up does not depend on those samples.

Live combat tracking logs kills, deaths, the current streak, and the best
streak this session. Pass -Player '<your in-game name>' to label the session.
It uses the fixed KILL and KILLED BY banners, with the numbered kill card
as additional evidence.
Each detected death resets the streak. Space Time gets one kill when the
live kill banner and Sang 9mm Akimbo label are visible together. This camo
sync works without -Player. If the HUD hides the label, use the tracked
camo panel or Camo Select to sync the in-game count.

The bridge checks the log every 100 ms without waiting for the tracker tab.
For live Space Time clicks, the bridge prints capture, detection, log write,
and bridge-read timestamps in UTC. The tracker page's developer console adds
the first browser receipt and confirmed update timestamps to the same event.

For a live replay diagnostic, add -FeedTrace .\feed-trace to the detector
command. It saves up to 240 sets of cropped banner, feed, and weapon images.
The trace.jsonl file records the timestamp and counted events for each sample.
The Military scanner also saves up to 60 corner crops when it sees an unlock header.

Sang 9mm military badge preview (screenshot only):

   powershell.exe -NoProfile -File .\camo-connector\sunburn.ps1 -Image .\badge.png -UnlockBadge -ShowOcr

Use a full screenshot or crop containing UNLOCKED, SANG 9MM, and MILITARY
CAMO followed by its tier number. The preview prints the recognised tier
without writing to the event log or tracker.

Start the bridge before playing. Historical lines are skipped on startup.
Keep one tracker tab open for the best response time. The bridge only listens
on this computer. Stop it or your detector with Ctrl+C.

If the footer reports an unverified click, compare the tracker count to the
game before restarting the bridge. Restarting discards any queued old events.
If the footer keeps asking you to open Multiplayer while the correct row is
visible, the live site's markup may differ from what the connector expects.

Local Multiplayer backup
Restart the bridge after updating. While the tracker is open, the connector
refreshes mw4-camos.latest.json beside sunburn-events.jsonl. If a camo count
or completion drops, it keeps the last saved file. This includes manual
unticks. Overlay & sync > Backup > Restore latest local backup submits the
file using the site's restore control after confirmation. You can also select
mw4-camos.latest.json manually using Restore from file.
