# TDS Unity Client

The Meta Quest 3 client for a **co-located, two-player mixed-reality game**, built as a testbed for a user study on how network latency and spatial drift affect collaborative play. Both players stand in the same physical room. A server aligns their headsets into one shared coordinate system, and they play a cube-on-board game against each other.

This is the Unity half of the project. The other halves sit next to it in the repository:

| Folder | Role |
|---|---|
| `UnityClient/` (this folder) | The Quest app: scene, hand-grabbable cubes, networking, spatial sync client |
| `../Server/` | ASP.NET Core server: matchmaking, authoritative game state, spatial alignment, study conditions |
| `../SharedLibrary/` | C# game state, actions and DTOs, shared by the server and this client |

## Requirements

- **Unity 6000.0.66f2** (see `ProjectSettings/ProjectVersion.txt`)
- A **Meta Quest 3** (the scene-anchor room scan is a Quest 3 feature)
- Android Build Support modules for Unity, to build the APK
- The server running and reachable from the headset (see `../Server`)

Key packages (`Packages/manifest.json`): Meta XR Core SDK, Meta XR Interaction SDK (OVR), and Meta Mixed Reality Utility Kit (MRUK), all 207.0.1; Unity OpenXR 1.16.1; Newtonsoft.Json.

## Setup

