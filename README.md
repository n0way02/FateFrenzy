# FateFrenzy ⚡

[![License: AGPL 3.0](https://img.shields.io/badge/License-AGPL_3.0-blue.svg)](https://opensource.org/licenses/AGPL-3.0)
[![Dalamud: API 15](https://img.shields.io/badge/Dalamud-API%2015-blue.svg)](https://dalamud.dev/)
[![Web Tracker](https://img.shields.io/badge/Web%20Tracker-Online-green.svg)](https://fatefrenzytracker.discloud.app/)
[![Ko-fi Support](https://img.shields.io/badge/Support%20me-Ko--fi-ff5e5b.svg)](https://ko-fi.com/n0way02)

**FateFrenzy** is the most advanced, fully automated, and feature-rich FATE farming plugin for Final Fantasy XIV (Dalamud). Designed with a sleek Cyberpunk / Holographic Neon interface, it coordinates pathfinding, combat rotations, cross-instance hops, data-center world travel, multi-account voucher exchanges, Retainer market listing, and live web/Discord telemetry in a unified, 100% hands-off loop.

---

## 🌐 Live Web Tracker (Monitor FATEs Online)

FateFrenzy features an official companion web platform where you can track all your farming characters from anywhere (PC, tablet, or phone):

👉 **[https://fatefrenzytracker.discloud.app/](https://fatefrenzytracker.discloud.app/)**

* **Live Telemetry & GPS:** Real-time character coordinates, active zone, current instance, and target FATE progress.
* **Wallet & Stock Monitor:** Live count of Bicolor Gemstones, Bicolor Gemstone Vouchers, and special inventory items.
* **Daily & All-time Stats:** Track FATEs completed, gemstones earned today, and total active farming hours.
* **Lodestone Account Verification:** Link your characters safely and privately through official Lodestone bio verification.
* **Bilingual UI:** Full support for both English (🇺🇸) and Portuguese (🇧🇷).
* **One-Click Access:** Open the web tracker directly from the in-game FateFrenzy toolbar (Globe icon 🌐).

---

## 🔔 Discord Webhook Alerts & Notifications

Never worry while AFK! FateFrenzy can send rich, instantaneous Discord webhook notifications directly to your private server or channel:

* 📩 **Incoming Whispers (`/tell`):** Receive instant alerts containing the sender's character name, home world server, and the full message content.
* 💌 **Party Invites:** Get alerted when another player invites you to a party, showing their name, server, and whether auto-decline is handling it.
* 🚨 **Game Master (GM) Alerts:** Immediate red-flag urgency alert if a Game Master is detected nearby in your zone.
* 🏆 **Goal Completed:** Comprehensive run summary when your target gemstone count, FATE quota, or mount voucher threshold is reached.
* ⏹️ **Session Interrupted:** Notification if the bot stops due to disconnected state or blockers.
* 🔒 **Active Only While Farming:** Discord alerts automatically turn on when you click **Start** and sleep when you **Stop**, ensuring you never receive spam during manual gameplay.
* ⚡ **Test Webhook Button:** Easily verify your Discord connection with one click in the plugin settings.

---

## 🚀 Key Features

### 🤖 Intelligent FATE Farming & Pathfinding
* **Full Expansion Coverage:** Supports Dawntrail, Endwalker, Shadowbringers, Stormblood, Heavensward, and A Realm Reborn zones.
* **Smart Travel & Secret Pathing:** Automatic aetheryte teleportation, including special navigation for tricky maps without a native aetheryte (e.g. *The Dravanian Hinterlands* navigated seamlessly via Idyllshire's Prologue Gate).
* **Combat & Position Sync:** Automatic 3D navigation using `vnavmesh`, target selection, level synchronization, and rotation coordination with `RotationSolver` or `BossMod`.
* **Multi-Class Queue:** Configure gearsets to level multiple jobs back-to-back; the bot automatically swaps to the next class in your queue once the current one hits its level cap.

### 🌌 Server & Instance Hopping
* **Instance Rotation:** Intelligently switches between instances (Instance 1, 2, 3) inside a zone if no eligible FATEs remain active.
* **Cross-World Rotation:** Uses `Lifestream` to travel across servers in your Data Center when all FATEs in your zone rotation are cleared.

### 🎫 Automated Mount Exchange & Retainer Market Seller
* **Multi-Account / Solo Voucher Trading:** Coordinates voucher handoffs between farmer characters and a buyer character in Mor Dhona.
* **Ty'aitya Whistle Exchange:** Automatically walks to Edelina in Mor Dhona and trades 500 Turali Bicolor Gemstone Vouchers for the rare **Ty'aitya Whistle** mount.
* **Automated Market Board Seller:** Travels to your Home World, enters a Private Inn room, summons your Retainer, scans current market board pricing, undercuts competitors by 1 gil, and lists the mount for sale automatically!

### 🐦 Companion Chocobo Automation
* **Chocobo Summoning:** Automatically summons your chocobo companion out of combat and maintains your preferred stance (Healer, Attacker, Defender, Free).
* **Auto-Restock Gysahl Greens:** Automatically travels to Limsa Lominsa Lower Decks (Bango Zango) to restock 99 Gysahl Greens when running low, then teleports back to resume farming.

### 💎 Bicolor Gemstone Auto-Trader
* **Automated Vendor Purchases:** Visits local Gemstone Traders to buy vouchers, crafting materials, or trade goods when your wallet reaches your configured threshold (e.g. 1500 cap).
* **Smart Wallet Reserve:** Keeps a safety reserve of gemstones for manual purchases.

### 🛡️ Logistics, Security & Humanizer
* **Auto-Repair:** Repairs equipped gear using Dark Matter or visits repair NPCs when item durability drops below your threshold.
* **Consumables Upkeep:** Automatically consumes food (+3% EXP) and medicine buffs on cooldown.
* **Humanizer Breaks:** Periodically teleports to random major city hubs to wander around naturally before returning to the grind.
* **Auto-Decline Party Invites:** Humanized delays before dismissing incoming party invites, with optional auto-reply via `/tell`, `/say`, or `/yell`.
* **GM Alert Watcher:** Detects nearby Game Masters and triggers custom reactions (sound beeps, chat warnings, stopping automation, or client termination).

---

## 📦 Required Plugins (Dependencies)

Install these plugins via Dalamud to enable full functionality:
1. **vnavmesh**: 3D mesh navigation and pathfinding.
2. **BossMod** / **BossModReborn** or **RotationSolver**: Combat execution, dodging, and positioning.
3. **Lifestream**: Changing instances, local aetherytes, and cross-world data-center visits.
4. **TextAdvance**: Skipping dialogue during vendor purchases, repair, and retainer interactions.

---

## ⚙️ Installation

### Method 1: Custom Plugin Repository (Recommended) ⭐

1. Open Dalamud Settings in-game by typing `/xlsettings` in chat.
2. Go to the **Experimental** tab.
3. Under **Custom Plugin Repositories**, add this URL:
   ```
   https://raw.githubusercontent.com/n0way02/FateFrenzy/main/repo/pluginmaster.json
   ```
4. Click **+** (Add), then click **Save and Close**.
5. Open the Plugin Installer with `/xlplugins`.
6. Search for **FateFrenzy** and click **Install**!

---

### Method 2: Manual / Developer Build 🛠️

1. Clone this repository:
   ```bash
   git clone https://github.com/n0way02/FateFrenzy.git
   ```
2. Build in Release mode:
   ```bash
   dotnet build -c Release
   ```
3. In Dalamud Settings (`/xlsettings` → **Experimental**), add `FateFrenzy/bin/Release/FateFrenzy.dll` to your Dev Plugins list.

---

## 💬 Commands

| Command | Description |
| :--- | :--- |
| `/fatefrenzy` or `/ff` | Open the main FateFrenzy window |
| `/ff config` | Open Settings & Configurations |
| `/ff stats` | Open Run History and Statistics |
| `/ff deps` | Open Dependency Manager |
| `/ff pause` | Pause or Resume active farming |
| `/ff target` | Print debug information for currently targeted NPC or FATE |

---

## 💖 Support the Project

If FateFrenzy saves you time and brings you value, consider showing your support! Your contributions help maintain updates, add new features, and host the telemetry web infrastructure:

☕ **Support on Ko-fi:** [https://ko-fi.com/n0way02](https://ko-fi.com/n0way02)

---

## 📝 Credits & Source

This project is built and maintained by **n0way02**.
* Originally derived from **FFXIV-AutoFATEGrind** by [XeldarAlz](https://github.com/XeldarAlz/FFXIV-AutoFATEGrind).
* In compliance with the copyleft terms of the **AGPL-3.0-or-later** license, this repository is open-source and publicly accessible.

---

## 📄 License
This project is licensed under the [AGPL-3.0-or-later License](LICENSE).
