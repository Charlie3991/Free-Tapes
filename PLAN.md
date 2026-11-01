# Free Tapes implementation plan

1. Build a native WPF Windows desktop application with a persistent offline audio library.
2. Import individual HTTPS YouTube links using yt-dlp and FFmpeg; stage each download separately and only publish complete MP3s. Support local MP3/WAV/M4A imports, cancellation, and useful errors.
3. Capture a selected physical microphone with WASAPI, resample to 48 kHz stereo, mix music, and send to a selected virtual cable playback endpoint. The cable's recording endpoint appears in the game as a microphone.
4. Poll a configurable hold key without consuming the game's input. While held, enable the physical mic and multiply music volume by the duck setting; on release, mute the mic and restore music.
5. Play music separately to selected headphones. Provide music and microphone sliders from 1–200%, with a final sample clamp to prevent digital overflow.
6. Package a Windows x64 build, downloader setup script, setup guide, and automated checks. Verify real audio routing and proximity transmission manually with VB-CABLE and WARDOGS.

## Boundaries

VB-CABLE is an external signed audio driver and needs administrator installation and a reboot. This application does not install a driver or modify the game. YouTube availability is controlled by the service; no promise of access to private, DRM, age-restricted, removed, or geographically restricted media. Music monitoring uses a separate WASAPI playback clock, so headphone playback is approximately synchronized, not sample synchronized.