1. Clone the repository and open the `UnityClient` folder in Unity Hub.
2. Open `Assets/Scenes/HandsGrabCubeAR.unity`.
3. In **OVRManager**, enable **Scene Support** (Quest Features) so the app gets the `USE_SCENE` permission.
4. Edit the two config files described under [Configuration](#configuration).
5. Every player must have run **Space Setup** on their headset, in the same physical room, with walls, doors and furniture scanned. See [Requirements for alignment](#requirements-for-alignment).

## Configuration

There are two small JSON files.

### `Assets/StreamingAssets/serverconfig.json` (where the server is)

Read at startup by `ServerConfig`. Every field is optional; missing fields fall back to defaults.

| Field | Meaning | Default |
|---|---|---|
| `host` | Server host name or IP | `localhost` |
| `port` | Server port | `5000` |
| `reconnectDelay` | Seconds between reconnect attempts | `1` |
| `username`, `password` | Credentials used to log in to the server | built-in defaults |

The client connects to `https://<host>:<port>`. The host and port must match `WebServer:Url` in the server's `appsettings.json`.

> This file ships in the build and may hold credentials. Don't commit real ones.

### `runconfig.json` (project root; how the client runs)

```json
{ "run_mode": "remote" }
```

| `run_mode` | Behaviour |
|---|---|
| `remote` | Log in to and play on the real game server (default). |
| `local_test` | Skip the server. `LocalTestBootstrapper` starts an in-app `OfflineGameStateManager`, which the client talks to over loopback TCP. The menu is hidden and the game starts straight away. |

The Editor reads `runconfig.json` from the project root. For device builds, `Editor/RunConfigBuildStep.cs` copies it into `StreamingAssets` for the duration of the build and deletes the copy afterwards, so the project-root file is the only one to edit. Change the mode **before** building the APK.

## Building and running

1. Set `run_mode` and `serverconfig.json` for the target setup.
2. **File > Build Settings**, switch to Android, and build or *Build and Run* to a connected Quest.
3. Start the server (`../Server`), then launch the app on **each** headset.
4. On the menu, press **Play** (enabled once the game connection is open). Players are queued, and the match starts once the server has aligned everyone.

To iterate without headsets or a server, set `run_mode` to `local_test` and press Play in the Editor.

Over Quest Link, a room scan can't be made; only a room already saved on the headset can be read, and Link needs the **Spatial Data over Meta Quest Link** setting on.

## How the client works

```
MainMenuManager ──► ConnectionManager ──► Server (HTTPS + WebSockets)
                          │
          ┌───────────────┼────────────────┐
          ▼               ▼                ▼
   GameStateManager  SpatialSyncManager  SceneAnchorManager
   (mirrored state,  (applies the         (reads MRUK scene
    prediction)       global pose)         anchors, sends key frames)
          │
          ▼
 GrabbableCubeManager / GameBoardView / GrabbableCube
```

**Spatial sync.** `SceneAnchorManager` reads the room's scene anchors through MRUK and sends them, with the head pose, to the server as key frames (5 per second until placed, then 1 per second). The server works out how the headsets' tracking spaces relate and replies with the head pose in the global frame. `SpatialSyncManager` then moves the `SharedScene` GameObject so the global frame lines up with this headset, blending later corrections over about a second. The server half is documented in `../Server/Manager/SpatialSync/SpatialSyncREADME.md`.

**Game.** The server holds the authoritative `GameState`. The client keeps a mirror, applies game-state snapshots, and predicts its own grab, release and move actions locally before the server confirms them. Players grab a cube from the board and carry it into their goal; a cube must stay in the goal for 0.5 s to score. The game runs for 12 rounds.

**Study conditions.** The server steps through four conditions, moving on each time a player wins a game: no latency / no drift, high latency / no drift, no latency / high drift, and high latency / high drift. In the drift conditions the server sends each client a rotation and shift to apply to its `SharedScene`, which only changes what that player sees.

## Building shared content

These rules come from how spatial sync works:

- **Put all shared content under one GameObject named `SharedScene`.** `SpatialSyncManager` finds it by name. Anything outside it is not synchronised.
- **Send positions in `SharedScene` space.** Messages to the server use `localPosition` and `localRotation` of objects parented to `SharedScene`. Apply incoming state to the local transform, not the world transform.
- **Wait for alignment.** Don't spawn or show shared content until `SpatialSyncManager` has applied a global pose. The server only answers once every expected player is placed, so everyone becomes visible together.

## Requirements for alignment

- Every player has done Space Setup in the same room, with walls, doors and furniture scanned.
- The room has something distinctive: a door, window, screen or uneven furniture. A plain empty rectangle looks identical when rotated 180° and is refused on purpose.
- Players need at least 3 well-matched anchors in common, including some walls or the floor.

If placement fails, check the server log for lines starting with `[SceneAnchors]`. On the headset, `SceneAnchorManager.ShowAnchors` draws the scene anchors with labels so you can see what is being sent. `No room loaded yet` in the client log means the headset has no saved room, or MRUK hasn't loaded it.

## Project layout

```
Assets/
  Scenes/HandsGrabCubeAR.unity     The game scene
  Scripts/
    Client/
      Communication/
        Config/ServerConfig.cs     Reads serverconfig.json and runconfig.json
        Manager/ConnectionManager.cs   Login, WebSockets, message routing
        Manager/MainThreadDispatcher.cs
      Scene/
        Manager/                   GameStateManager, SpatialSyncManager, SceneAnchorManager,
                                   GrabbableCubeManager, MainMenuManager
        View/                      GameBoardView, GrabbableCube, ButtonTextColor
      Utilities/PassthroughBootstrapper.cs
    OfflineServer/                 In-app server for local_test mode
    Editor/RunConfigBuildStep.cs   Copies runconfig.json into builds
    Utilities/Conversions.cs
  StreamingAssets/serverconfig.json
  Oculus/, XR/, StarterSamples/    Meta SDK and sample assets
runconfig.json                     Run mode (local_test or remote)
```

## Acknowledgements and license

This project started from Meta's [Unity-StarterSamples](https://github.com/oculus-samples/Unity-StarterSamples), and the `StarterSamples`, `Oculus` and related folders come from it. Those files are covered by the MIT license in [LICENSE](./LICENSE), except for [Audio360](./Assets/StarterSamples/Core/Video/Plugins/Android/Audio360), which has its own terms. See also [CONTRIBUTING.md](./CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md).
