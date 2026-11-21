CoD Streaming Tools

Windows app

For the current portable CoDStreamingTools.exe release, choose one download on
GitHub Releases:

- Standard (win-x64-slim.zip): install the x64 .NET 10 Desktop Runtime and
  ASP.NET Core Runtime first from https://dotnet.microsoft.com/download/dotnet/10.0.
  FFmpeg and OCR are bundled for monitors, capture cards, and OBS Virtual Camera.
- Offline (win-x64.zip): includes .NET, OCR and capture support. Use this for
  an offline setup or if you prefer to skip installing the .NET prerequisites.

Extract the ZIP before launching CoDStreamingTools.exe. Both downloads use the
same local tracker, capture controls and OBS overlay. The Tracker view uses
Microsoft Edge WebView2. In-app updates stay on the edition you installed.

Upgrading from Camo Connector

Use Check for updates in the old app. The signed compatibility download
updates in place and keeps Data and the existing shortcut. A subsequent update
installs the renamed executables, and the existing shortcut opens the new app.

Tracking and OBS

Open Auto tracking, choose a monitor, capture card, or OBS Virtual Camera, and
turn on auto tracking. Use the capture preview to check the source. Use an
uncropped 16:9 game feed for OCR.

Open Tracker to view or edit your Multiplayer camo progress. To show your
progress on stream, copy the overlay URL from Tracker into an OBS Browser Source.

Open Trophy Tracker and enable it to track trophies with the same capture
source. The toggle and trophy progress are saved in Data. This feature uses
the app's bundled OCR and Node.js. If Node.js is missing, the app downloads a portable copy on first use. Close the standalone Trophy Tracker
before enabling it here. Its OBS Browser Source URL is still
http://127.0.0.1:47851/overlay.

For desktop updates, open Settings and select Check for updates. Your local
progress and preferences are kept in the app's Data folder.
