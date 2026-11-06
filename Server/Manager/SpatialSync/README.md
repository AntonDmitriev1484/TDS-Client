# Spatial sync (scene-anchor SLAM)

Spatial sync puts every headset in one shared coordinate system, the **global frame**, so that virtual content (cubes, props, avatars) appears at the same real-world spot for everyone in the room. This folder is the server half. The client half is `SpatialSyncManager` and `SceneAnchorManager` in the Unity project.

There is no image or point-cloud mapping any more; that was archived in `Server/Archived`. Everything here works from the room scan each Quest already has.

## The idea

Each Quest 3 scans its room once in the system **Space Setup** and keeps the result. Meta's Mixed Reality Utility Kit (MRUK) exposes it as *scene anchors*: walls, invisible walls, floor, ceiling, door and window frames, wall art, screens and furniture, each with a label, a pose and a size.

Each headset has its own **tracking space**, and the origins of two headsets have nothing to do with each other. The scene anchors have no shared IDs between headsets. So the server has to recognise that "this wall on headset B is that wall on headset A" from geometry alone, and work out how the two tracking spaces are related.

Tracking spaces are gravity aligned, so only a **rotation about the vertical axis (yaw)** and a **3D shift** are unknown. That is what the server solves for.

## Data flow

1. The client (`SceneAnchorManager.BuildKeyFrame`) sends a `KeyFrameAnchorDTO`: its head pose and all its scene anchors, in its own tracking space, read in the same frame. It sends 5 per second until placed, then 1 per second to keep the alignment fresh.
2. `ServerSpatialSyncManager` hands the message to `GlobalMappingManager.HandleNewKeyFrame`.
3. **The first player with at least 3 anchors defines the global frame**: their tracking space is the global frame (the identity transform).
4. Every other player is lined up to the map by `SceneAnchorAligner` (below). Anchors a new player sees that the map lacks are merged in, in the global frame.
5. Once **every expected player is placed** (the "global map formed"), the server answers each key frame with a `SyncResponseDTO` that carries the global pose of the head pose that was sent. Before that, the response has no global pose and the client keeps sending quickly.

If there is only one player, the map forms as soon as that player has enough anchors.

## How the aligner matches (`SceneAnchorAligner.cs`)

1. **Candidates.** The most telling anchors are paired first: doors, windows, screens and wall art, then furniture. Each pair with the same label and a similar size proposes the yaw and shift that would put one onto the other. Only if those fail are pairs of walls used.
2. **Cull.** A candidate is dropped unless the walls (real and invisible together) overlap the map's walls by at least 20%.
3. **Evidence.** Every overlapping pair of anchors earns points: its overlap ratio, times a label weight. The same label counts in full (1.0). A real wall against an invisible wall counts 0.6, different furniture labels 0.3, a door or window lying in a wall 0.2. An alignment's evidence is the sum.
4. **Refine.** The eight best candidates are fitted by least squares and then nudged (yaw and shift, steps shrinking from 3° / 15 cm to 0.1° / 5 mm) until the evidence stops rising.
5. **Accept** only if the evidence is high enough (at least 2 points and 0.3 per anchor of the smaller set), at least 3 anchors match well, the matches agree to within 25 cm RMS, and they fix the headset in all three directions (conditioning check).
6. **Refuse when ambiguous.** If the second best alignment is almost as good, nothing is chosen. A bare rectangular room looks the same turned 180°, so it is refused until a door, window, screen or furniture breaks the symmetry. This is deliberate: a wrong placement is worse than none.

A placed player is refitted every 2 seconds from their latest anchors, which follows tracking drift and recentres. Players not yet placed are retried every second. All tolerances live in `SceneAnchorMapParameters`.

## What matters when you build a game

**Author content in the global frame.** In Unity, put all shared content under one GameObject called `SharedScene` (`SpatialSyncManager` finds it by that name). When the server places this headset, the client moves `SharedScene` so that the global frame lines up with the headset's tracking space. Anything under it then appears in the same place for everybody. Content outside it is not synchronised.

**What the client does with a response.** It takes the head pose it sent (L) and the global pose the server returned (G), and sets `SharedScene`'s rotation to `L.rot · G.rot⁻¹` and its position to `L.pos − rotation · G.pos`. The first alignment is applied at once. Every later one is blended over `AlignmentBlendSeconds` (default 1 s) so corrections do not make the scene jump. Because the response carries back the pose that was sent, network latency does not skew the result.

**Send positions between players in global coordinates.** Anything you put in a message to the server or other players (cube poses, player poses) should be expressed in `SharedScene` space, not in the headset's own world space.

**Wait for alignment.** Before the first global pose arrives, `SharedScene` is not placed. Do not spawn or show shared content until `SpatialSyncManager` has applied a global pose. The server only starts answering once all expected players are placed, so everyone becomes visible together.

**Requirements for it to work:**
- Every player must have done **Space Setup** on the headset, in the same physical room, with the walls, doors and furniture scanned. Over Quest Link a scan cannot be made; only a room already saved on the headset can be read, and Link needs the "Spatial Data over Meta Quest Link" setting on.
- The app needs the scene permission (`USE_SCENE`; in the Unity project, OVRManager's Scene Support under Quest Features).
- The rooms need some unique features. A plain empty rectangle will be refused (see above). A door, a window, a screen or an uneven piece of furniture is enough.
- Players do not need to see the same part of the room. They need enough in common (at least 3 well-matched anchors, including some walls or the floor for height).

**Things that go wrong, and what the server logs say.** All server lines start with `[SceneAnchors]`:
- `player N placed in the global map by X matching anchor(s), Y points of evidence, residual Z cm`. A good placement has a residual of a few centimetres.
- `two different alignments have almost the same evidence`: the room looks symmetric from these anchors. Add a distinctive anchor.
- `no pair of anchors with the same label and a similar size`: probably not the same room, or the scans share nothing.
- `No room loaded yet` (client log): the headset has no saved room, or MRUK has not loaded it yet.

## Debugging

With `DebugMode` on in the server settings, the server opens a 3D viewer window per submap and one **Global map** window. Walls are drawn as rectangles with a normal tick, floors flat, furniture as boxes, players as dots. Solid means matched, dashed means unmatched. Mouse to orbit, pan and zoom; `L` toggles labels; `F` toggles floor and ceiling. A player that has not been placed has its own window in its own tracking space.

On the headset, `SceneAnchorManager.ShowAnchors` draws the scene anchors as outlines with labels, so you can see what is being sent.

## Files

| File | What it does |
|---|---|
| `GlobalMappingManager.cs` | Holds the global map and each player's alignment. Entry point for key frames; decides when the map is formed. |
| `SceneAnchorAligner.cs` (in `Utilities`) | Candidate search, evidence scoring and refinement. Pure geometry, no networking. |
| `ServerSpatialSyncManager.cs` | One spatial sync session: owns the mapping manager, the connections and the debug viewer. |
| `SpatialSyncConnectionManager.cs` | The session's WebSocket connections by player. |
| `SpatialSyncCoordinator.cs` | Creates and finds sessions. The server normally runs one. |
| `Handler/SpatialSyncConnectionHandler.cs` | Reads incoming messages and replies with `SyncResponseDTO`. |
| `Utilities/SceneMapView.cs`, `SceneMapViewer.cs`, `SceneMapSnapshot.cs` | The 3D debug viewer (Windows only). |

The messages are in `SharedLibrary/Communication/DataTransferObjects/SpatialSync`: `KeyFrameAnchorDTO` (client to server), `SyncResponseDTO` (server to client) and `BeginSpatialSyncDTO` (server tells the client to start sending).
