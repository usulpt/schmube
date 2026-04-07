# Schmube

Windows desktop IPTV player built with .NET WPF + LibVLCSharp.

## Run
```powershell
dotnet run --project .\Schmube.csproj
```

## Built-in configuration
Schmube reads `schmube.config.json` from the application folder. The file supports:
- `subscriptionUrl`
- `defaultGroup`
- `autoLoadOnStartup`
- `recordingsDirectory`

`defaultGroup` is a single channel group name. When channels load, Schmube automatically selects that group in the group filter if it exists.

If `recordingsDirectory` is empty, recordings are written to `%USERPROFILE%\Videos\Schmube`.

Recording stays on the same active playback session. Schmube does not open a second provider stream for recording, so switching channels replaces the current stream instead of recording in parallel.

## Group flags
Schmube reads `schmube.group-flags.json` from the application folder and uses it to decorate channels with local country flag images. It first tries to infer the country from the channel name prefix and falls back to the group name if the channel prefix does not resolve.

The mapping file is optional. Standard prefixes such as `PT`, `DE`, `NL`, `USA`, or full names like `Portugal` are resolved automatically. Use the JSON file for provider-specific aliases and forced matches.

Example:
```json
{
  "aliases": {
    "CHL": "CL",
    "UKM": "GB"
  },
  "rules": [
    {
      "match": "24/7",
      "matchMode": "Contains",
      "countryCode": "GB",
      "label": "United Kingdom"
    }
  ]
}
```

`aliases` maps any custom provider token to the real two-letter flag asset code used by the app. So if your provider uses `CHL: Canal 13`, adding `"CHL": "CL"` makes Schmube use the Chile flag. If you map to a country that is not already bundled, add the matching lowercase PNG to `Assets/Flags`, for example `Assets/Flags/cl.png`.

## Usage
1. Launch the app.
2. The configured subscription URL is preloaded automatically.
3. Click `Load Channels` or let auto-load run on startup.
4. The configured default group is auto-selected after load.
5. Filter further by search text, manual group selection, favorites, or recents.
6. Double-click a channel or click `Play Selected`.
7. Use `Shot` in the player window, or press `Ctrl+S`, to save the current frame to `%USERPROFILE%\Pictures\Schmube\Screenshots`.
8. Use `Start Rec` in the player window to record the current stream to a local `.ts` file.
9. Use `Open Folder` in the player window to jump directly to the recordings directory.

## Persistence
- App-level config: `schmube.config.json`
- Group-flag mapping: `schmube.group-flags.json`
- User settings, favorites, and recents: `%APPDATA%\Schmube\settings.json`
- Logo cache: `%LOCALAPPDATA%\Schmube\logos`
- Default recordings folder: `%USERPROFILE%\Videos\Schmube`



