# FateFrenzy ⚡

[![License: AGPL 3.0](https://img.shields.io/badge/License-AGPL_3.0-blue.svg)](https://opensource.org/licenses/AGPL-3.0)
[![Dalamud: API 14](https://img.shields.io/badge/Dalamud-API%2014-blue.svg)](https://dalamud.dev/)

**FateFrenzy** is an advanced, fully-automated FATE farming plugin for Final Fantasy XIV (Dalamud). It features a modern, custom-designed Holographic Neon Cyan user interface that coordinates movement, combat, instances, and cross-server world hops in a unified, hands-off loop.

---

## Key Features 🚀

### 🤖 Intelligent FATE Farming
- **Auto-Navigation & Combat**: Automatically teleports to active FATEs, paths to objectives using `vnavmesh`, auto-targets combatants, syncs level, and engages.
- **Multiclass Queue**: Automatically switches gearsets on startup and advances to the next class in your queue when one hits its level cap.

### 🌌 Dynamic Server & Instance Rotation
- **Instance Hopping**: Automatically switches instances (Instance 1, 2, 3) inside a zone if no eligible FATEs are active.
- **World Rotation**: Teleports to other servers on your data center using `Lifestream` when all FATEs in your zone rotation are cleared, waiting just 10 seconds of idle time.

### 🐦 Automated Chocobo Companion System
- **Chocobo Summoning**: Monitors companion timer and automatically summons your chocobo out of combat.
- **Auto-Restock (Gysahl Greens)**: Teleports to Limsa Lominsa Lower Decks when out of Gysahl Greens, paths to Bango Zango, purchases 99 greens, closes the merchant interface, and teleports back to continue farming.

### 💎 Bicolor Gemstone Auto-Trading
- **Voucher & Item Purchases**: Automatically teleports to Bicolor Gemstone merchants and purchases vouchers or items when your wallet hits a custom threshold.
- **Smart Reserves**: Allows you to configure a gemstone reserve to keep in your wallet.

### 🛠️ Logistics & Humanizer
- **Auto-Repair**: Repairs gear automatically using Dark Matter or by visiting a repair NPC when durability drops below your threshold.
- **Consumable Upkeep**: Automatically eats food and drinks potions to maintain experience buffs.
- **Humanizer Breaks**: Simulates human breaks by occasionally taking you to random city hubs to wander around before resuming.
- **Party Invite Decliner**: Automatically declines incoming party invites after a random delay.
- **GM Alert System**: Detects nearby Game Masters and triggers custom reactions (sound alerts, stopping the bot, or immediate client termination).

---

## Required Plugins (Dependencies) 📦

For FateFrenzy to operate fully, you should have the following Dalamud plugins installed:
1. **vnavmesh**: Handles 3D mesh navigation and pathfinding.
2. **BossMod** or **RotationSolver**: Handles active combat rotations and positioning.
3. **Lifestream**: Required for changing instances and executing world rotations.
4. **TextAdvance**: Handles dialogue progression and auto-skipping during merchant interactions.

---

## Installation & Commands ⚙️

1. Clone or download this repository.
2. Open the solution file `FateFrenzy.sln` and compile the project using Visual Studio or Rider.
3. Add the compiled `FateFrenzy.dll` path as a developer plugin in Dalamud.
4. Open the plugin interface in-game with:
   - `/fatefrenzy` (primary command)
   - `/ff` (alias)

### Command Parameters:
- `/ff config` — Open configuration settings.
- `/ff stats` — Open run history and statistics.
- `/ff deps` — Open dependency checker.
- `/ff pause` — Pauses or resumes the active loop.

---

## Credits & Source 📝
This project is a modified fork of the original **FFXIV-AutoFATEGrind** plugin developed by [XeldarAlz](https://github.com/XeldarAlz).
- Original Repository: [XeldarAlz/FFXIV-AutoFATEGrind](https://github.com/XeldarAlz/FFXIV-AutoFATEGrind)
- In compliance with the copyleft terms of the **AGPL-3.0-or-later** license, this repository is open-source and publicly accessible.

---

## License 📄
This project is licensed under the AGPL-3.0-or-later License.
