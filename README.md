# Grappler

A Windows desktop app for [yt-dlp](https://github.com/yt-dlp/yt-dlp): paste a
link, pick a quality, get a file. It comes with a Brave extension that grabs
the video you're watching in one click, with live progress in the toolbar.

- **Paste and go.** Title, channel, length and thumbnail show up before the
  download starts, so you know you pasted the right link.
- **Several at once.** Paste many links, drop a `.txt` file of links, or grab
  a whole playlist or channel. Three download in parallel by default.
- **Any quality.** Best, 1080p, 720p, 480p, MP3, original audio, subtitles
  only, or pick an exact format from the full list.
- **Big files are fine.** Resumes after dropped connections, retries failed
  chunks, and has no size or length limit.
- **Brave extension.** A toolbar button, a right-click menu and `Alt+Shift+G`
  send the page to Grappler. Your downloads, with progress, show in the
  popup, and sign-in works for private and members-only videos.
- **Runs in the tray.** Downloads carry on with the window closed, with a
  notification when each one finishes.
- **Self-contained.** Everything lives in one folder: no installer, no admin
  rights, nothing added to PATH. Delete the folder and it's gone.

## Requirements

- Windows 10 or 11 (64-bit). The .NET Framework it uses is part of Windows.
- About 400 MB of disk space for yt-dlp, ffmpeg and deno, which setup
  downloads from their official releases.
- Optional: [Brave](https://brave.com/) for the browser extension. Chrome
  works too.

## Quick start

1. **Get the code.** Clone it:

   ```powershell
   git clone https://github.com/AllaudinAIi/Grappler.git
   ```

   or use **Code → Download ZIP** on GitHub and unzip it anywhere.

2. **Double-click `Setup.bat`.** It downloads the tools into `bin\` and
   builds `Grappler.exe`. It takes a minute or two, mostly for ffmpeg.

3. **Double-click `Grappler.exe`.** Paste a link and press Enter.

Videos are saved to the `downloads` folder. You can change that in Settings.

### Getting updates

```powershell
git pull
.\Build.bat
```

If downloads from a site start failing, run `Update.bat` (or Settings →
**Update yt-dlp**) first. Sites change all the time and yt-dlp ships fixes
every few days.

## The Brave extension

**One-time setup:**

1. Double-click `Install-Brave-Extension.bat`. It tells Brave where Grappler
   is: one registry entry under your user account, no admin rights needed.
2. In Brave, open `brave://extensions`, turn on **Developer mode**, click
   **Load unpacked** and choose the `browser-extension` folder. Its path is
   on your clipboard after step 1.
3. Pin the Grappler icon to the toolbar.

**Then, on any video page:**

- click the toolbar button, choose a quality, and press **Grab**
- or right-click the page, a link or a video → **Grab with Grappler**
- or press `Alt+Shift+G`

If Grappler isn't running, it starts hidden in the system tray, with no
window popping up. The popup lists your downloads with progress, speed and
time left, plus cancel, retry, play and show-in-folder. While the popup is
closed, the toolbar icon shows progress.

**Use my sign-in for this site** (on by default) passes Brave's cookies for
that site to Grappler, so private, members-only and age-restricted videos
work, and so do sites with bot checks. The cookies stay on your PC: they go
over a local connection, are used for that one download, and are deleted when
you clear the row or close Grappler.

If you move the Grappler folder, run `Install-Brave-Extension.bat` again. To
remove the extension, run `Install-Brave-Extension.bat -Uninstall` and remove
it in `brave://extensions`.

## Using the app

**Quality** is the dropdown next to the link box:

| Option | What you get |
|---|---|
| Best quality | The default |
| 1080p / 720p / 480p or lower | Caps the resolution |
| Audio only - MP3 | Converted, highest quality |
| Audio only - best | Whatever the site serves, untouched |
| Subtitles only | No video |
| Choose format... | Lists every format the video offers |

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
run at once, what the close button does, and **Update yt-dlp**. Everything
else, such as file naming, retries and what gets embedded in each file, is
one commented option per line in `config\yt-dlp.conf`.

### Good to know

- **Above 720p, video and audio download separately**, so the percentage
  restarts partway through. That's the second stream starting. ffmpeg then
  joins them in the `Merging audio and video` step.
- **Three at a time is usually the sweet spot.** Your connection is shared
  between them, and some sites throttle you past three or four. The maximum
  is 8.
- **Partial files** wait in `downloads\.incomplete\`, so the main folder only
  ever shows finished videos.
- **Prefer a console?** `Download.bat` is a terminal version that shares the
  same tools and settings.

## Troubleshooting

| Symptom | What to do |
|---|---|
| `Unable to extract ...` | Run `Update.bat`; this is almost always an outdated yt-dlp |
| `Private video` / `Video unavailable` | Nothing to fix: the video isn't public |
| `Sign in to confirm you're not a bot` | Grab it from Brave with **Use my sign-in** turned on |
| `Requested format is not available` | Use **Choose format...** to see what exists |
| `Failed to decrypt with DPAPI` | Set Settings → Sign-in cookies to *None* and use the Brave extension's sign-in instead. Chrome and Brave no longer let other programs read their cookies |
| Stuck at 99%, then fails | The ffmpeg merge failed: run `Setup.bat` again |
| `yt-dlp.exe is missing` | Run `Setup.bat` |

**Patches to yt-dlp** live in `bin\yt-dlp-plugins\`. yt-dlp loads them
automatically, and they survive yt-dlp updates. `kvs-fix` handles video sites
on newer KVS players, which otherwise fail with `Unable to extract
flashvars`. `config\yt-dlp.conf` also makes yt-dlp pose as Chrome on sites it
has no dedicated support for, which gets past most bot checks.

## Project layout

```
Grappler\
  Setup.bat                    first-time setup: tools + build
  Grappler.exe                 the app (created by Setup.bat / Build.bat)
  Build.bat                    rebuilds the app from source
  Update.bat                   updates yt-dlp
  Download.bat                 terminal version
  Install-Brave-Extension.bat  connects the Brave extension
  app\
    build.ps1                  MSBuild wrapper
    make-icon.ps1              makes every icon from the logo in assets\
    assets\                    logo-48.png, logo-96.png
    src\                       the WPF app (C#)
      Theme.xaml               the design system
      MainWindow, SettingsWindow, FormatWindow, CloseWindow
      Core\                    settings, queue engine, yt-dlp plumbing,
                               tray icon, BrowserBridge (the extension link)
  browser-extension\           the Brave extension (Manifest V3)
    host\                      tells Brave where Grappler.exe is
  bin\                         yt-dlp, ffmpeg, ffprobe, deno (downloaded)
    yt-dlp-plugins\            local patches to yt-dlp
  config\
    settings.ps1               download folder, shared with Download.bat
    yt-dlp.conf                quality, naming, retries, embedding
  scripts\                     setup, extension installer, terminal menu
  downloads\                   your videos (created on first run)
```

## Building from source

`Setup.bat` already builds the app. After changing anything under
`app\src\`, run `Build.bat`, or:

```powershell
.\app\build.ps1            # build and copy Grappler.exe to the project root
.\app\build.ps1 -Run       # ...and launch it
.\app\build.ps1 -Clean     # wipe obj\ and bin\ first
```

It builds with the MSBuild and WPF that come with Windows as part of the .NET
Framework: no SDK, no NuGet, no network, and it takes a few seconds.

One catch before you edit: that compiler only understands **C# 5**. No string
interpolation, no `?.`, no `nameof`. `async`/`await` is fine.

To refresh only yt-dlp, which is the tool that goes out of date:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -SkipFfmpeg -SkipDeno
```

## Contributing

Issues and pull requests are welcome. For a bug, include the link that failed
(if you can share it) and the log from **Copy the full log** on the failed
row.

## License and credits

Grappler is released under the [MIT License](LICENSE).

It drives tools that are downloaded separately and keep their own licences:
[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense),
[FFmpeg](https://ffmpeg.org/) (GPL builds from
[yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)) and
[Deno](https://deno.com/) (MIT). None of them are included in this
repository.

Logo: [Download icon by Icons8](https://icons8.com/icon/set/download/glassmorphism).

Downloading is subject to each site's terms of service and to copyright law.
Grappler is a tool; what you use it for is your responsibility.
