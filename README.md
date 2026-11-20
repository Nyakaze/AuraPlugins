# Aura plugins

The official plugins for [Aura](https://github.com/Nyakaze/AuraPresence), the Rich Presence hub for Discord. Aura installs them from its Marketplace tab.

| Plugin | What it does |
|---|---|
| [Stremio](plugins/Aura.Presence.Stremio) | Shows what you watch in Stremio on your profile. Nothing to set up. |
| [AniList Sync](plugins/Aura.Presence.AniListSync) | Marks the anime episodes you watch as watched on your AniList. |
| [MyAnimeList Sync](plugins/Aura.Presence.MyAnimeListSync) | Marks the anime episodes you watch as watched on your MyAnimeList. |
| [Simkl Sync](plugins/Aura.Presence.SimklSync) | Marks the movies, episodes and anime you watch as watched on your Simkl. |
| [Netflix](plugins/Aura.Presence.Netflix) | Shows what you watch on Netflix in your browser on your profile. Needs the Aura browser extension. |
| [Roblox](plugins/Aura.Presence.Roblox) | Shows the Roblox game you play on your profile. Nothing to set up. |
| [Valorant](plugins/Aura.Presence.Valorant) | Shows your VALORANT mode, map, agent, score and party on your profile. Nothing to set up. |
| [League of Legends](plugins/Aura.Presence.League) | Shows your League of Legends queue, champion, skin, KDA and party on your profile. Nothing to set up. |

## How the repo works

[`meta.json`](meta.json) is the repo file Aura reads. It lists every plugin, its version, the Aura API version it needs, and where to download it, with a SHA-256 checksum. Each version's zip is a GitHub release.

**Releasing a plugin:** raise `<Version>` in its `.csproj` and push to `main`. The [release workflow](.github/workflows/release.yml) runs the checks, builds every plugin whose version isn't in `meta.json` yet, creates a release with its zip, and commits the updated `meta.json`. A push without a version bump releases nothing.

Never commit a `meta.json` you made yourself. Only the workflow writes it.

## Building

Plugins compile against the Aura API (`Aura.Presence.Sdk` and the `Aura.Presence.Services.*` libraries). Aura provides those at runtime, so they are referenced with `Private="false" ExcludeAssets="runtime"` and never shipped in a zip. Everything else a plugin needs goes in its zip.

The API projects come from an `AuraPresence` checkout next to this folder:

```
F:\Projects\AuraPresence
F:\Projects\AuraPlugins
```

Set `AuraRoot` to build against a checkout somewhere else. The workflow checks Aura out at the tag in `AURA_REF`. That tag is the oldest Aura these plugins work with; raise it when a plugin needs newer API.

- `dotnet build AuraPlugins.slnx`: a Debug build also copies each plugin into the sibling Aura's `bin\Debug\net10.0-windows10.0.19041.0\plugins\`, where a Debug Aura loads it ahead of installed plugins.
- `dotnet run pack.cs`: a dry run. It builds and zips into `dist/` and writes `dist/meta.json`; nothing is published.
- `dotnet run StremioChecks.cs`, `dotnet run AniListSyncChecks.cs`, `dotnet run MyAnimeListSyncChecks.cs`, `dotnet run SimklSyncChecks.cs`, `dotnet run NetflixChecks.cs`, `dotnet run RobloxChecks.cs`, `dotnet run ValorantChecks.cs` and `dotnet run LeagueChecks.cs` in `tests/`.

## Adding a plugin

1. Add `plugins/<AssemblyName>/` with a `.csproj` that has a `<Version>`, the plugin code, an `icon.png` (square, 128 px) and a `plugin.json`:

   ```json
   {
     "name": "Display name",
     "author": "You",
     "description": "One or two sentences for the Marketplace.",
     "category": "Video",
     "uses": ["What it reads, and why"],
     "showsActivity": true,
     "repoUrl": "https://github.com/…"
   }
   ```

2. The folder name, the assembly name and the dll name must match. Aura installs a plugin into `%LOCALAPPDATA%\AuraPresence\plugins\<AssemblyName>\<version>\`.

## Custom repos

Anyone can host a repo: a `meta.json` in this format at an https URL, with zips anywhere over https. In Aura, Marketplace → Repos → Add repo. A custom repo can't list a plugin under the same name as an official one.

[Building a plugin for Aura](https://github.com/Nyakaze/AuraPresence/blob/master/docs/PLUGINS.md) covers writing a plugin, the meta.json fields, and reusing this repo's tooling for your own repo.
