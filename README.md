# ClubFFXIV

A Dalamud plugin that turns FFXIV housing into spatial audio venues. Tune any house to an internet radio stream and walk past it to hear muffled music drift through the door — step inside, full audio.

## What it does

Stand in a ward near a club's front door → hear the music through the wall, getting clearer as you walk up. Step inside → full audio, no muffling. Walk away → fades out.

Listeners point ClubFFXIV at any of these:

- **Twitch channel** — `twitch.tv/yourchannel`
- **YouTube Live** — `youtube.com/watch?v=...`
- **SoundCloud / Mixcloud / Twitcasting / Niconico**
- **Icecast / Shoutcast / direct MP3 stream**

DJs who already broadcast on Twitch or YouTube don't change anything — listeners just paste the channel URL.

## Features

- **Spatial proximity audio** — distance-based volume + lowpass filter; pre-buffers ahead of the audible threshold so audio is ready the moment you cross it
- **Auto-discovery** — registered houses auto-tune when you enter; nearby clubs auto-play muffled when you approach outdoors
- **Multi-source playback** — Twitch / YouTube / SoundCloud handled via bundled yt-dlp + ffmpeg; direct MP3/OGG streams use in-process decoders (NLayer / NVorbis)
- **DJ identity** — Ed25519 keypair signs your publishes; only you can update or delete your club
- **House ownership check** — local FFXIVClientStructs check prevents accidental publishing of plots you don't own
- **Cross-platform** — works on Windows, and via XIVLauncher on Mac/Linux through Wine

## Install

The plugin isn't in the official Dalamud repository yet. For now:

1. Build from source (see [Development](#development) below)
2. In FFXIV with Dalamud loaded: `/xlsettings` → Experimental → Dev Plugin Locations → add the path to `ClubFFXIV/bin/x64/Debug/ClubFFXIV.json`
3. Reload plugins via `/xlplugins` → Dev Tools tab → Load Dev Plugin

## Quick start

### Listener

1. `/club config` opens the panel
2. Paste a stream URL — try `https://www.youtube.com/watch?v=9Tzc3ybp8vA` to test
3. Click Play

Auto-discovery works as soon as you walk into a registered house or near a calibrated club's plot. The default registry URL is preconfigured so it just works.

### DJ

**Already streaming on Twitch / YouTube?** Just share your channel URL. Listeners paste it.

**Want to publish your house** so visitors auto-tune when they enter:

1. Stand inside your house, open `/club config`
2. Paste your stream URL
3. Click **Publish to registry**
4. Walk outside to your front door, find your house in the Published Houses list, click **Calibrate door**

Friends within ~40m of your door will hear muffled music as they approach.

**Don't have a stream yet?** See [docs/DJ-BROADCASTING.md](docs/DJ-BROADCASTING.md) for setup options ranging from $0 (Cloudflare Tunnel + local Icecast) to fully managed.

## Documentation

- **[DJ Broadcasting Guide](docs/DJ-BROADCASTING.md)** — set up your own radio station, free and paid options

## Architecture

- **Plugin** (.NET 8 + Dalamud) — audio chain, housing detection, spatial mixing, ImGui UI
- **Backend** (Cloudflare Workers + KV) — small registry mapping house → stream URL with Ed25519-signed updates
- **Audio pipeline:**
  - Direct streams: `HttpAudioReader` (NLayer for MP3, NVorbis for OGG) → BiQuad lowpass → VolumeSampleProvider → WaveOut
  - Twitch / YouTube: yt-dlp resolves the URL, ffmpeg decodes to PCM piped over stdout, fed into the same chain

## Development

Requirements: Windows, .NET 8 SDK, XIVLauncher with Dalamud installed.

```bash
git clone https://github.com/electr0sheep/ClubFFXIV
cd ClubFFXIV
dotnet build -c Debug
```

The backend is in `backend/` (Cloudflare Worker + TypeScript). Deploy with `wrangler deploy` after configuring `wrangler.toml` with your KV namespace IDs.

## Caveats

- **Dalamud plugins technically violate FFXIV's ToS.** Square Enix's stated stance is no third-party tools; the practical stance is "don't be visible about it." Use is at your own risk.
- **Twitch / YouTube playback** has ~5–10s latency (HLS) — listeners are roughly synced with each other but a few seconds behind the live source.
- **Music licensing** is the DJ's responsibility — streaming copyrighted music to listeners is technically a broadcast and requires licensing. Royalty-free or your own original mixes sidestep this. The plugin author isn't responsible for what DJs choose to stream.
- **House ownership check is best-effort** — runs against the game's local state, can be bypassed by a forked client. Acceptable for a small social plugin; not a defense against determined abuse.

## License

MIT

## Acknowledgements

- [Dalamud](https://github.com/goatcorp/Dalamud) and [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) — plugin framework and launcher
- [NAudio](https://github.com/naudio/NAudio), [NLayer](https://github.com/naudio/NLayer), [NVorbis](https://github.com/NVorbis/NVorbis) — audio I/O and managed decoders
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [ffmpeg](https://ffmpeg.org/) — Twitch / YouTube extraction and decoding
- The FFXIV venue community for being creative enough to make this worth building
