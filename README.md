# ClubFFXIV

A Dalamud plugin that turns FFXIV housing into spatial-audio venues. Tune any house to an internet radio stream and walk past it to hear muffled music drift through the door — step inside, full audio, the game's BGM ducks out of the way.

## What it does

- Stand in a ward near a registered club → hear the music through the wall, panned toward the door and getting clearer as you approach.
- Step inside → full audio, no muffle, your house's stream auto-tunes.
- Walk away → fades out and the stream tears down.

Listeners point ClubFFXIV at any of:

- **Twitch channel** — `twitch.tv/yourchannel`
- **YouTube** — videos, live streams, and playlists
- **SoundCloud / Mixcloud / Twitcasting / Niconico**
- **Icecast / Shoutcast / direct MP3 or OGG**

DJs who already broadcast on Twitch or YouTube don't change anything — listeners just paste the channel URL.

## Features

- **Spatial proximity audio** — distance-based volume + lowpass; doors pan L/R relative to the camera and pick up extra muffle when behind you. Streams pre-buffer before they're audible so audio is ready the moment you cross the threshold.
- **Multi-stream outdoor mode** — optional: mix multiple nearby clubs at once, each weighted by its own distance.
- **Auto-discovery** — registered houses auto-tune when you enter; nearby clubs auto-play muffled when you approach outdoors.
- **Now Playing player** — per-row mute, pause/skip, seek bar, thumbnails, blacklist. Loop + random-order toggles for YouTube playlists.
- **Public directory** — browse a filterable, sortable list of publicly-listed clubs via `/pclub directory`.
- **Password-protected clubs** — DJs can gate their club with an auto-generated EFF diceware passphrase; the registry only ever sees an Argon2id hash + salt.
- **Local overrides** — bind a private URL to any plot for your-eyes-only auto-play, separate from anything published to the registry.
- **DJ identity** — Ed25519 keypair signs your publishes; only you can update or delete your club.
- **House-ownership check** — local FFXIVClientStructs check blocks accidental publishing of plots you don't own.
- **URL permissioning** — first-time domains/URLs prompt for approval; allow / block lists are persistent.
- **Focus-aware muting** — follows FFXIV's own "Play sounds when window is not active" setting, no separate toggle.
- **Cross-platform** — works on Windows, and via XIVLauncher on Mac/Linux through Wine.

## Install

Not in the official Dalamud repository yet. For now:

