# DJ Broadcasting Guide

How to stand up an internet radio station that ClubFFXIV listeners can tune into.

## How it works

```
  Your PC                                Your stream server                        Listeners
  ┌──────────────────┐    push MP3      ┌──────────────────────┐    HTTP pull   ┌─────────┐
  │ Mixxx / butt /   │ ───────────────► │ Icecast / Shoutcast  │ ──────────────►│ player  │
  │ OBS  (the source)│   (one feed)     │ (fans out to many)   │   (one URL)    │ player  │
  └──────────────────┘                  └──────────────────────┘                │ player  │
                                                                                 └─────────┘
```

Three pieces:

1. **A source** — software on your PC that produces audio and pushes it to your stream server.
2. **A stream server** — Icecast or Shoutcast. Takes one source feed, fans out to many listeners.
3. **A public URL** — e.g. `https://stream.example.com/club.mp3`. Paste this into ClubFFXIV.

You need to run (1) yourself; (2) can be self-hosted on a cheap VPS or rented from a managed provider.

## Pick a path

| Path | Cost | Effort | Best for |
|---|---|---|---|
| **Local PC + Cloudflare Tunnel** | **$0** | low–medium | run on your gaming PC, no cloud signup |
| **Oracle Cloud Free Tier + Icecast** | **$0** | medium | always-on, dedicated, real free hosting |
| **Self-hosted Icecast on a $5 VPS** | $5/mo | medium | reliable, no risk of free-tier termination |
| **Azuracast self-hosted on a VPS** | $5/mo | medium (more features) | want a web UI to manage your station |
| **Managed: Azuracast Cloud / Radio.co / Centova** | $15–25/mo | low | non-technical, set-and-forget |
| **Free tiers (Caster.fm, ZenoLive)** | $0 | low | testing only — bitrate caps, ads, instability |

