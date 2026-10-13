# Grappler

A Windows desktop app for downloading online video: paste a link, see what
the video offers, pick a quality, download. It comes with a Brave extension
that does the same for the page you're watching, with live progress in the
toolbar.

- **Fetch, then choose.** Grappler first reads the video's title, channel,
  length and thumbnail, and the qualities it **really** offers, with
  estimated sizes. You pick one; nothing downloads until you press
  **Download**.
- **Several at once.** Paste many links, drop a `.txt` file of links, or
  fetch a whole playlist or channel. **Download all** starts every fetched
  video at the quality chosen on its row. Three download in parallel by
  default.
- **Any format.** Each resolution the video has, audio as MP3 or in its
  original format, subtitles, or an exact stream from the full list.
- **Big files are fine.** Resumes after dropped connections, retries failed
  chunks, and has no size or length limit.
- **Brave extension.** Fetch the page from the toolbar, the right-click menu
  or `Alt+Shift+G`, choose a quality in the popup, and follow the progress
  there. Sign-in works for private and members-only videos.
- **Runs in the tray.** Downloads carry on with the window closed, with a
  notification when each one finishes.
- **Self-contained.** Everything lives in one folder: no installer, no admin
  rights, nothing added to PATH. Delete the folder and it's gone.

## Requirements

- Windows 10 or 11 (64-bit). The .NET Framework it uses is part of Windows.
- About 400 MB of disk space for the download tools, which setup fetches from
  their official releases.
