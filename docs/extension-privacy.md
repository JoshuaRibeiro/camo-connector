# Camo Connector extension privacy policy

Camo Connector synchronises progress between the MW4 Dark Matter Solutions tracker and the Camo Connector desktop app on your computer.

The extension reads the tracker’s displayed weapon and camo progress and its local Multiplayer save. A local backup can contain challenge counts, completion flags, custom tracker values, selected weapons, weapon levels, tracked items, a player label if present, and a save timestamp. The extension sends this information to the desktop app at `http://127.0.0.1:47150` after you explicitly pair. The desktop app stores its latest backup in the configured data folder. The extension reads game events from that same app and updates the tracker through its controls. The tracker website may apply its own syncing and privacy terms.

The browser profile stores the local bridge pairing key and progress cursors using extension-local storage. It sends the pairing key only to the loopback bridge. It does not use browser sync storage. Disconnect from the extension popup to remove the pairing key from that browser. Removing the extension clears its browser storage. Delete the local backup or event logs from the desktop data folder if you also want to remove those files. Deleting the desktop pairing key while the desktop app is stopped makes it issue a new key at the next start.

The extension code sends requests only to the local bridge. It contains no analytics, advertising, remote executable code, or developer-operated service receiving tracker saves. The desktop app separately contacts GitHub when you ask it to check for updates.

For questions or support, use the [Camo Connector issue tracker](https://github.com/JoshuaRibeiro/camo-connector/issues). Leave pairing keys and game or tracker sync codes out of public reports.
