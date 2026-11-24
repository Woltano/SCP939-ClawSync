# Third-party components and rights

SCP-939 Claw Sync is an independent server plugin by Woltano. The plugin source is made available for inspection; no separate open-source license has been selected for the author's code. The existing permission to install and use the distributed plugin binary on SCP:SL servers is unchanged. This notice does not restrict rights granted by third-party licenses.

## External dependencies

The source repository does not redistribute the game assemblies, third-party DLLs or original audio file. Consult the original projects for their license texts and notices.

- [LabAPI](https://github.com/northwood-studios/LabAPI): LGPL-3.0; supplied with the dedicated server.
- [AudioManagerAPI 2.4.3](https://github.com/iomatix/-SCPSL-AudioManagerAPI/releases/tag/2.4.3): upstream project identifies its license as MIT. Loaded separately at runtime.
- [NAudio](https://github.com/naudio/NAudio): the version distributed alongside AudioManagerAPI 2.4.3.
- [NLayer 2.0.1](https://www.nuget.org/packages/NLayer/2.0.1): runtime MP3 decoding dependency.
- SCP:SL Dedicated Server, including its game and Unity assemblies, is obtained from its official distribution. Its own licenses apply.

## Sound asset

The separate installation archive contains the unmodified `939_claw_beta.mp3` obtained from the official SCP:SL wiki. The supplied game license states CC BY-SA 3.0 for its generally covered content and does not list this file among its exceptions. An asset-specific license was not independently confirmed; separate terms, if any, prevail. Audio is not authored by Woltano. The installation archive includes the original game license and audio attribution; preserve both when redistributing the sound.

- [Official wiki file page](https://betawiki.scpslgame.com/index.php?title=File:939_claw_beta.mp3)
- [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/)
- [SCP:SL EULA](https://store.steampowered.com/eula/700330_eula_0)

No audio or third-party binaries are included in this source archive.
