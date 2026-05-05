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

### Plugin

Requirements: Windows, .NET 8 SDK, XIVLauncher with Dalamud installed.

```bash
git clone https://github.com/electr0sheep/ClubFFXIV
cd ClubFFXIV
dotnet build -c Debug
```

Then point Dalamud at `bin/x64/Debug/ClubFFXIV.json` as a dev plugin (see [Install](#install)).

### Backend

The backend is a single Cloudflare Worker in `backend/` (TypeScript) backed by Workers KV. The default registry URL in the plugin points at the public instance; you only need to deploy your own if you want to run a private registry.

**Stack**
- Cloudflare Workers — serverless edge runtime
- Workers KV — eventually-consistent key-value store
- Ed25519 verification via Web Crypto (built into Workers runtime)

**Endpoints**
- `GET  /health` — liveness check
- `GET  /time` — server time (for clock-skew debugging)
- `GET  /clubs/:plotKey` — fetch a single club record by plot key
- `POST /clubs/:plotKey` — publish/update (Ed25519-signed; first writer claims the plot)
- `DELETE /clubs/:plotKey` — unpublish (signed by the original DJ key)
- `GET  /wards/:worldId/:territoryType/:ward` — list all calibrated clubs in a ward (used for outdoor proximity discovery)

**Local dev**

```bash
cd backend
npm install
npx wrangler dev   # local server on http://127.0.0.1:8787
```

Hit it from a separate terminal (`curl http://127.0.0.1:8787/health` etc.) to verify routes.

**Deploy**

```bash
# 1. Provision KV namespaces (one-time)
npx wrangler kv:namespace create CLUBS_KV
npx wrangler kv:namespace create CLUBS_KV --preview

# 2. Paste the two returned IDs into wrangler.toml under [[kv_namespaces]]

# 3. Deploy
npx wrangler deploy
```

Wrangler prints the public URL (e.g. `https://clubffxiv-registry.<account>.workers.dev`). Set this as the Registry URL in `/club config` to point the plugin at your instance instead of the default.

**Cost**
- Workers free tier: 100k requests/day
- KV free tier: 1 GB storage, 100k reads/day, 1k writes/day
- Plenty for a small community; comfortable headroom for a few thousand active listeners

**Schema**
- `club:{plotKey}` → JSON `ClubRecord` (streamUrl, displayName, djId, pubkey, updatedAt, optional door coords)
- `ward:{worldId}:{territoryType}:{ward}` → JSON map of `{plotKey: WardIndexEntry}` for one-shot ward listing

The ward index is maintained alongside the per-club record on every publish/delete. KV doesn't have transactions, so concurrent writes to the same ward could race; for a low-traffic registry this is acceptable.

## Caveats

- **Twitch / YouTube playback** has ~5–10s latency (HLS) — listeners are roughly synced with each other but a few seconds behind the live source.
- **Music licensing** is the DJ's responsibility — streaming copyrighted music to listeners is technically a broadcast and requires licensing. Royalty-free or your own original mixes sidestep this. The plugin author isn't responsible for what DJs choose to stream.
- **House ownership check is best-effort** — runs against the game's local state, can be bypassed by a forked client. Acceptable for a small social plugin; not a defense against determined abuse.

## License

[Unlicense](LICENSE) — public domain. Do whatever you want with it.

## Acknowledgements

- [Dalamud](https://github.com/goatcorp/Dalamud) and [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) — plugin framework and launcher
- [NAudio](https://github.com/naudio/NAudio), [NLayer](https://github.com/naudio/NLayer), [NVorbis](https://github.com/NVorbis/NVorbis) — audio I/O and managed decoders
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [ffmpeg](https://ffmpeg.org/) — Twitch / YouTube extraction and decoding
