# LethalCCTV

![About Y4NGZ](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shared/about-y4ngz.png)

I am Y4NGZ, an up and coming rapper out of NYC/Detroit. All of my mods are
inspired by the music I write. If you are interested in knowing more about me,
check out my SoundCloud: https://soundcloud.com/y4ngz

## Generative AI usage

The entire code base of this mod was created with generative AI. Assets are
free, licensed, paid for, or generated with AI tools. If you do not wish to play
with AI-generated content, do not install this mod.

## What LethalCCTV does

LethalCCTV fills the facility with security cameras and locked Company stashes.
If an active camera spots you, the alarm goes off. The entrances seal for a while
and monsters head for the tripped alarm. Hack the mainframe in each interior to
shut the active cameras down.

![LethalCCTV ship CCTV station](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/lethalcctv/cctv-station.gif)

![LethalCCTV facility alarm](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/lethalcctv/alarm-active.gif)

![LethalCCTV mainframe hacking](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/lethalcctv/mainframe-hacking.gif)

## Requirements

Your mod manager installs these automatically:

- **BepInExPack**
- **LethalLib**
- **DawnLib**
- **CSync**
- **LethalNetworkAPI**
- **LethalCompany InputUtils**
- **Y4NGZInteractions**
- **Y4NGZCore**

## Cameras

- **Red** - passive. It sees you and does nothing.
- **Orange** - active. Getting spotted trips the facility alarm.

An active camera shows a detection warning on your HUD when you are in its field of view.
The brighter it gets, the closer it is to tripping the alarm.

## The mainframe

A mainframe spawns inside every interior. Beat its hacking mini-game to disable the
active cameras. A hacked mainframe also shows how much scrap is in the interior,
lets you speak over a facility-wide intercom and lists the four-digit stash codes.

## Company stashes

Company stashes open with a four-digit keypad code from the mainframe. The mainframe
does not say which code belongs to which stash, so you may need teamwork to open them all.
Stashes hold gold bars by default. Their spawn lists are configurable.

## The ship CCTV station

The CCTV station is sold in the store. It gives you the camera feeds, a walkie-talkie
and a live radar of the interior. From the station you can ping locations, acquire
targets and hack the mainframe remotely. If OpenBodyCams is installed, every connected
player gets their own named bodycam feed at the start of the feed list.

## Configuration

The configuration file is `BepInEx/config/com.y4ngz.company.lethalcctv.cfg`. The host's settings apply in multiplayer.

- **`Security Systems` / `Security Enabled`** - master switch for cameras, alarms, lockdown gates and drills.
- **`Mainframe` / `Enabled`** - turn the mainframe off. Cameras and alarms still work, and breaking a camera is the only way to stop it.
- **`Company Stashes`** - `Enabled`, `Loot Rolls`, `Loot Pool` and minimum/maximum stash counts per moon risk level.

## Compatibility

Independent installation: LethalCCTV needs only Y4NGZCore and the libraries listed under
Requirements. The other Y4NGZ packages are optional companions - install any combination
and each one lights up its extra behavior.

The required Core package is `Y4NGZ313-Y4NGZCore-1.0.10` or newer.

| Mod | What it adds |
| --- | --- |
| **Contracted** | Contract camera pings, mainframe and Company Stash integration, and facility security reactions; Contracted also feeds the stash loot table. |
| **Y4NGZ Upgrades** | The Chameleon upgrade, the Field Operations tablet, camera interactions and markers, and a CCTV time statistic. |
| **Better Armory** | Its weapons can be made Company Stash exclusive. |
| **OpenBodyCams** | Every connected player gets their own named bodycam feed at the start of the CCTV station's feed list. |
| **GeneralImprovements** | Compatible with Better Monitors. |

## Asset credits

- https://sketchfab.com/3d-models/cctv-ea4b5571f08049adbe9069ebf71aeff8
- https://assetstore.unity.com/packages/3d/props/electronics/keypad-free-262151
- https://assetstore.unity.com/packages/3d/props/industrial/hq-shipping-container-modular-203201

## GitHub

Source code: https://github.com/y4ngz313/LethalCCTV

## Bugs

Report bugs at https://github.com/y4ngz313/LethalCCTV/issues.
