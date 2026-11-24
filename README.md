# SCP-939 Claw Sync

A server-side LabAPI plugin by **Woltano** that makes SCP-939's missed claw attacks visible and audible to other players in SCP: Secret Laboratory.

**[Download v1.0.0 RC1 and watch the demonstration](https://github.com/Woltano/SCP939-ClawSync/releases/tag/v1.0.0-rc1)**

## Features

- Repeated missed claw swings are visible to other players.
- Nearby spatial claw swing sound (up to 16 m), without adding a second sound for the attacker.
- Enabled automatically on server startup, across rounds, with independent tracking of SCP-939 players.
- No changes to damage, hitboxes, or attack cooldowns. No client-side mods required.
- FAST timing by default, with optional slower presets.

## Installation

Download `Woltano_SCP939_ClawSync_v1.0.0-rc1_INSTALL_EN.zip` from the release linked above, extract it, and read `START_HERE.txt`. The archive contains the compiled plugin, audio, installer scripts and documentation, with pinned dependencies obtained from their original providers.

Stop the server and back up LabAPI before installing. Windows users can run `INSTALL_WINDOWS.cmd`; Linux and managed-hosting instructions are included in the installation archive. No compilation is necessary to use the release.

## Source and building

The [`src/`](src/) directory contains the C# source and project from the RC1 author build kit, unchanged. For build requirements, commands and the published DLL's checksum, see [BUILD.md](BUILD.md).

The implementation observes server-side attack state and sends cosmetic messages to observers after a missed attack. It uses version-specific reflection and an undocumented animation result value. Normal successful attacks continue to use the game's existing effects. Updates to SCP:SL may require compatibility changes.

## Remote Admin

```text
clawsync status
clawsync on
clawsync off
clawsync mode spatial
clawsync mode quiet
clawsync timing fast
clawsync timing balanced
clawsync timing legacy
clawsync debug on
clawsync debug off
```

Requires the `PlayersManagement` permission. Settings revert to their defaults after a server restart. Without the sound asset or working audio dependencies, animation remains available.

## Compatibility and feedback

The RC1 plugin was tested on SCP:SL Dedicated Server 14.2.9 (Windows). The installation scripts, other ports, Linux runtime and different public-server environments still need independent live testing. This is a release candidate, not a compatibility guarantee for later game versions.

If you encounter a problem, please use [Issues](https://github.com/Woltano/SCP939-ClawSync/issues). Include the game/LabAPI version, plugin version and relevant redacted logs. Do not share credentials, player personal data, private server configuration or full logs containing sensitive information.

## Credits and licensing

Developed by **Woltano**. This is an independent community plugin, not affiliated with or endorsed by Northwood Studios.

This source is published for inspection. No separate open-source license has been selected for the plugin code at this time. The existing distribution of the binary for use on SCP:SL servers is unchanged. Third-party components and the separately licensed audio retain their own terms; see [THIRD_PARTY.md](THIRD_PARTY.md).
