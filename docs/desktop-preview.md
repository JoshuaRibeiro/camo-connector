# Portable desktop development preview

The preview includes the compiled app, detector, bridge and updater.
It is intended for development until live gameplay, capture hardware
and a signed self-contained release have been verified.

On Windows x64 with the .NET 10 SDK, run the following from the repository root.

```powershell
./scripts/build-desktop-preview.ps1 -OcrDirectory '<Tesseract directory>' -CaptureDirectory '<FFmpeg binary directory>'
```

Run `dist/desktop-preview/CamoConnector.exe` and click **Connect automatically**
in the development browser extension. Approve the first connection on the
desktop. The extension keeps the resulting credential in its browser profile.
In the desktop app, choose a monitor or DirectShow capture device and click
**Start detection**. **Stop** ends capture without disconnecting the browser.
Selecting a source previews it. The detector takes over the source when
detection starts and continues to supply preview frames.
The last source is stored in `Data/capture-source.json`. Preview builds
without OCR can pair and sync with externally supplied events, but cannot
start detection.
The window follows Windows' app colour preference. The **Logs** tab shows
recent detector events and messages, bridge output, and capture diagnostics
together. The detector's event log is retained in `Data`.
The bridge creates a `Data` folder beside the launcher. It follows new records
written to `Data/sunburn-events.jsonl` and stores a local backup there.

The GitHub Actions preview workflow bundles Tesseract, the English OCR model
and FFmpeg in the desktop artifact. The title bar includes the desktop version
and preview run number. The desktop version lives in `desktop/version.txt`
and can change independently of the browser extensions. Branch builds publish
preview artifacts. A matching `vX.Y.Z` tag publishes a verified desktop release.

For a signed update test, pass `-UpdatePublicKeyFile <PEM file>` to the
build script. The build embeds that public key in both programs. Keep the
private signing key outside the repository. Builds without a public key can
recover interrupted replacements but cannot install GitHub updates.

## Detector development

The C# detector development target has desktop capture, saved-frame input,
weapon-name recognition, Military unlock badges, hovered and tracked camo progress and
replay rejection. The detector needs a Windows
x64 Tesseract distribution in the sibling `Ocr` directory, including its
dependent DLLs and `tessdata/eng.traineddata`. Set up a test directory with
`Runtime/Detector/CamoConnector.Detector.exe` and `Runtime/Ocr/tesseract.exe`.
The detector-only CI artifact includes the detector and its runtimes for
command-line testing. For a local build with a trusted Windows x64 OCR runtime,
run the following from the repository:

```powershell
./scripts/build-detector-development.ps1 -OcrDirectory 'C:\Program Files\Tesseract-OCR' -CaptureDirectory '<FFmpeg binary directory>'
```

`CamoConnector.Detector.exe --image frame.png Data` prints a read without
writing tracker data. `CamoConnector.Detector.exe --monitor 0 Data` emits
two-read-confirmed weapon and hovered camo progress events for the selected
desktop monitor. Hovered camo progress uses the camo title for identity.
The tracked panel uses the name where available and can identify known
thumbnails from the existing catalogue. Use a disposable data directory
until live capture is verified. The development detector also reads
combat banners, longshot medals, match notices and Gold completion evidence.
On a development build with FFmpeg in `Runtime/Capture`, run
`CamoConnector.Detector.exe --list-devices` to list DirectShow sources. Use
`CamoConnector.Detector.exe --device "<Input>" Data` with the returned Input
value. The detector normalises the incoming signal to a 1280×720 frame at
up to 15 fps. Use an uncropped 16:9 game feed for the existing OCR regions.
`--video clip.mp4 Data` reads a decoded frame for diagnostics without writing
tracker data.
The detector reads Special slots from the camo menu. It requires two
matching reads and skips partial unlocks when the padlock scores and counter
disagree. Live capture-device verification and some in-game Special
challenge-banner variants are still pending.

## Portable release preparation

`scripts/build-portable-release.ps1` assembles the self-contained app and
both runtime dependencies in one ZIP. Supply a reviewed, redistributable
OCR and FFmpeg build with their applicable licence files and source notices.
The script takes the manifest key from `CAMO_PORTABLE_SIGNING_KEY` and embeds
the corresponding public PEM via `-UpdatePublicKeyFile`. The private key
must stay in the signing environment.

For Windows publisher signing, supply `-CertificateThumbprint` and
`-TimestampServer` on a machine with the trusted certificate available in
the current user's certificate store. If those options are omitted the
manifest is still signed, but the Windows executables are unsigned.

To enable verified updates, configure these repository Actions settings once.
Use the existing key pair if releases have already been signed with it.

| Setting | Name | Value |
| --- | --- | --- |
| Repository variable | `CAMO_UPDATE_PUBLIC_KEY` | RSA public key in PEM format |
| Repository secret | `CAMO_PORTABLE_SIGNING_KEY` | Matching RSA private key in PEM format |

For a first-time setup, generate and upload a new key from an authenticated
GitHub CLI session in PowerShell 7:

```powershell
$key = [Security.Cryptography.RSA]::Create(3072)
try {
    $key.ExportPkcs8PrivateKeyPem() | gh secret set CAMO_PORTABLE_SIGNING_KEY --repo JoshuaRibeiro/camo-connector
    if ($LASTEXITCODE) { throw 'Could not set the GitHub Actions signing secret.' }
    $key.ExportSubjectPublicKeyInfoPem() | gh variable set CAMO_UPDATE_PUBLIC_KEY --repo JoshuaRibeiro/camo-connector
    if ($LASTEXITCODE) { throw 'Could not set the GitHub Actions public key variable.' }
} finally { $key.Dispose() }
```

For each release, bump `desktop/version.txt`, commit the change and push a
matching tag, such as `v1.9.38`. Actions publishes the ZIP and its signed
manifest. The automated workflow signs the update manifest. Authenticode for
Windows executables is available through the local signing options above.

Existing builds without a trusted key require a one-time manual switch to
a keyed build. After that, use **Check for updates** and **Update and restart**
in the app. The updater checks signatures and keeps the existing `Data` folder.
