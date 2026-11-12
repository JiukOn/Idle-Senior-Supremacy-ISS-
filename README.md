# Idle Senior Supremacy (ISS)

> A modern Steam games viewer and manager for card farming, playtime idling, and achievement editing.

**Idle Senior Supremacy (ISS)** is a Windows desktop application built with **.NET 9** and **WPF UI (Fluent Design)**, bringing together complete Steam account management in a single modern interface.

---

## ✨ Features

- **Dashboard & Account Analytics:**
  - Steam level, XP progress bar, badges, and account standing (VAC, Trade Ban, Game Bans).
  - Library breakdown (never played, `< 5h`, `5h+`, total playtime).
  - Real-time library valuation (**Today's Prices**, **Full Value**, **Average Price per Game**, **Average Price per Hour**) via batch Steam Store queries.
  - Random game picker ("What to play today?") and recent news from your games.
- **Card Farming:**
  - Simultaneous mode (batches of up to 25 games under 2 hours) and sequential mode, with minimum-hour filter, blocklist, and automatic remaining-drop checks.
- **Hours Farming:**
  - Simultaneous idling (up to 32 active games on Steam) or sequential (`1 at a time`).
  - Dedicated **Hours Farm Queue** with live elapsed time, configurable target hours, priority reordering (`▲ / ▼`), **Start All**, **Stop All**, and batch adding.
- **Library & Game Panel:**
  - Search, filters, high-definition banners, current store price with discount badge (`-XX%`), **View on Steam** (`steam://store/...`) button, completion time estimates (HLTB / SteamHunters), and Anti-Cheat advisories.
- **Organic Achievement Manager:**
  - Unlock/lock achievements with organic scheduling and safeguards against accidental changes.
- **Full Customization:**
  - **Languages:** English (US) and Português (Brasil).
  - **Native Steam Currencies:** `USD ($)`, `BRL (R$)`, `EUR (€)`, and `GBP (£)` without artificial conversion.
  - **Themes:** `Dark`, `Light`, `Spring`, `Dracula`, and `Midnight`.

---

## 📦 Getting Started (End Users)

1. Download the latest release from the [**Releases**](https://github.com/JiukOn/Idle-Senior-Supremacy-ISS-/releases) page.
2. Extract the `.zip` archive to a folder of your choice.
3. Make sure the **Steam Desktop Client** is running and logged into your account.
4. Launch the app using the **`ISS`** shortcut (or `config\IdleSenior.UI.exe`).

> **Windows SmartScreen Notice:** Because this project is distributed for free without a paid code-signing certificate, Windows may show a SmartScreen warning on first launch. Click *"More info"* → *"Run anyway"*.

---

## 🛠️ Building from Source (Developers)

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10/11

### Automated Build & Packaging
Run the build script from the repository root:

```bat
build.bat
```

The script performs:
1. `dotnet build` in `Release` mode (verifying 0 warnings and 0 errors);
2. `dotnet publish` *self-contained* (`win-x86`) into `publish\IdleSeniorSupremacy-win-x86\config\`;
3. Organization of `runtimes\` and `config\` folders, workers (`IdleSenior.Worker.exe`, `IdleSenior.AchievementWorker.exe`), and `steam_api.dll`;
4. Creation of the `ISS.lnk` shortcut and a ready-to-distribute `.zip` package in `publish\`.

---

## 📄 License

Copyright © 2026 **JiukOn**. All rights reserved.  
See [`LICENSE`](LICENSE) for full terms.
