# FFMETestApp

WPF/C# testbed for FFME and FFmpeg.  Used for validating dual-buffer seamless video switching, zoom/pan, overlays, scrubbing, and thumbnail extraction. See details section below.

**Target:** .NET 10, x64, Windows, WPF

---

## Prerequisites

### 1. .NET 10 SDK

Download from https://dotnet.microsoft.com/download/dotnet/10.0 (x64 Windows).

### 2. NuGet packages (restored automatically)

| Package | Version |
|---------|---------|
| `Sinaxxr.FFME.Windows` | 8.0.361-sinaxxr.1 |
| `FFmpeg.AutoGen` | 8.0.0.1 |

These are declared in `FFMETestApp.csproj` and restored by `dotnet restore` or Visual Studio. No manual steps required.

#### Why Sinaxxr.FFME.Windows

FFME has been maintained by several people across forked repositories. `Sinaxxr.FFME.Windows` is currently the best available NuGet package for the following reasons:

- **`FFME.Windows`** (unosquare — the original): not updated for FFmpeg 8.x; depends on `FFmpeg.AutoGen 7.0.0` and will fail to load FFmpeg 8.x DLLs at runtime.
- **`zgabi.FFME.Windows`**: updated for FFmpeg 8.x but contains no bug fixes beyond that single change; still exhibits a known DirectSound COM RCW finalizer-thread crash on rapid video open/close cycles.
- **`Sinaxxr.FFME.Windows`**: updated for FFmpeg 8.x (`FFmpeg.AutoGen 8.0.0.1`) and includes lifecycle stability fixes — dedicated decode/read worker threads, DirectSoundPlayer audio output improvements, and a seek-to-zero close/open workaround.

### 3. FFmpeg 8.x Windows shared DLLs

The project **does ship** the FFmpeg binaries. If for some reason you want to get them yourself (e.g., if there is a later version) you must obtain them separately and place them in `FFMETestApp\ffmpegbin\`. The build copies everything in that folder to the output directory automatically.

#### Which FFmpeg DLLs are required

These seven files must be available in `FFMETestApp\ffmpegbin\`:

```
avcodec-62.dll
avdevice-62.dll
avfilter-11.dll
avformat-62.dll
avutil-60.dll
swresample-6.dll
swscale-9.dll
```

The version suffixes (`-62`, `-60`, etc.) identify the **FFmpeg 8.x** ABI. DLLs from FFmpeg 6.x or 7.x have different suffix numbers and will not load correctly.

#### Where and how FFMEG was downloaded 

**Option A — gyan.dev (recommended for Windows)**

1. Go to https://www.gyan.dev/ffmpeg/builds/
2. Under **Release builds**, download **`ffmpeg-release-full-shared.7z`** (or the `essentials` variant — either contains the shared DLLs).
3. Extract the archive. Inside `bin\` you will find all the `.dll` files.
4. Copy the seven DLLs listed above into `FFMETestApp\ffmpegbin\`.

**Option B — BtbN GitHub builds**

1. Go to https://github.com/BtbN/FFmpeg-Builds/releases
2. Download **`ffmpeg-n8.x.x-win64-lgpl-shared-8.x.zip`** (choose the latest `8.x` tag, `lgpl-shared` variant).
3. Extract. The DLLs are in `bin\`.
4. Copy the seven DLLs listed above into `FFMETestApp\ffmpegbin\`.

> **Important:** Always use the **shared** (dynamic library) build, not the static or GPL-only build. The `lgpl-shared` variant from BtbN or the `full-shared` variant from gyan.dev both work. GPL builds also work but carry additional licence obligations.

---

## Build & Run

```
cd FFMETestApp
dotnet build -c Debug
dotnet run -c Debug
```

Or open `FFMETestApp.slnx` in Visual Studio 2022 (v17.12+) and press F5.

---

## Video folder

On startup the app searches upward from the executable directory for a folder named `Videos`. Place `.mp4`, `.avi`, `.mov`, `.wmv`, or `.mkv` files in such a folder. At least two files are needed for the auto-switch loop. If the folder or videas are not present, it asks the user to locate a folder containing videos. 

---

## Licence notes

`Sinaxxr.FFME.Windows` is MIT-licensed (fork of the original unosquare FFME project).
`FFmpeg.AutoGen` is LGPL.
The FFmpeg shared libraries themselves are LGPL (when using the `lgpl` build) or GPL (when using the `gpl` build) — check the terms for your use case.

# Details and what this program illustrates

WPF testbed for validating dual-buffer seamless video switching, zoom/pan, overlays, scrubbing, and thumbnail extraction using FFME and FFmpeg. 

## Controls and what they illustrate

###OpenVideo:
- Standard file dialog that opens in the included Videos folder and lets you select a video
- ***illustrates how to open and load a video***

###Open By Video Thumbnail
- Displays a dialog box showing thumbnails of all videos in the video folder (1 second in).
- ***illustrates how to create video thumbnails***

###Play/Pause button
- toggles video playing/pausing
- ***illustrates how to play/pause a video***

###Stop button
- stops playing
- ***illustrates stopping a video and resetting it back to the beginning***

###Volume button
- adjust the volume
- ***illustrates dynamically adjusting the video volume***

###Position slider
- allows the video to be scrubbed manually during play or pause
- ***illustrates scrubbing, which is somewhat more complex than you think.***
   ***Note that ScrubbingEnabled="False" must be set to avoid unwanted effects***

###Overlays
- Shows frame number and position at the top,  and a progress bar showing its position at the bottom
- ***illustrates how a video can be overlayed by other WPF elements in a canvas***

###Zoom
- Allows zooming in and out of the video
- ***illustrates zooming, and how the overlays can be clamped to corners of the video***

###ScrollWheel to zoom
- Does the actual zooming in and out
- ***illustrates zooming, and how the overlays can be clamped to corners of the video***

###Click to translate
- translates the video (which can be in a zoomed state) 
- ***illustrates translation and how the overlays remain clamped to corners of the video***

###Double click to unzoom
- unzoom/ untranslate the video
- ***illustrates restoring zoom and translation to its defaults***

###Repeat
- auto-repeat the video when it reaches its end
- ***illustrates how to reset the video to its beginning and replay it when it reaches its end***

###Dual buffer
- toggle use of a dual buffer to show smooth video transitions when opening/switching to a new video
-***Illustrates how we use two video players to avoid the visually annoying 'black screen' that occurs when a video is opened or switched***

###Hardware accelerated
- Try to use D3D11VA hardware-accelerated decoding; falls back silently to software if the GPU or codec doesn't support it. Reloads the current video immediately so the change takes effect. The status bar shows whether hardware or software decode is active.
- ***Illustrates how to enable hardware acceleration in FFME and detect whether it is actually in use***

###Use direct sound
- Switches the audio output engine between DirectSound (checked, default) and the legacy WinMM renderer (unchecked). Reloads the current video immediately so the change takes effect. The status bar shows the active audio codec and engine.
- ***Illustrates the two audio renderers available in FFME and reproduces the DirectSound COM RCW finalizer-thread crash that occurs with rapid video switching when UseLegacyAudioOut is false (see github.com/unosquare/ffmediaelement/issues/683)***

###Speed
-change the video speed
-***illustrates how to change the speed of the runnnig video***

###Switch videos automatically
-closes the current video and opens another random video after the interval set in the slider
***illustrates time constraints in opening/closing videos rapidly, helpful for certain UI situations***

###Forward /Backward arrow keypress
-Go to the next/previous frame
-***illustrates frame by frame navigation.***

###status indicators
-***illustrates how to get video information, such as its position, resolution, and frames per second.***
