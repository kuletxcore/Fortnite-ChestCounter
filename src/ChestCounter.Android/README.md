# Chest Counter for Android

The Android version of the counter. It uses the same replay analysis, history and report as `ChestCounter.exe`
(the platform-neutral files of `src/ChestCounter` are linked into this project), with an Android shell around them.

## What it does

Fortnite on Android (`com.epicgames.fortnite`) does not record replays (Epic offers replays on PC, PlayStation and
Xbox only), so on the phone the chests are **counted by hand**, with a floating counter over the game:

- **Play Fortnite** starts the game and a foreground service that keeps the counter on screen.
- **🧰 +1** for each chest opened (long press: undo the last one). With auto-search on, the game opens a chest without a
  tap: remember to tap 🧰 when it opens. **★** when a chest gives a treasure, **⏹** when the match is over (a match
  with no chest for 35 minutes ends by itself). Drag the figures to move the counter. *+1 chest* is also in the
  notification, and the same buttons are in the app when the overlay is not allowed.
- **Auto-detect (👁)**: the counter watches the screen (screen capture; Android shows its own consent dialog each time
  it starts, the overlay permission does not cover it) and adds a chest when the **search cue** shows for a moment and
  then goes away, which also catches the chests auto-search opens without a tap. The cue is taught once, on your own
  game: tap **🎯** next to a chest, let the 3-second countdown end while the search shows, then draw a box around the
  search ring or the *Search* button. The figure after 👁 on the counter is how close that spot looks to the cue
  (1 = identical); *Detection* in the app sets how close it must be (loose / normal / strict). Frames are looked at a
  few times per second at 360 px, in memory only: nothing is recorded or sent. A search given up halfway can't be told
  from a finished one: long-press 🧰 to undo.
- **Reset**: hold **↺** on the counter for a new session (*Session* back to 0; the total, the history and the match
  being counted are kept). In the app, **↺ Reset** offers the same, **Reset all counts** (matches, chests added by hand
  and the match in progress, after a backup; the collection and the list of replays already read are kept), and
  **Undo the last reset**, which brings the cleared counts back together with what was counted since. The session
  survives the app being closed by Android, and a new one starts by itself after 4 hours without playing.
- Each tap or detection keeps its time, so the chests opened during a **Treasure Hour** count with their boosted odds. A match is
  saved as the mode picked in the app (**Mode**: solo / duo / trio / squad, build or zero build).
- The **report** (chances, collection, treasures, latest matches) is the same page as on the PC.
- **Replays from a PC** can still be imported (*Import PC replays*, *Share* / *Open with* a `.replay`), or read from a
  folder they are synced to: their chests are counted automatically, by the same analysis as the exe.

It never touches the game, its files or its memory.

## Build

```bash
dotnet workload install android
```

```bash
dotnet publish src/ChestCounter.Android/ChestCounter.Android.csproj -c Release -o dist-android
```

The APK is `dist-android/io.github.chestcounter.android-Signed.apk`. CI builds it as the `ChestCounter-android` artifact
on every push, with the run number as `versionCode`, and attaches it to releases.

### Signing (needed for updates)

Android only installs an update over an app signed with the **same key**. Without a key, each build is signed with a
throwaway debug key, so a new APK can't replace the installed one: players have to uninstall first and lose their
counted matches. Create a key once and keep it safe (losing it means the same problem):

```bash
keytool -genkeypair -v -keystore chestcounter.keystore -alias chestcounter -keyalg RSA -keysize 2048 -validity 10000
```

Then add four repository secrets (*Settings → Secrets and variables → Actions*): `ANDROID_KEYSTORE_BASE64` (the output
of `base64 -w0 chestcounter.keystore`), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (`chestcounter`) and
`ANDROID_KEY_PASSWORD`. CI signs with it from then on. Never commit the keystore.

### Tests

The Android logic without Android types (auto-detect: `CueMatcher.cs`, a match counted by hand: `TallyRecord.cs`) is
tested with the shared code: `dotnet test tests/ChestCounter.Tests`.
