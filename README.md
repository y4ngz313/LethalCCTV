# LethalCCTV

Plugin source for **LethalCCTV**, a facility surveillance overhaul for Lethal Company:
procedurally placed CCTV cameras that trip alarms when they spot a worker, a purchasable
ship CCTV station for watching the feeds, and a hackable mainframe in every interior with
an intercom, scrap census, and four-digit stash codes for Company Stashes.

Download and play it from Thunderstore:
[Y4NGZ313/LethalCCTV](https://thunderstore.io/c/lethal-company/p/Y4NGZ313/LethalCCTV/).

## What is in this repository

The C# plugin source only. The textures, audio, and compiled Unity asset bundles the plugin
loads at runtime are not published here — they ship inside the Thunderstore package. The
project also references `Y4NGZCore`, a shared support library that is not public. This tree
shows how the mod works; it does not build into a playable mod on its own.

## Runtime dependencies

BepInEx 5.4.2305 and Y4NGZCore (bundled with the Thunderstore package).

## Bugs and feedback

Open an issue with the moon, interior, whether the problem occurred for the host or a
client, and the relevant `BepInEx/LogOutput.log` excerpt.

## Licensing

No open-source license is granted; this source is published for reference.
