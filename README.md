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
Schmube reads `schmube.group-flags.json` from the application folder and uses it to decorate groups with mapped country flags and display labels. The mapping file is optional. Standard IPTV group prefixes such as `PT|`, `DE|`, `NL|`, and any other leading two-letter `XX|` country code are resolved automatically. Keep the JSON file for exceptions or provider-specific aliases that do not follow that pattern.

## Usage
1. Launch the app.
2. The configured subscription URL is preloaded automatically.
3. Click `Load Channels` or let auto-load run on startup.
4. The configured default group is auto-selected after load.
5. Filter further by search text, manual group selection, favorites, or recents.
6. Double-click a channel or click `Play Selected`.
7. Use `Start Rec` in the player window to record the current stream to a local `.ts` file.
8. Use `Open Folder` in the player window to jump directly to the recordings directory.

## Persistence
- App-level config: `schmube.config.json`
- Group-flag mapping: `schmube.group-flags.json`
- User settings, favorites, and recents: `%APPDATA%\Schmube\settings.json`
- Logo cache: `%LOCALAPPDATA%\Schmube\logos`
- Default recordings folder: `%USERPROFILE%\Videos\Schmube`