The rest of this guide walks through the **self-hosted Icecast** path on a paid $5 VPS. The two free paths use the same Icecast + Mixxx setup; only the hosting differs (see [Free options](#free-options) below).

## Free options

### Local PC + Cloudflare Tunnel (recommended free path)

Run Icecast on your own PC, expose it publicly via Cloudflare Tunnel. Your PC needs to be on while broadcasting (probably already is, you're playing FFXIV).

**Bandwidth ceiling:** at 128 kbps per listener, ~50 listeners need 6.4 Mbps upload — fits within most home internet plans. Check your upload speed before assuming it scales.

**Setup:**

```bash
# Install Docker Desktop (Windows/Mac) — https://www.docker.com/products/docker-desktop/
# Run Icecast locally:

docker run -d --name icecast -p 8000:8000 \
  -e ICECAST_SOURCE_PASSWORD=changeme_source \
  -e ICECAST_ADMIN_PASSWORD=changeme_admin \
  -e ICECAST_HOSTNAME=localhost \
  libretime/icecast:2.4.4

# Verify locally:
curl http://localhost:8000/status.xsl    # should return HTML
```

**Expose via Cloudflare Tunnel:**

```bash
# Install cloudflared:
#   Mac:     brew install cloudflared
#   Windows: winget install --id Cloudflare.cloudflared
#   Linux:   see https://pkg.cloudflare.com

cloudflared tunnel login
cloudflared tunnel create clubffxiv
cloudflared tunnel route dns clubffxiv stream.yourdomain.com
cloudflared tunnel run --url http://localhost:8000 clubffxiv
```

(Need your own domain on Cloudflare DNS — you already have one for the registry, reuse it with a subdomain.)

Your stream URL is now `https://stream.yourdomain.com/clubffxiv.mp3` once Mixxx connects to mountpoint `/clubffxiv.mp3`. Free, encrypted, public.

**Caveats:**
- PC sleep / shutdown = stream offline
- Home upload bandwidth = listener cap
- Cloudflare may rate-limit very long-running tunnels (rare in practice)

### Oracle Cloud Free Tier

Oracle's "Always Free" tier gives 4 ARM cores + 24 GB RAM **with no time limit**. Identical setup to the $5 VPS guide below — just provision an Ampere A1 instance instead of paying for one.

**Pros:** dedicated, always on, real server. **Cons:** signup requires a credit card (not charged), and Oracle has a reputation for terminating free accounts that sit idle. Set up monitoring or accept the risk.

Sign up: https://www.oracle.com/cloud/free/

### Hosted free tiers (last resort)

These exist but compromise audio quality, inject ads, or limit listener counts:

- **Caster.fm free tier** — 64 kbps cap, audio ads
- **ZenoLive free** — bitrate cap, ads, periodic disconnects
- **Render / Fly.io free tiers** — services spin down on inactivity, breaks streaming

Fine for "test for an hour", not fine for a real club.

### What doesn't work

YouTube Live, Twitch, and Discord aren't compatible with ClubFFXIV — they use HLS/RTMP/WebRTC, not Icecast/MP3. The plugin's audio reader expects an Icecast-style MP3 or OGG stream. HLS support could be added in a future version but isn't today.

---

---

## 1. Stream server: Icecast on a $5 VPS

### Provision a server

Any cheap Linux VPS works. DigitalOcean, Hetzner, Vultr, Linode — all $4–6/mo for a 1 GB droplet, which is plenty for a few dozen listeners.

```bash
# SSH in
ssh root@your-vps-ip

# Install Docker
curl -fsSL https://get.docker.com | sh
```

### Run Icecast in Docker

Create `/opt/icecast/docker-compose.yml`:

```yaml
services:
  icecast:
    image: libretime/icecast:2.4.4
    restart: unless-stopped
    ports:
      - "8000:8000"
    environment:
      ICECAST_SOURCE_PASSWORD: "CHANGE_ME_source"
      ICECAST_RELAY_PASSWORD:  "CHANGE_ME_relay"
      ICECAST_ADMIN_PASSWORD:  "CHANGE_ME_admin"
      ICECAST_ADMIN_USERNAME:  "admin"
      ICECAST_ADMIN_EMAIL:     "you@example.com"
      ICECAST_LOCATION:        "Earth"
      ICECAST_HOSTNAME:        "stream.example.com"
      ICECAST_MAX_CLIENTS:     "100"
      ICECAST_MAX_SOURCES:     "5"
```

Replace the three passwords (different ones — `source` is what Mixxx will use, `admin` is for the web UI).

Start it:

```bash
cd /opt/icecast
docker compose up -d
```

Verify: `curl http://your-vps-ip:8000/status.xsl` — should return HTML.

### Add HTTPS

Plain HTTP technically works but ClubFFXIV won't autoplay over HTTP from secure pages, and you want a real domain anyway. Easiest: point Cloudflare in front of it.

1. In your DNS provider, point `stream.example.com` (A record) at your VPS IP.
2. In Cloudflare DNS, set the proxy status to **proxied (orange cloud)**.
3. In Cloudflare → SSL/TLS → set mode to **Flexible** (Cloudflare → you over HTTP, listener → Cloudflare over HTTPS). For real production, use **Full (strict)** with Let's Encrypt on the VPS via nginx.

Now `https://stream.example.com/...` works publicly.

> **Note on ports:** Icecast listens on 8000 by default. Cloudflare proxy only handles 80/443. Either:
> - Run nginx on the VPS that proxies 443→8000 locally and use Cloudflare on top, OR
> - Open Icecast directly on 80 (`docker-compose` port `80:8000`) and proxy through Cloudflare.

## 2. Pick a mountpoint

Icecast streams live at a "mountpoint" — a path under your server URL. Convention: name it after your station.

Your full stream URL becomes:

```
https://stream.example.com/clubffxiv.mp3
```

This is what listeners will connect to. You don't pre-create the mountpoint — it appears when Mixxx connects.

## 3. Mixxx (the DJ deck)

[Mixxx](https://mixxx.org) is free, open-source DJ software. Has decks, crossfading, BPM matching, effects, library management — basically a virtual CDJ setup. Works on Windows, Mac, Linux.

### Install

Download from https://mixxx.org/download/ — install normally.

### Add your music library

Preferences → Library → add your music folders. Mixxx scans MP3/FLAC/OGG/etc.

### Configure broadcasting

Preferences → Live Broadcasting → enable.

Click **Add Connection** and fill in:

| Field | Value |
|---|---|
| Type | Icecast 2 |
| Mount | `/clubffxiv.mp3` |
| Host | `stream.example.com` |
| Port | `443` (if using HTTPS via Cloudflare) or `8000` (direct) |
| Login | `source` |
| Password | the `ICECAST_SOURCE_PASSWORD` you set above |
| Format | MP3 |
| Bitrate | 128 kbps (good balance) or 192 kbps (cleaner) |
| Channels | Stereo |
| Stream name | "Your club name" |

Save.

### Go live

Top of Mixxx window → click **broadcast** icon (or Options → Enable Live Broadcasting).

Mixxx will connect to Icecast. The master deck output is now being streamed.

Verify it's working: open the stream URL in VLC or a browser tab — should hear what Mixxx is playing.

## 4. Plug into ClubFFXIV

In your house in-game, open `/club config`:

1. **Stream URL** field: `https://stream.example.com/clubffxiv.mp3`
2. Click **Save URL for this house (local)** — auto-plays for you when you enter
3. Click **Publish to registry** — visible to other plugin users
4. Walk outside, click **Calibrate door** to enable spatial proximity

Listeners with the plugin entering your house will now hear your stream.

## Alternative source software

Mixxx is the most full-featured but heavier than you may want.

- **butt** ([Broadcast Using This Tool](https://danielnoethen.de/butt/)) — minimal, just streams whatever your system audio capture is. Good if you want to play music in iTunes/Spotify and rebroadcast it. Free, cross-platform.
- **OBS Studio** — overkill but works if you already use it for streaming. Add a Browser Source for music + an audio output filter.
- **VirtualDJ / Serato / Rekordbox** — paid DJ apps, all support Icecast broadcasting. Better effects/library than Mixxx if you're already paying for them.
- **Liquidsoap** — script-driven, headless, runs on the server alongside Icecast. Good for "always-on radio" with auto-DJ playlists. No PC required. Steeper learning curve.

## Music licensing — the legal bit

Streaming copyrighted music to listeners is legally a *broadcast*, not personal use, and technically requires licensing.

**In practice:**
- Small Discord communities and tiny internet radios largely operate in a gray area
- Royalty-free music (Pixabay, Free Music Archive, NCS, Bandcamp Creative Commons) sidesteps it entirely
- Your own original music is always fine
- DMCA takedowns target streamers when they get noticed (large audiences); unlikely for an in-game club with 5 friends

**If you want to be legitimate (US):**
- [SoundExchange](https://www.soundexchange.com/) for digital performance royalties
- ASCAP / BMI / SESAC for songwriter royalties
- Combined cost: ~$500/year minimum for small webcasters

**Practical recommendation for an FFXIV housing club:** stick to royalty-free or Creative Commons music, or your own mixes. Note in your description what you play so listeners know.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Mixxx says "Connection failed" | wrong host/port/password | double-check, test from the VPS itself first (`curl http://localhost:8000/admin/`) |
| Stream URL works in VLC, silent in ClubFFXIV | format Cloudflare/proxy is changing | confirm `Content-Type` is `audio/mpeg`; VLC tolerates more than ClubFFXIV |
| Listeners hear gaps / cutouts | source disconnected briefly | check Mixxx's network; for production reliability use a wired connection |
| Stream connects but no audio | Mixxx master output is muted or no deck is loaded | load a track, push the volume up |
| Stream URL is HTTPS but plugin can't connect | cert issue (esp. on Wine) | see ClubFFXIV README about Wine TLS limitations; try the Icecast `*.workers.dev`-equivalent direct host |

## Operating your station

- **Going off-air:** stop broadcasting in Mixxx. Listeners' streams will end; the plugin handles this gracefully.
- **Auto-DJ when you're not playing:** run Liquidsoap on the server with a playlist of your library — stream stays alive 24/7, you just take over when you want to mix live.
- **Multiple DJs:** each runs Mixxx, each connects to a different mountpoint (`/dj1.mp3`, `/dj2.mp3`). Or schedule with Liquidsoap as a master mixer.
- **Listener count:** Icecast's admin page (`https://stream.example.com/admin/` with the admin password) shows current listeners per mountpoint.

## Quick start checklist

- [ ] Pick a hosting path (self-hosted VPS or managed)
- [ ] Stream server running, public URL works in a browser
- [ ] Mixxx installed and library scanned
- [ ] Mixxx broadcast configured, status shows "Connected"
- [ ] Stream URL plays in VLC
- [ ] Stream URL pasted into ClubFFXIV `/club config`
- [ ] Saved to your house and/or published to registry
- [ ] Door calibrated for outdoor proximity
- [ ] Tested with a friend or alt character
