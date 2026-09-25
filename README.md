# SC Contract Tracker

**English** · [Čeština](README.cs.md)

A Windows desktop app for **Star Citizen** that tracks your accepted contracts,
measures how long they take and calculates your **earnings in aUEC per hour**.

> Unofficial fan-made tool, not affiliated with Cloud Imperium Games.
> The app does not modify the game, does not read its memory and sends nothing to the game.

## Features

- **Automatic contract tracking** – accepted, completed, failed, abandoned and ended on logout, with exact times.
- **Active contracts overview** – compact cards with contract type, contractor, reward, time accepted
  and the **current objective** (what to do and where). Once an objective is done, the next one shows up right away.
  Expand a card to see all details.
- **Reward, contractor, description and objectives** are read from the mobiGlas when you open *Contracts › Accepted*.
- **Acceptance fee** and **net earnings** (reward − fee).
- **3 nearest locations** for each contract and what to do there.
- **History** – colour-coded by outcome, grouped by type / contractor / title, expandable to the full card,
  editing, deleting, time period filter.
- **Statistics** – number of contracts, success rate, earnings, average and median reward, durations, **aUEC/h**
  and best contracts, overall and per group.
- **Blueprints and items** – everything you have earned from contracts, with search.
- **Manual entry and editing** of a contract, including all details.
- **AUTO / MANUAL completion mode** – a contract ends on its own based on the game, or only when you press a button in the app.
- **Import of older history** from previous game sessions.
- **Customisable tables** – show/hide columns, width and order (right-click the header).
- **Automatic updates** – new versions download by themselves.

## Installation

1. Download **`ScContractTracker-win-Setup.exe`** from [Releases](../../releases) and run it.
   No administrator rights are needed.
   A shortcut is created in the Start menu; on first launch the app asks whether you also want a desktop shortcut.
2. On first launch, check the game path in *Settings › Preferences*
   (default `C:\Program Files\Roberts Space Industries\StarCitizen\LIVE`) and the game language.
3. Play. You can keep the app open next to the game or on a second monitor.

**Requirements:** Windows 10 (version 2004 or newer) or Windows 11.

## Tips

- **Contract reward and description:** after accepting a contract, open *Contracts › Accepted* in the mobiGlas and click the contract.
  The app reads the details on its own. You can also trigger it manually via *Help › Read mobiGlas now*.
- **Game display mode:** Windowed, Borderless and Fullscreen all work. If the status bar reports a black image,
  press **PrtScn** in the game on the Accepted tab – the app will read the screenshot.
- **Text recognition:** the app uses Windows text recognition (OCR). If it is missing for your game language, the app
  installs it by itself (Windows asks for confirmation once). You can run it later from *Settings › Install OCR for game language*.
- **Location for nearest places:** type `/showlocation` in the in-game chat – the app picks up the coordinates from the clipboard.
- **Older contracts:** *File › Import history from logbackups…* loads contracts, blueprints and items from previous sessions.
- **Game crash:** accepted contracts stay active (just like in the game). Logging out or quitting the game via the menu ends them.
- **If the app reads wrong values:** use *Help › Save OCR diagnostics* and send the saved files in [Issues](../../issues).

## Languages

In *Settings* you set the **app language** and the **game language** (the language you play in) separately.
English, Czech and German are built in.

## Data and privacy

Everything stays on your computer in `%AppData%\ScContractTracker` (history, settings).
The app sends nothing; it only connects to GitHub to check for new versions (can be turned off in settings).

## Bugs and ideas

Report them in [Issues](../../issues).

## License

MIT © 2026 Jiří Libich.
Star Citizen® is a trademark of Cloud Imperium Rights LLC. This project is not affiliated with Cloud Imperium Games.
