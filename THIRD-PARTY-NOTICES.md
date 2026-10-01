# Third-party software

Museek includes these components in its published folder:

- **.NET 10 and WPF**, Microsoft and contributors. MIT license; notices supplied in the published runtime. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.
- **LibVLCSharp 3.10.1**, VideoLAN and contributors. LGPL 2.1 or later. Source and license: https://code.videolan.org/videolan/LibVLCSharp/-/tree/3.10.1.
- **VLC / libVLC 3.0.24**, VideoLAN and contributors, via the official VideoLAN.LibVLC.Windows NuGet package. VLC is GPL 2 or later; libVLC is LGPL 2.1 or later, with plugins and dependencies under their respective licenses. Corresponding source: https://download.videolan.org/pub/videolan/vlc/3.0.24/ and https://code.videolan.org/videolan/vlc.
- **FFmpeg essentials Windows build**, FFmpeg contributors and Gyan Doshi. This GPL-enabled build is GPL 3 or later; its exact configuration/version is recorded in `licenses/FFmpeg-build.txt`, and its GPL license is included in `licenses`. Build provider: https://www.gyan.dev/ffmpeg/builds/. Corresponding upstream source: https://ffmpeg.org/download.html and https://ffmpeg.org/releases/; external codec library versions are documented by the build provider.

VLC and FFmpeg remain replaceable files in `libvlc/win-x64` and `tools`; they are not embedded into Museek.exe. FFmpeg is invoked as a separate process. Before redistributing modified binaries, review their licenses and provide the required corresponding source and notices.
