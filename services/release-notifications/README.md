# Release notifications

The Worker sends version numbers to open desktop clients. The app checks the
signed GitHub release and stages the download. Installation still waits for
the user. The Worker holds no update files and never receives user progress.

## One-time deployment

From this directory, run the following with an authenticated Wrangler session:

```powershell
wrangler deploy
wrangler secret put RELEASE_TOKEN
```

Use a securely generated random token for the Worker secret. In the
camo-connector GitHub repository, set:

- Actions variable `CAMO_UPDATE_NOTIFY_URL` to the deployed HTTPS Worker origin
  (for example, `https://camo-connector-updates.example.workers.dev`).
- Actions secret `CAMO_UPDATE_NOTIFY_TOKEN` to the same token as `RELEASE_TOKEN`.

Set the variable and secret **before** pushing the desktop release tag. The
release build embeds the origin into the app, and the publication job sends a
notice after the GitHub release is created. With no origin configured, desktop
clients use scheduled checks and publication skips the notice. Existing
clients must install a build with the embedded origin once to receive notices.

Run `node --test worker.test.mjs` from this directory to validate publication
authorization and version ordering.
