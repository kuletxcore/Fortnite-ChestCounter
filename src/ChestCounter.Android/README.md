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

The APK is `dist-android/io.github.chestcounter.android-Signed.apk` (signed with a debug key; use your own keystore
with `-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=...` for a release). CI builds it as the `ChestCounter-android`
artifact.