1. Build from source (see [Development](#development) below).
2. In FFXIV with Dalamud loaded: `/xlsettings` → Experimental → Dev Plugin Locations → add the path to `ClubFFXIV/bin/x64/Debug/ClubFFXIV.json`.
3. Reload plugins via `/xlplugins` → Dev Tools tab → Load Dev Plugin.

A first-run **Setup Wizard** handles domain pre-approval (Twitch, YouTube, SomaFM, SoundCloud), the one-time download of helper binaries (~123 MB: yt-dlp + ffmpeg + Deno), and the yt-dlp auto-update opt-in. Nothing is downloaded silently — you click Install.

## Quick start

`/pclub` opens the music player. The title-bar `?` button opens the in-game Getting Started window with Listener / DJ / FAQ walkthroughs; what follows is the tldr.

### Listener

1. `/pclub` → title-bar `+` → paste a stream URL → **Play**.
2. To test: `https://www.youtube.com/watch?v=9Tzc3ybp8vA` (YouTube), `https://ice1.somafm.com/groovesalad-128-mp3` (SomaFM, no helper binaries needed).
3. To browse what's already published: `/pclub directory`.

Auto-discovery is on by default — walk into a registered house and the stream auto-plays; approach an outdoor plot with a calibrated door and you'll hear it muffled, panned in the right direction. The default registry URL is preconfigured; nothing to set up.

### DJ

Already streaming on **Twitch or YouTube?** Share your channel URL — that's it. Listeners paste it and your house auto-tunes them in.

Want to **publish your house** so visitors auto-tune when they enter:

1. Stand inside your house, open `/pclub` → ⚙ → **My Clubs**.
2. **Publish new club** → name + URL → optionally **Password-protect** to gate the stream → **Publish**.
3. Walk outside to your front door, find your house in the My Houses table, click the **crosshair icon** to calibrate the door position.

Friends within ~40 yalms of your door will hear muffled music as they approach. To stay off the public browse list while still letting friends with the plot key (or who walk past) discover your club, uncheck **Show in public directory** when you publish.

**Don't have a stream yet?** See [docs/DJ-BROADCASTING.md](docs/DJ-BROADCASTING.md) for options from $0 (Cloudflare Tunnel + local Icecast) to fully managed.

### Local overrides

If you want a private URL bound to a plot — your-eyes-only, never published — use **Create local override** in the same tab. Useful for personal house playlists you don't want others to discover, or for testing a URL before publishing it.

## Slash commands

| Command | What it does |
|---|---|
| `/pclub` | Open the music player. |
| `/pclub play <url>` | Start a stream. |
| `/pclub stop` | Stop playback. |
| `/pclub config` | Open the settings window. |
| `/pclub directory` (or `browse`) | Open the public directory. |
| `/pclub calibrate <plotKey>` | Calibrate a door without using the UI. |

## Documentation

- **[DJ Broadcasting Guide](docs/DJ-BROADCASTING.md)** — set up your own radio station, free and paid options.
- In-game: click the `?` button on the music player title bar for the Listener / DJ / FAQ tabs.

## Architecture

- **Plugin** (.NET 8 + Dalamud) — audio chain, housing detection, spatial mixing, ImGui UI.
- **Backend** (Cloudflare Workers + KV) — registry mapping `plotKey → club record`. Updates are Ed25519-signed; password-protected clubs ship Argon2id verification at the edge.
- **Helper binaries** (downloaded into the plugin config dir on demand) — yt-dlp, ffmpeg, Deno. Deno is required because yt-dlp uses it to solve YouTube's signature/n-challenge JavaScript.

**Audio pipeline**

- **Direct streams (Icecast / Shoutcast / MP3 / OGG):** `HttpAudioReader` (NLayer for MP3, NVorbis for OGG) → BiQuad lowpass → stereo balance → volume → `WaveOut`.
- **Twitch / YouTube / SoundCloud / etc.:** yt-dlp resolves the URL, ffmpeg decodes to PCM piped over stdout, fed into the same chain.

Outdoor proximity mode mixes N voices in parallel through `MultiStreamPlayer` when multi-stream is enabled; otherwise the single-voice path runs and only the closest in-range club is heard.

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

- Cloudflare Workers — serverless edge runtime.
- Workers KV — eventually-consistent key-value store.
- Ed25519 verification via Web Crypto (built into the Workers runtime).
- Argon2id for passphrase hashing (plaintext never leaves the DJ's machine — only the hash + salt + verifier do).

**Endpoints**

- `GET  /health` — liveness check.
- `GET  /time` — server time (for clock-skew debugging).
- `GET  /clubs` — public directory: all clubs whose DJ opted into the browse list.
- `GET  /clubs/:plotKey` — fetch a single club record. Password-protected records return `{passwordRequired, salt}`; the client derives a key and POSTs it to retrieve the actual stream URL.
- `POST /clubs/:plotKey` — publish/update (Ed25519-signed; first writer claims the plot). Body field `listed: false` hides the club from `GET /clubs` only — per-plot lookup and ward proximity still work.
- `DELETE /clubs/:plotKey` — unpublish (signed by the original DJ key).
- `GET  /wards/:worldId/:territoryType/:ward` — list all calibrated clubs in a ward (used for outdoor proximity discovery).

**Local dev**

```bash
cd backend
npm install
npx wrangler dev   # local server on http://127.0.0.1:8787
```

Hit it from a separate terminal (`curl http://127.0.0.1:8787/health` etc.) to verify routes.

**Deploy**

```bash
# 1. Copy the example config (wrangler.toml itself is gitignored so each
#    operator's IDs stay out of the repo).
cp wrangler.toml.example wrangler.toml

# 2. Provision the KV namespace (one-time)
npx wrangler kv namespace create CLUBS_KV

# 3. Paste the returned id into wrangler.toml under [[kv_namespaces]]

# 4. Deploy
npx wrangler deploy
```

`wrangler dev` runs in local mode by default and uses an in-process KV simulation, so a preview namespace isn't needed unless you explicitly run `wrangler dev --remote`.

Wrangler prints the public URL (e.g. `https://clubffxiv-registry.<account>.workers.dev`). Set this as the Registry URL in `/pclub` → ⚙ → Registry to point the plugin at your instance instead of the default.

**Cost**

- Workers free tier: 100k requests/day.
- KV free tier: 1 GB storage, 100k reads/day, 1k writes/day.
- Plenty for a small community; comfortable headroom for a few thousand active listeners.

**Schema**

- `club:{plotKey}` → JSON `ClubRecord` (streamUrl, displayName, description, djId, pubkey, updatedAt, optional door coordinates, `listed` flag, optional Argon2id hash + salt + verifier for passphrase-gated clubs).
- `ward:{worldId}:{territoryType}:{ward}` → JSON map of `{plotKey: WardIndexEntry}` for one-shot ward listing.
- `directory` → JSON map of `{plotKey: DirectoryEntry}` for the public browse list (`GET /clubs`).

The ward and directory indexes are maintained alongside the per-club record on every publish/delete. KV doesn't have transactions, so concurrent writes could race; for a low-traffic registry this is acceptable.

## Caveats

- **Twitch / YouTube playback** has ~5–10s latency (HLS) — listeners are roughly synced with each other but a few seconds behind the live source.
- **Music licensing** is the DJ's responsibility — streaming copyrighted music to listeners is technically a broadcast and requires licensing. Royalty-free music or your own original mixes sidestep this. The plugin author isn't responsible for what DJs choose to stream.
- **House-ownership check is best-effort** — runs against the game's local state, can be bypassed by a forked client. Acceptable for a small social plugin; not a defense against determined abuse.
- **Anyone can publish to the registry.** Club names and descriptions are written by whoever holds the DJ key for a plot — they are not verified identity. The URL approval prompt makes this explicit when you encounter an unfamiliar host.
- **Spotify / Apple Music are not supported** — DRM-encrypted, no legal route. Rebuild your playlist on YouTube or YouTube Music and paste that URL instead.
- **First Twitch / YouTube / SoundCloud play downloads helper binaries (~123 MB)** — one-time, gated behind the setup wizard. Direct Icecast / MP3 streams skip the download entirely.

## License

[Unlicense](LICENSE) — public domain. Do whatever you want with it.

## Acknowledgements

- [Dalamud](https://github.com/goatcorp/Dalamud) and [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) — plugin framework and launcher.
- [NAudio](https://github.com/naudio/NAudio), [NLayer](https://github.com/naudio/NLayer), [NVorbis](https://github.com/NVorbis/NVorbis) — audio I/O and managed decoders.
- [yt-dlp](https://github.com/yt-dlp/yt-dlp), [ffmpeg](https://ffmpeg.org/), [Deno](https://deno.com/) — extraction, decoding, and the JS runtime that solves YouTube's challenge scripts.
- [EFF large wordlist](https://www.eff.org/dice) — diceware passphrase generation for password-protected clubs.
