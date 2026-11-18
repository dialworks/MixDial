<div align="center">

<img src="docs/icon.png" width="96" alt="MixDial icon">

# MixDial

**Use the game/chat mix dial on a SteelSeries Arctis 7+ or Arctis Nova Elite — without SteelSeries GG.**

[![Latest release](https://img.shields.io/github/v/release/dialworks/MixDial?label=download&color=46c86e)](https://github.com/dialworks/MixDial/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/dialworks/MixDial/total?color=50a0ff)](https://github.com/dialworks/MixDial/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078d4)](#requirements)
[![License: MIT](https://img.shields.io/github/license/dialworks/MixDial?color=lightgrey)](LICENSE)

<img src="docs/popup.png" width="300" alt="MixDial popup showing Game 70% and Chat 45%">

<sub>Unofficial community project — not affiliated with or endorsed by SteelSeries. See <a href="#legal">Legal</a>.</sub>

</div>

---

MixDial is a tiny tray app that reads the mix dial straight from the headset's USB dongle and turns it into
per-app volume. **Chat apps** follow the chat side of the dial, **media apps** stay at full volume, and
**everything else** playing on the headset follows the game side. An app on both lists follows chat.
This applies to every supported headset.

No drivers, no installer, no background services, no telemetry — one small exe, built from a single C# file
you can read in a few minutes.

## ✨ Features

|   |   |
|---|---|
| 🎚️ **Hardware dial works again** | Turn the dial, and game and chat apps balance against each other |
| 📊 **Live tray icon** | Two bars show the current mix at a glance (grey when the headset is off) |
| 🪟 **On-screen popup** | A small overlay while you turn the dial (optional, never steals focus) |
| 🔋 **Battery & charging** | Battery level in the tray menu, plus a heads-up when it runs low |
| 🔔 **Connect notifications** | Know when the headset connects, turns off or goes out of range |
| 💬 **Pick chat and media apps** | Tick apps from the tray menu, or edit a plain text list. Media stays full on every headset |
| 🚀 **Start with Windows** | One click, runs quietly in the tray |
| ↩️ **Leaves no mess** | App volumes go back to 100% when you pause or quit |

## 🚀 Quick start

1. Download **`MixDial.exe`** from the [latest release](https://github.com/dialworks/MixDial/releases/latest).
2. Close SteelSeries GG if it's running.
3. Run `MixDial.exe` — the icon appears in the tray.
4. Right-click it → **Start with Windows**.

That's it. Turn the dial.

> [!NOTE]
> The exe isn't code-signed, so SmartScreen may say *"Windows protected your PC"* the first time —
> click **More info → Run anyway**. If **Smart App Control** blocks it completely, use
> [Run from source](#run-from-source) instead.

## 🎧 Compatible headsets

| Headset                       | USB ID      |
|-------------------------------|-------------|
| SteelSeries Arctis 7+         | `1038:220E` |
| SteelSeries Arctis 7P+        | `1038:2212` |
| SteelSeries Arctis 7X+        | `1038:2216` |
| SteelSeries Arctis 7+ Destiny    | `1038:2236` |
| SteelSeries Arctis Nova Elite    | `1038:2244` |
| SteelSeries Arctis Nova Elite SNG| `1038:2270` |

On the Nova Elite the wheel is a volume knob until ChatMix is switched on. MixDial turns ChatMix on
while it is running and turns it off again when you quit. Press the wheel once if it is still changing
the system volume — that toggles the wheel between volume and the mix. The tray shows mic mute and an
**ANC**, **Sidetone**, **Screen**, and **Onboard EQ** appear only while a Nova Elite is connected.
An Arctis 7+ does not get those menus. **Edit wireless curve…** writes the Nova Elite Custom slot only.

Have a different model? Other headsets use a different protocol, so they aren't supported yet —
[open an issue](https://github.com/dialworks/MixDial/issues) or send a pull request.

## 🖱️ Usage

Right-click the tray icon:

| Menu item | What it does |
|---|---|
| **Chat apps** | Apps playing on the headset. Ticked ones follow the chat side. |
| **Media apps** | Ticked apps stay at full volume on every supported headset. Chat wins if an app is on both lists. |
| **Mix enabled** | Pause / resume. Pausing puts app volumes back to 100%. |
| **Show popup when turning dial** | Toggle the on-screen overlay. |
| **Notifications** | Toggle connect / disconnect / battery notifications. |
| **Start with Windows** | Launch MixDial when you log in. |

Common chat apps and common browsers and players are on the lists out of the box. The lists live in
`%APPDATA%\MixDial\chat-apps.txt` and `media-apps.txt` — one process name per line, `#` for comments —
and changes apply as soon as you save.

## Requirements

- Windows 10 or 11
- .NET Framework 4.x (already part of Windows)
- SteelSeries GG **not** running at the same time — both would fight over the app volumes

## 🛠️ Build it yourself

No Visual Studio or SDK needed — Windows already ships a C# compiler:

```bat
build.bat
```

### Run from source

`Start-MixDial.ps1` compiles the code in memory and runs it inside PowerShell, so there's no unsigned exe for
Smart App Control to block:

```bat
conhost.exe --headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File Start-MixDial.ps1
```

**Start with Windows** works in this mode too.

## ❓ FAQ

<details>
<summary><b>Why per-app volume instead of separate Game and Chat devices like GG?</b></summary>
<br>
Without GG's virtual audio driver, Windows sees the headset as a single output device. MixDial sets each
app's volume on that device instead — exactly what the Windows Volume Mixer does — so no driver is needed.
</details>

<details>
<summary><b>An app isn't reacting to the dial.</b></summary>
<br>
Make sure it's playing through the headset (not your speakers) and has made a sound since it started.
Then check the lists: ticked chat apps follow chat, ticked media apps stay full, and everything else follows game.
</details>

<details>
<summary><b>Does it change anything on the headset?</b></summary>
<br>
On an Arctis 7+ it only sends a status query. The Nova Elite dial does not report a mix until ChatMix
is on, so MixDial sends that enable command when it connects and the matching disable command when you
quit. It does not change the equalizer, sidetone, microphone, or firmware, and it does not store
anything on the headset.
</details>

<details>
<summary><b>How do I uninstall it?</b></summary>
<br>
Untick <b>Start with Windows</b>, quit from the tray, and delete <code>MixDial.exe</code>. To remove settings
too, delete <code>%APPDATA%\MixDial</code> and the registry key <code>HKCU\Software\MixDial</code>.
</details>

<details>
<summary><b>How does it talk to the dongle?</b></summary>
<br>

It listens on the receiver's vendor-specific HID interfaces (usage page `0xFF00` for events, `0xFFC0` for requests).

**Arctis 7+** uses report id `0x00`:

| Report | Meaning |
|---|---|
| `45 <game> <chat>` | Dial moved (0–100 each) |
| `BB <03 \| 00>` | Headset connected / off |
| `B7 <0–4>` | Battery level changed |
| `B0` → `B0 <online> <battery> <charging> <game> <chat>` | Status query |

**Arctis Nova Elite** uses report id `0x01` for commands and `0x07` for events. Its battery is a percentage, and the base reports a spare battery. The 7+ menu and reports are unchanged. |

</details>

## Legal

MixDial is an independent project, made so that headsets people already own work with their own PC. It is
**not affiliated with, endorsed by, sponsored by, or supported by SteelSeries ApS**. Please don't contact
SteelSeries support about it — [open an issue](https://github.com/dialworks/MixDial/issues) here instead.

SteelSeries, Arctis, SteelSeries GG and ChatMix are trademarks of SteelSeries ApS. Discord, TeamSpeak,
Microsoft Teams and other product names mentioned are trademarks of their respective owners. They are used
here only to describe compatibility.

MixDial contains no SteelSeries code, software, firmware, logos or other assets. The icon is original.

The software is provided "as is", without warranty of any kind. Use it at your own risk.

## License

[MIT](LICENSE) © 2026 dialworks
