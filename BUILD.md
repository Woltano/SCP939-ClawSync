# Build from source

This directory contains the unmodified C# source and project from the author kit used for `v1.0.0-rc1`. The target is .NET Framework 4.8 (`net48`), not the .NET runtime used by the SDK.

## Prerequisites

- .NET SDK 8 or newer and the .NET Framework 4.8 developer/targeting pack for a Windows build.
- A local, legitimate SCP: Secret Laboratory Dedicated Server installation matching the tested baseline (14.2.9), including LabAPI.
- The server's `SCPSL_Data/Managed` directory. The project references the following local assemblies, none of which are redistributed by this repository:

- `$(SL_MANAGED)\LabAPI.dll`
- `$(SL_MANAGED)\Assembly-CSharp.dll`
- `$(SL_MANAGED)\Assembly-CSharp-firstpass.dll`
- `$(SL_MANAGED)\CommandSystem.Core.dll`
- `$(SL_MANAGED)\UnityEngine.CoreModule.dll`
- `$(SL_MANAGED)\UnityEngine.AnimationModule.dll`
- `$(SL_MANAGED)\UnityEngine.PhysicsModule.dll`
- `$(SL_MANAGED)\Pooling.dll`
- `$(SL_MANAGED)\Mirror.dll`

AudioManagerAPI is loaded at runtime via reflection, so its DLL and audio dependencies are not needed to compile this project. They are needed for spatial audio when running the plugin.

## Windows build (PowerShell)

Open PowerShell in the root of the downloaded repository. Set `$managed` to the **actual** Managed directory in your Dedicated Server installation:

```powershell
$managed = 'C:\path\to\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
if (-not (Test-Path -LiteralPath (Join-Path $managed 'LabAPI.dll'))) {
    throw 'Choose the correct Dedicated Server Managed directory.'
}
dotnet build .\src\Woltano_SCP939_ClawSwing.csproj -c Release "-p:SL_MANAGED=$managed"
```

Output: `src/bin/Release/net48/Woltano_SCP939_ClawSwing.dll`.

Building does not install the plugin or change the server. To use a self-built DLL, follow the installation documentation from the corresponding release, replace only its plugin DLL, and verify compatibility on a test server. Do not load multiple copies of Claw Sync.

The documented build uses the same project and local-reference approach as the tested Windows build; a fresh build of this repository has not been independently executed here. Linux build/runtime has not been independently verified.

## Release provenance

- Release: [v1.0.0-rc1](https://github.com/Woltano/SCP939-ClawSync/releases/tag/v1.0.0-rc1)
- Assembly name: `Woltano_SCP939_ClawSwing`
- Assembly version: `1.0.0.0`
- SHA-256 of the DLL in the published installation archive: `3ed2b963800053fbc29e5b1ea55cd04423754ddffbbea2df02659a1ca41bb636`

The C# and project files are byte-for-byte identical to the retained RC1 author kit. The hash above identifies the original released binary, not a guarantee of a bit-identical independent rebuild; compiler, build paths and server reference versions can affect the output. See `SOURCE_SHA256SUMS.txt` for the original source file hashes.