- Optional: [Brave](https://brave.com/) for the browser extension. Chrome
  works too.

## Quick start

1. **Get the code.** Clone it:

   ```powershell
   git clone https://github.com/AllaudinAli/Grappler.git
   ```

   or use **Code → Download ZIP** on GitHub and unzip it anywhere.

2. **Double-click `Setup.bat`.** It downloads the tools into `bin\` and
   builds `Grappler.exe`. It takes a minute or two, mostly for ffmpeg.

3. **Double-click `Grappler.exe`.** Paste a link, press **Fetch**, choose a
   quality, and press **Download**.

Videos are saved to the `downloads` folder. You can change that in Settings.

### Getting updates

```powershell
git pull
.\Setup.bat
```

Setup skips the tools you already have, so this is quick. If downloads from
a site start failing, open Settings → **Update download engine** first.
Sites change all the time and engine fixes ship every few days.

## The Brave extension

**One-time setup:**

1. In Grappler, open **Settings → Brave extension** and click **Connect**.
   This writes one registry entry under your user account (no admin rights
   needed) and copies the extension's folder path to your clipboard.
2. In Brave, open `brave://extensions`, turn on **Developer mode**, click
   **Load unpacked** and choose the `browser-extension` folder.
3. Pin the Grappler icon to the toolbar.

**Then, on any video page:** click the toolbar button and press **Fetch
video**. When the details are in, choose a quality and press **Download**.
The right-click menu (**Fetch with Grappler**) and `Alt+Shift+G` do the same
and open the popup for the quality choice.

If Grappler isn't running, it starts hidden in the system tray, with no
window popping up. The popup lists your downloads with progress, speed and
time left, plus cancel, retry, play and show-in-folder. While the popup is
closed, the toolbar icon shows progress.

**Use my sign-in for this site** (on by default) passes Brave's cookies for
that site to Grappler, so private, members-only and age-restricted videos
work, and so do sites with bot checks. The cookies stay on your PC: they go
over a local connection, are used for that one download, and are deleted when
you clear the row or close Grappler.

Moving the Grappler folder is fine: Grappler updates the connection the next
time it starts. To remove the extension, click **Disconnect** in Settings and
remove it in `brave://extensions`.

## Using the app

**Fetch a link.** Paste one or more links and press **Fetch** (or Enter).
Each row shows the video's details, then a quality list built from what that
video actually has:

| Choice | What you get |
|---|---|
| 4K / 1080p / 720p ... | Each resolution the video offers, highest first, with frame rate and estimated size |
| Audio only - MP3 | Converted, highest quality |
| Audio only - original | The site's own audio, untouched |
| Subtitles only | Shown when the video has subtitles |
| More formats... | The full list of streams, pick one exactly |

The list starts on your preferred quality (set in Settings), or the closest
one the video has. Press **Download** on a row, or **Download all** for
every fetched row.

**Whole playlist or channel** is the checkbox. Leave the items box empty for
everything, or narrow it down with `1-10`, `3,7,12` or `5:`. Grappler keeps a
record in `downloads\.downloaded-archive.txt`, so running a channel again
later only fetches the new uploads.

**The queue** has `All / Active / Done / Failed` tabs, a text filter and five
sort orders. Each row can be cancelled while running, or opened and played
once done. A failed row offers **Retry** and **Copy the full log**.

**Closing the window** asks whether to quit or keep Grappler running in the
tray. Tick **Don't ask again** to remember your answer; you can change it
later in Settings. Only one Grappler runs at a time: opening it again brings
the running one back. To quit from the tray, right-click the icon → **Quit**.

**Settings** (the gear button) covers the download folder, how many downloads
run at once, your preferred quality, what the close button does, the Brave
extension, and **Update download engine**. Everything else, such as file
naming, retries and what gets embedded in each file, is one commented option
per line in `config\yt-dlp.conf`.

### Good to know

- **Above 720p, video and audio download separately**, so the percentage
  restarts partway through. That's the second stream starting. ffmpeg then
  joins them in the `Merging audio and video` step.
- **Three at a time is usually the sweet spot.** Your connection is shared
  between them, and some sites throttle you past three or four. The maximum
  is 8.
- **Partial files** wait in `downloads\.incomplete\`, so the main folder only
  ever shows finished videos.

## Troubleshooting

| Symptom | What to do |
|---|---|
| `Unable to extract ...` | Settings → **Update download engine**: this is almost always an outdated engine |
| `Private video` / `Video unavailable` | Nothing to fix: the video isn't public |
| `Sign in to confirm you're not a bot` | Fetch it from Brave with **Use my sign-in** turned on |
| `Failed to decrypt with DPAPI` | Set Settings → Sign-in cookies to *None* and use the Brave extension's sign-in instead. Chrome and Brave no longer let other programs read their cookies |
| Stuck at 99%, then fails | The merge failed: run `Setup.bat -Force` to download fresh tools |
| "Download engine is missing" | Run `Setup.bat` |
| The extension says it can't reach Grappler | Settings → Brave extension → **Connect**, then restart Brave |

**Patches to the engine** live in `bin\yt-dlp-plugins\`. They load
automatically and survive engine updates. `kvs-fix` handles video sites on
newer KVS players, which otherwise fail with `Unable to extract flashvars`.
`config\yt-dlp.conf` also makes the engine pose as Chrome on sites it has no
dedicated support for, which gets past most bot checks.

## Project layout

```
Grappler\
  Setup.bat                    first-time setup: tools + build
  Grappler.exe                 the app (created by Setup.bat)
  app\
    build.ps1                  MSBuild wrapper, also makes the icons
    make-icon.ps1              makes every icon from the logo in assets\
    assets\                    logo-48.png, logo-96.png
    src\                       the WPF app (C#)
      Theme.xaml               the design system
      MainWindow, SettingsWindow, FormatWindow, CloseWindow
      Core\                    settings, queue engine, engine plumbing,
                               VideoInfo (quality choices), tray icon,
                               BrowserBridge + BraveExtension (the extension link)
  browser-extension\           the Brave extension (Manifest V3)
    host\                      tells Brave where Grappler.exe is
  bin\                         yt-dlp, ffmpeg, ffprobe, deno (downloaded by Setup.bat)
    yt-dlp-plugins\            local patches to the engine
  config\
    yt-dlp.conf                naming, retries, embedding
    grappler.conf              your settings (created on first run, not in git)
  scripts\
    setup.ps1                  downloads the tools
  downloads\                   your videos (created on first run)
```

## Building from source

`Setup.bat` builds the app. After changing anything under `app\src\`, run
`Setup.bat` again, or:

```powershell
.\app\build.ps1            # build and copy Grappler.exe to the project root
.\app\build.ps1 -Run       # ...and launch it
.\app\build.ps1 -Clean     # wipe obj\ and bin\ first
```

It builds with the MSBuild and WPF that come with Windows as part of the .NET
Framework: no SDK, no NuGet, no network, and it takes a few seconds. Close
Grappler first, since a running exe can't be replaced.

One catch before you edit: that compiler only understands **C# 5**. No string
interpolation, no `?.`, no `nameof`. `async`/`await` is fine.

## Contributing

Issues and pull requests are welcome. For a bug, include the link that failed
(if you can share it) and the log from **Copy the full log** on the failed
row.

## License and credits

Grappler is released under the [MIT License](LICENSE).

It runs on tools that are downloaded separately and keep their own licences:
[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), the download engine;
[FFmpeg](https://ffmpeg.org/) (GPL builds from
[yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)); and
[Deno](https://deno.com/) (MIT). None of them are included in this
repository.

Logo: [Download icon by Icons8](https://icons8.com/icon/set/download/glassmorphism).

Downloading is subject to each site's terms of service and to copyright law.
Grappler is a tool; what you use it for is your responsibility.
