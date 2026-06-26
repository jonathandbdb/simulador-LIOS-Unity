# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity **6000.5.1f1** (Unity 6) project using the **Universal Render Pipeline (URP)** and the new **Input System** package. It is currently a near-default URP template — the only custom code is the `TutorialInfo` Readme tooling. Treat this as a greenfield project: most game code, scenes, and assets are yet to be added under `Assets/`.

## Working through Unity (MCP)

This repo is configured with the **unity-mcp** server (`.mcp.json`, plugin `com.anklebreaker.unity-mcp` in `Packages/manifest.json`). Prefer the `unity_*` MCP tools for all Editor operations — creating/editing scripts, GameObjects, components, prefabs, materials, scenes, running play mode, and builds. Do not call the Unity HTTP bridge (`http://127.0.0.1:7890`) directly; it bypasses the agent queue and safety mechanisms.

- On the first Unity tool call instances are auto-discovered. If multiple Editor instances are found, ask the user which one to use and call `unity_select_instance` before proceeding.
- After editing C# from the filesystem (not via MCP), use `unity_get_compilation_errors` to confirm the code compiles in the Editor — there is no standalone CLI compile step in this workflow.
- Builds run through `unity_build`; play-mode testing through `unity_play_mode`. Tests use the Unity Test Framework (`com.unity.test-framework`).

## Conventions

- **Input:** Uses the Input System package with the generated actions asset at `Assets/InputSystem_Actions.inputactions`. Do not write code against the legacy `UnityEngine.Input` API; add/extend action maps in that asset instead.
- **Rendering:** URP with separate **PC** and **Mobile** quality tiers — paired Renderer + RP Asset files in `Assets/Settings/` (`PC_Renderer`/`PC_RPAsset`, `Mobile_Renderer`/`Mobile_RPAsset`). When changing render features, update the relevant tier rather than assuming a single pipeline asset.
- **Scenes:** `Assets/Scenes/SampleScene.unity` is the only scene; the post-processing volume profile is `Assets/Settings/SampleSceneProfile.asset`.

## Notes on generated files

`Assembly-CSharp*.csproj`, `*.slnx`, `Library/`, `Temp/`, and `Logs/` are Unity-generated — do not hand-edit them. Every asset under `Assets/` has a paired `.meta` file: when creating, moving, or deleting assets outside the Editor, keep the `.meta` alongside its asset (or let the MCP/Editor manage it) to preserve GUIDs and references.
