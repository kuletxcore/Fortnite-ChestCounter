# Chest Counter for Android

The Android version of the counter. It uses the same replay analysis, history and report as `ChestCounter.exe`
(the platform-neutral files of `src/ChestCounter` are linked into this project), with an Android shell around them.

## What it does

- **Reads Fortnite's replays** (`com.epicgames.fortnite`). The player grants the replays folder once with the system
  folder picker (it opens on `Android/data/com.epicgames.fortnite/files`), and the counter copies each new `.replay`
  into its own storage and counts it. On Android 10 and older the folder is read directly.
- **Replays from a PC** can be imported (*Import replays*, or *Share* / *Open with* a `.replay` file from a file
  manager), or read from any folder they are copied to.
- **Play Fortnite** starts the game and a foreground service that checks for new replays every 15 seconds and draws a
  **floating counter** over the game (total, last match, session, treasures; drag it to move it). Its notification
  hides the overlay or stops the counter.
- The **report** (chances, collection, treasures, latest matches) is the same page as on the PC.

It never touches the game, its files or its memory: replays are opened read-only.

## Limits

- Android 11 and later restrict access to other apps' `Android/data` folders. Depending on the Android version and
  the phone, the folder picker may refuse Fortnite's folder; replays then have to be imported or copied elsewhere.
- Fortnite has to record replays (*Settings → Game → Replays*). If your platform's Fortnite does not record any,
  import replays from a PC.
- A match is counted once its replay is finished, a few seconds after the match.

## Build

```bash
dotnet workload install android
```

```bash
dotnet publish src/ChestCounter.Android/ChestCounter.Android.csproj -c Release -o dist-android
```

The APK is `dist-android/io.github.chestcounter.android-Signed.apk` (signed with a debug key; use your own keystore
with `-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=...` for a release). CI builds it as the `ChestCounter-android`
artifact.
