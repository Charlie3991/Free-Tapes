# Free Tapes

A Windows desktop music library and microphone mixer for proximity chat.

**Development status:** WARDOGS audio transmission is still being investigated. Hearing music through headphones does not confirm that the game receives it. Verify the CABLE Output recording meter and the game's selected voice input before relying on in-game playback.

## Run

Open `dist/FreeTapes/FreeTapes.exe`. The default build uses the .NET 10 Desktop Runtime. `./Build.ps1 -SelfContained` builds a version with its own runtime.

**Each time you use Free Tapes, reselect the virtual audio cable, even if it is already shown as selected.** Choose **CABLE Input** as the game feed in Free Tapes and **CABLE Output** as the microphone in WARDOGS, then apply the game setting before starting playback.

1. Install [VB-CABLE](https://vb-audio.com/Cable/) using its administrator installer, then reboot.
2. Run `./Setup-Tools.ps1` from the published app folder to download yt-dlp, FFmpeg, and Deno. Only download content you have permission to use. YouTube restrictions can prevent imports; any link is not guaranteed.
3. Open Free Tapes. Select your physical microphone, **CABLE Input** as the game feed, and your headphones under **Hear music through**. Click **Start routing**.
4. In WARDOGS audio settings, select **CABLE Output** as the microphone and enable proximity voice chat. Keep the game's proximity push-to-talk binding on **K**. With **Automatically hold proximity key K** enabled (default), Free Tapes holds K while music plays. With no music playing, holding your Free Tapes talk binding also holds K. Your microphone still opens only while you hold your talk binding.
5. Paste an HTTPS YouTube video URL and choose **Save YouTube → MP3**, or import a local audio file. Select a library item and press **Play**.
6. Click **Change bind**, then press your next keyboard key or click a mouse button inside Free Tapes (left, right, middle, or either side button). Esc cancels; switching away from the app also cancels. Release the captured input before using it to speak. The binding is saved immediately, and older keyboard preferences are preserved.
7. Hold the selected input (V by default) to enable your mic and duck music; release it to restore music and mute your physical mic. Automatic K runs only with routing active and WARDOGS focused. Pausing, stopping, or reaching the end of a song releases K unless you are still holding your talk binding. Switching away from the game, disconnecting, rebinding, disabling automatic K, and normal app exit release the app's K hold. Returning to the game resumes the hold if music is still playing.

**Next song** starts the next track in the playlist's displayed order, wrapping from the last to the first. It follows the current track even if you select a different library row. With no current track it advances from the selected row, or starts the first song if nothing is selected. It starts playback even when the previous song was paused or stopped.

The song area has two lists: **Playlist** on the left and **Not in playlist** on the right. Playback and Next use only the playlist. Click **×** beside a queued song to move it to the right while keeping its audio file; removing the current song stops its playback. Click **Add** on the right to append a song to the queue. Membership and order persist across app restarts. Existing songs migrate into the playlist initially, and new imports/downloads/trimmed copies are added automatically.

Each song row has its own **1%–200% Volume slider** in both lists. Songs default to 100%, and each file's setting is saved independently, including original and trimmed versions. Adjusting the playing song changes both headphone and game-feed audio immediately. Its level multiplies each output's own music slider and the speaking duck level: 50% game × 50% song = 25% game output before ducking, independently of your headphone level. Removing/re-adding a song retains its level. Streaming uses the separate game/headphone music sliders. Song volume changes do not modify the audio file.

Each row also has **Delete**. This shows a confirmation listing the original and linked trimmed copies that will be permanently deleted from Free Tapes, and removes those files from both lists. Deleting the current song first stops playback and releases automatic K unless you are speaking. Imported source files outside the Free Tapes library are never deleted. New trims carry explicit source relationships, including trims of trims; older recognizable trim filenames are migrated when unambiguous. Unrelated songs are preserved. Deletion is disabled while a download/trim save is pending.

Under **Playback bindings**, click **Bind** beside Play/pause, Alt. toggle, Stop, or Next, then press a keyboard key or mouse button inside Free Tapes. The old Play and Pause bindings are preserved as primary and alternate shortcuts for the combined Play/pause action. Shortcuts start unbound, save immediately, and run while WARDOGS is focused. **Clear** removes a shortcut. Each press triggers once; holding a button does not repeatedly execute the action. The captured input must be released before it can activate. Duplicate bindings and the game's reserved K key are rejected. Existing talk bindings are preserved.

The combined **Play/Pause** button starts playback when stopped, pauses the current track when playing, and resumes it when paused. Its label follows the current state, including streaming and natural song completion. When starting, it uses the selected playlist song (or the first song if none is selected), or reconnects to the last stream if no playlist song is selected. While a stream is connecting the button is disabled; Stop cancels the connection. Double-clicking a playlist song explicitly starts that song. Both Play/pause shortcuts perform the same toggle and update automatic proximity PTT immediately. Stop ends playback. Rebinding suspends the microphone and automatic K until capture finishes or is cancelled with Esc.

Automatic PTT uses ordinary Windows `SendInput`, without game injection or anti-cheat workarounds. WARDOGS may reject synthetic input; live in-game compatibility needs testing. Windows also restricts input to applications at equal or lower privilege levels. If the status reports automatic K unavailable, use the game's PTT manually. A forced process kill or system crash cannot run the app's key-release cleanup; tap and release physical K if needed. The app observes physical K separately so its own generated K presses never open your physical microphone when K is also your talk binding.

**Stream YouTube** resolves the pasted video's audio and plays it directly through the game feed and your headphones. Start routing first. Streaming uses a bounded RAM buffer and creates no audio file in the library; it still transfers audio over the network. Pause/resume and Stop work while streaming; Stop also cancels an in-progress connection. Play resumes a paused stream or reconnects to the last stream after it ends/stops (unless you select a library song). Next switches to the first library track. Streaming always plays the full track and ignores the trim fields. YouTube availability and temporary network interruptions can affect playback.

**MP3 trim** is only for downloads and saved songs. Enter start/end as seconds, `mm:ss`, or `hh:mm:ss` (for example `1:05.5` to `2:00`). Blank start means zero; blank end means the song's end. **Save YouTube → MP3** saves only that section in the library when a range is supplied. Conversion happens in a temporary staging folder; only the finished MP3 enters the library. To trim an existing download or imported song, select it and click **Trim selected song**. This saves a new MP3 and preserves the original. Invalid or out-of-duration times show an error; **Cancel save** cancels either kind of save and removes unfinished output.

Music and microphone volume both range from **1% to 200%**. **Music sent to game** controls what other players hear; **Music in my headphones** controls what you hear. These sliders are independent and save automatically. Existing preferences initialize both to your previous music level. The duck slider is a multiplier applied to both outputs while speaking. Microphone gain remains a separate control and the microphone is only live while the talk key is held. Disconnect stops routing and playback. Pause freezes both music readers, stops and flushes headphone playback, and removes music from the game mix; the microphone continues working. Resume reconnects music and restarts the headphone output. Boosting past 100% may distort loud recordings; final output samples are clamped to the legal digital range.

The library and preferences are stored in `%LOCALAPPDATA%/FreeTapes`. Library files remain available after restarts and do not need another download. Audio device selections, levels, and talk key are remembered; routing starts only when you request it. Local imports retain their original format. Headphone playback contains music only; use the game's microphone test to verify the complete mix. Keep the game/headset playback output separate from the cable to avoid feedback.

## Build and check

Install the .NET 10 SDK or place it in `tools/dotnet`. Run `./Build.ps1`. Run `./tests/Verify.ps1` to build and exercise the URL, mixer, pause, and offline-library checks without transmitting audio.

Manual verification with your devices:

- With routing active and a tape playing, hear music in headphones and see activity on Windows' CABLE Output recording meter.
- Hold the talk key: music gets quieter, your microphone appears on the cable meter, and your own voice does not play in headphones. Release: music restores and the physical microphone is muted.
- Try music/mic at 1%, 100%, and 200%. Pause, resume, stop, switch tracks, disconnect, and restart.
- Confirm the mix with WARDOGS' microphone test, then test proximity chat. A game PTT/voice threshold can prevent music transmission even if Windows' cable meter moves.
- Test a permitted public YouTube video, malformed URL, unavailable video, cancellation, and offline replay after restart.

## Dependencies

[NAudio](https://github.com/naudio/NAudio) (MIT), [yt-dlp](https://github.com/yt-dlp/yt-dlp), [FFmpeg](https://ffmpeg.org/), [Deno](https://deno.com/), and separately installed [VB-CABLE](https://vb-audio.com/Cable/). Downloader tools are fetched from their official release distribution locations; retain their bundled licensing files if redistributing. The default repository/build does not bundle the virtual cable driver.

## License

Free Tapes source code is available under the [MIT License](LICENSE). Third-party dependencies retain their own licenses.
