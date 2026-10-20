# Qlyptra

A small Windows app: download a track from a Yandex Music link, prepare an MP3, copy it to the clipboard, or save it to disk.

Author: [@wz3k9l](https://t.me/wz3k9l)

[Русский](README.md)

---

## How to use

1. Open **Settings**, paste your Yandex Music token, and click **Save token**.
2. On **Track & link**, paste a track URL and click **Find / download**.
3. When the track is ready:
   - **Copy MP3** — puts the file on the clipboard so you can paste it (`Ctrl+V`) into any messenger that accepts audio files;
   - **Save** — writes the track under `Tracks` next to the app, in a subfolder named after the **first** artist in the credit line;
   - **Copy link** — copies the original track URL.

After saving, use **Open folder** to jump to the file in Explorer.

---

## Token

The downloader needs a personal Yandex Music OAuth token.

Get it here:  
https://ym.marshal.dev/token/#implicit-oauth  

A practical path: scroll to **Alternative methods → Website**, follow the steps, then paste the token into Qlyptra.

The token is stored only on your PC (encrypted with Windows DPAPI for your user account). The app does not send it anywhere else; it is used locally when fetching the track. Do not share the token — it grants access to your account.

---

## Prebuilt release

Download the archive from [Releases](../../releases), extract it **as a whole**, and run `Qlyptra.exe`.

Keep the `bin` folder next to `Qlyptra.exe` (it contains ffmpeg and the downloader runtime) — downloads will not work without it. The `Tracks` folder is created automatically on the first save.

You do **not** need the .NET SDK to run a release build. The SDK is only required if you build from source.

---

## Build from source

Do not keep the sources under `Program Files` — the build writes next to the project. Copy the repo somewhere writable, e.g. `Documents\Qlyptra`.

Windows x64, [.NET 10 SDK](https://dotnet.microsoft.com/download), and Python 3.9+ (3.12 recommended).

```bat
build.bat
```

Output goes to the `release\` folder.

---

## Components

- [yandex-music-downloader](https://github.com/llistochek/yandex-music-downloader) — track download  
- FFmpeg — MP3 conversion  

Only use this for content you are allowed to download and share.
