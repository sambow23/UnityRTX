# UnityRTX
A BepInEx plugin that brings NVIDIA RTX Remix path tracing to most modern Unity games.

| Supported Features |   |
|--------------------|---|
| Skinned meshes     |✅|
| Remix Replacements |✅|
| Basic textures     |✅|
| Point and spot lights (that are not baked) |✅|
| Directional lights |❌|
| Particle systems |❌|
| GPU-Instanced / Statically Batched Geometry |❌|

## Requirements

- BepInEx 5.x
- A Unity 2019+ game that uses MonoBleedingEdge (IL2CPP support will come later)

## Installation
1. Download the latest [release](https://github.com/sambow23/UnityRTX/releases/latest)
2. Extract to the root of your Unity game

OR

1. Install BepInEx in your Unity game
2. Place all dlls inside the `.trex` folder of the remix archive in the game's root folder (next to the .exe)
3. Copy `UnityRemix.dll` to `BepInEx/plugins/`
4. Launch the game

## Configuration

Edit `BepInEx/config/com.Unity.remix.cfg`:

## Building

Requirements: Visual Studio 2019+, .NET Framework 4.7.2+, BepInEx installed to a game, a Unity 2019+ Mono game

```bash
# Setup references (edit paths in setup-references.ps1)
.\setup-references.ps1 -UnityPath "F:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"

# Build
.\build.ps1 -Deploy -UnityPath "F:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"
```
