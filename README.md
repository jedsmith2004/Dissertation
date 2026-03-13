# Smart Motion Editing — AI-Generated Animation in Unity

A dissertation project that brings **text-to-motion generation** into the Unity Editor. Enter a prompt such as *"a person walks forward"*, generate one or more motion variants from the backend, convert them into Humanoid `.anim` clips, preview them on a selected character, and optionally refine root translation with a path-edit bake workflow.

The project currently has two active parts:

| Component | Location | Language |
|-----------|----------|----------|
| **Motion Backend** — gRPC inference server for BVH generation | `motion-backend/` | Python |
| **Unity Plugin** — generation, library, preview, and path editing tools | `Dissertation Plugin Playground/` | C# |

They communicate over **gRPC** on `localhost:50051`.

---

## Current Scope

The implemented pipeline today is:

1. **Prompt → BVH batch generation** using a selectable backend model.
2. **Text-guided clip editing (MoMask)** over one or more selected time windows from a source clip.
3. **BVH → Humanoid AnimationClip** conversion inside Unity via `BvhImporter` and `HumanPoseHandler`.
4. **Library + preview workflow** in the `MotionGen` editor window.
5. **Root path editing** by placing path keys in the Scene view and baking corrected root translation back into the clip.
6. **Optional post-processing** from the `Post` tab, producing a preserved reference clip plus a separate processed clip.

The current system is **BVH-only** and supports both `T2M_GPT` and `MOMASK`.

---

## End-to-End Pipeline

This is the implemented click-to-animation flow in the current codebase:

1. In **Window → MotionGen**, the user enters a prompt, FPS, duration, version count, and seed settings, then clicks **Generate Versions**.
2. `MotionGenWindow` calls `MotionClient.GenerateBatchAsync(...)`.
3. `MotionClient` sends a `BatchGenerateRequest` with:
   - `prompt`
   - `fps`
   - `duration_seconds`
   - `count`
   - `use_random_seed`
   - `seed`
   - `format = BVH`
  - `model = selected in the MotionGen UI (`T2M_GPT` or `MOMASK`)`
4. `motion-backend/server/app.py` validates that the request is `BVH`, resolves seeds for each requested version, selects the requested model generator, and calls its `generate_bvh(...)` implementation.
5. The backend returns a `BatchGenerateReply` containing one `GenerateReply` per version, including BVH bytes plus metadata such as resolved seed and batch index.
6. Unity writes each BVH to:
   - an external export folder chosen by the user
   - a mirrored asset path under `Assets/MotionGen/Generated/...`
7. Each mirrored BVH is converted to an `.anim` clip by `BvhToAnimConverter`, which uses `BvhImporter` to build a temporary Humanoid avatar and bake muscle/root curves.
8. The generated batch is stored in `MotionGenGenerationHistory.asset` and shown in the **Library** tab.
9. If auto-apply is enabled, the first generated clip is assigned to the selected humanoid via a lightweight playback controller.
10. In the **Path Edit** tab, users can place translation path keys and bake corrected root translation back into the clip’s `RootT.x/y/z` curves.
11. In the **Post** tab, users can optionally create a preserved reference copy of the path-baked clip, review auto-detected contact windows, and generate a separate processed clip with smoothing/contact passes.

Phase 1 edit flow (implemented):

1. In **Library**, select a generated clip and use **Edit Selected Clip (MoMask)**.
2. Enter an edit prompt (for example, "turn this section into a jump"), define one or more time windows, and choose version/seed settings.
3. Unity samples a 22-joint source motion sequence from the selected humanoid clip and sends `BatchEditRequest` via gRPC.
4. Backend `app.py` routes edit requests to the MoMask edit pipeline and applies masked text-guided editing over the selected ranges.
5. Edited BVH variants are mirrored/imported like normal generations and persisted as new non-destructive library items with source/edit provenance.

---

## Architecture

```text
┌──────────────────────── Unity Editor ────────────────────────┐
│                                                              │
│  MotionGenWindow                                             │
│    ├─ Generate tab                                           │
│    │   └─ BatchGenerateRequest (BVH, T2M_GPT)                │
│    ├─ Library tab                                            │
│    │   ├─ preview / inspect / apply generated clips          │
│    │   └─ BatchEditRequest (source clip + edit windows)      │
│    ├─ Path Edit tab                                          │
│    │   └─ root translation key editing + bake to RootT       │
│    └─ Post tab                                               │
│        └─ reference copy + processed clip + cleanup passes   │
│                                                              │
│  MotionClient ─────────────── gRPC ───────────────┐          │
└───────────────────────────────────────────────────┼──────────┘
                                                    │
                               ┌────────────────────▼────────────────────┐
                               │ Python Backend (`motion-backend/server`)│
                               │  `app.py`                               │
                               │    ├─ Ping                              │
                               │    ├─ Generate                          │
                               │    ├─ GenerateBatch                     │
                               │    └─ Edit / EditBatch                  │
                               │                                          │
                               │  `t2mgpt_exact_bvh.py`                  │
                               │    └─ T2M-GPT inference → BVH bytes     │
                               └────────────────────┬────────────────────┘
                                                    │
                                                    ▼
                            Mirrored `.bvh` assets under `Assets/MotionGen/Generated/`
                                                    │
                                                    ▼
                        `BvhToAnimConverter` + `BvhImporter` + `HumanPoseHandler`
                                                    │
                                                    ▼
                                  Humanoid `.anim` clips + library history
```

---

## Motion Backend (`motion-backend/`)

The backend is a small gRPC service that currently exposes **BVH-only generation/editing** for `T2M-GPT` and `MoMask`.

### Key Files

| File | Purpose |
|------|---------|
| `server/app.py` | gRPC server entry point, request validation, and model selection |
| `server/t2mgpt_exact_bvh.py` | T2M-GPT inference pipeline that produces exact BVH output |
| `server/momask_bvh.py` | MoMask inference pipeline that produces BVH output |
| `server/model_paths.py` | Import-isolation helpers for switching vendored model code safely |
| `server/bvh_writer.py` | BVH writing utilities |
| `protos/motion.proto` | Shared protobuf / gRPC contract |
| `scripts/test_ping.py` | Simple connectivity test |
| `test_generate.py` | End-to-end backend generation test |

### gRPC Service

Defined in `motion-backend/protos/motion.proto`:

| RPC | Request → Response | Current status |
|-----|-------------------|----------------|
| `Ping` | `Empty` → `PingResponse` | Implemented |
| `GetDummyBVH` | `Empty` → `MotionReply` | Present in proto |
| `Generate` | `GenerateRequest` → `GenerateReply` | Implemented |
| `GenerateBatch` | `BatchGenerateRequest` → `BatchGenerateReply` | Implemented and used by Unity |
| `Edit` | `EditRequest` → `EditReply` | Implemented (MoMask edit path) |
| `EditBatch` | `BatchEditRequest` → `BatchEditReply` | Implemented and used by Unity Edit section |

Important current limitations:

- Only `BVH` output is accepted by `server/app.py`.
- `GenerateBatch` is the main path used by the Unity editor.
- `EditBatch` currently targets `MoMask` and expects a 22-joint source sequence payload.
- `MoMask` requires its own upstream checkpoints in addition to the existing backend dependencies.

### Active Inference Flows

The backend selects a generator based on the request model:

1. `T2M_GPT` via `server/t2mgpt_exact_bvh.py`
2. `MOMASK` via `server/momask_bvh.py`

Both paths end by converting recovered joints into BVH bytes and returning metadata for Unity.

### Models

| Submodule | Status |
|-----------|--------|
| `models/T2M-GPT/` | Supported backend model source |
| `models/MoMask/` | Supported backend model source |

---

## Unity Plugin (`Dissertation Plugin Playground/`)

The Unity side is an editor-first workflow built around the `MotionGen` window.

### Key Files

| File | Purpose |
|------|---------|
| `Assets/Plugins/MotionGen/Scripts/MotionGenWindow.cs` | Main editor window for generation, library management, preview, and path editing |
| `Assets/Plugins/MotionGen/Scripts/MotionClient.cs` | gRPC client wrapper |
| `Assets/Plugins/MotionGen/Scripts/MotionGenSettingsWindow.cs` | Editor settings UI and persisted defaults |
| `Assets/Plugins/MotionGen/Scripts/MotionGenGenerationHistory.cs` | Persistent generation/session history and path-key data |
| `Assets/Plugins/MotionGen/Scripts/MotionGenPostProcessor.cs` | Editor-side post-processing for smoothing, contact review, and processed clip generation |
| `Assets/Plugins/MotionGen/Scripts/BvhImporter.cs` | BVH → Humanoid clip conversion logic |
| `Assets/Plugins/MotionGen/Scripts/BvhToAnimConverter.cs` | Saves converted `.anim` assets from mirrored BVH files |
| `Assets/Plugins/MotionGen/Scripts/Generated/Motion.cs` | Generated protobuf classes |
| `Assets/Plugins/MotionGen/Scripts/Generated/MotionGrpc.cs` | Generated gRPC stubs |

### Editor Workflow

Accessed via **Window → MotionGen**, the current tabs are:

- `Generate`
  - Enter prompt, export path, generation name, FPS, duration, version count, and seed settings.
  - Optionally auto-apply the first generated clip to the selected humanoid.
- `Library`
  - View generation sessions and versions stored in `MotionGenGenerationHistory`.
  - Preview, inspect, apply, reveal, and enter path-edit mode for a selected clip.
  - When post-processing is enabled and up to date, preview/apply prefers the processed clip.
- `Path Edit`
  - Display the original generated root path as an overlay.
  - Create and move translation path keys in the Scene view.
  - Edit key time and position numerically.
  - Bake corrected root translation into the clip.
- `Post`
  - Toggle post-processing per generated item.
  - Create or refresh a preserved reference copy of the current source clip.
  - Auto-detect candidate hand/foot support contacts, then review and edit the windows before applying.
  - Generate a separate processed clip with root smoothing, motion smoothing, and support-limb contact locking.

### Animation Conversion Path

The implemented conversion path is BVH-based:

1. Unity receives BVH bytes from the backend.
2. The plugin mirrors the BVH into the Unity project under `Assets/MotionGen/Generated/...`.
3. `BvhToAnimConverter` loads the mirrored `.bvh`.
4. `BvhImporter` parses the hierarchy, builds a temporary skeleton, builds a Humanoid avatar, samples poses through `HumanPoseHandler`, and writes Humanoid muscle plus root curves.
5. The resulting `.anim` clip is saved as a Unity asset and linked into generation history.

### Path Editing

The current path editor is a **root translation correction tool**:

- Path keys store time and world-space position.
- The Scene view shows:
  - the generated path
  - the edited path
  - the current preview marker
- Editing keys does not continuously rewrite the clip.
- **Bake Corrected Clip** writes the corrected path into the clip’s `RootT.x/y/z` curves.

Rotation editing is not part of the active path-edit workflow.

### Post-Processing

The current post-processing workflow is an **editor-side clip cleanup pass**:

- It runs manually from the **Post** tab on the selected generation item.
- The source clip remains the path-baked/generated clip stored in `clipAssetPath`.
- The first apply creates or refreshes a preserved **reference clip** copied from the source clip.
- A separate **processed clip** is then generated from that reference and becomes the preferred preview/apply variant while it remains up to date with the source.
- Supported v1 passes are:
  - root smoothing
  - motion smoothing
  - hybrid contact locking with auto-detected candidate windows and manual review
  - support-limb IK style correction for hands, feet, or both
- Post-processing currently depends on having a selected humanoid `Animator` in the scene so the tool can sample and solve poses in editor time.

### Dependencies

| Package | Purpose |
|---------|---------|
| Google.Protobuf 3.33.5 | Protocol Buffer serialization |
| Grpc.Core 2.46.6 | Unity-side gRPC client |
| Grpc.Net.Client 2.76.0 | Additional .NET gRPC support |
| BVH Tools (Banana Yellow Games) | Third-party BVH-related utilities |

---

## Getting Started

### Prerequisites

- **Unity 6000.2** or later
- **Python 3.10+**
- **T2M-GPT checkpoints** and normalization stats
- **MoMask checkpoints** if you want to use the `MOMASK` model option

### 1. Clone the Repository

```bash
git clone --recurse-submodules https://github.com/jedsmith2004/Dissertation.git
cd Dissertation
```

If you already cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
```

### 2. Download Model Checkpoints

The pretrained weights are too large for GitHub and must be placed under the corresponding model directories:

```text
motion-backend/models/T2M-GPT/pretrained/
motion-backend/models/MoMask/checkpoints/
```

You can download them with the upstream script or copy them from another machine.

For `MoMask`, the upstream download script populates the `checkpoints/t2m/` subtree under `motion-backend/models/MoMask/`.

### 3. Provide HumanML3D Normalization Files

The backend needs `Mean.npy` and `Std.npy`. Place them in one of the locations the backend searches, such as:

```text
motion-backend/models/T2M-GPT/dataset/HumanML3D/
```

### 4. Start the Backend

```bash
cd motion-backend
python -m venv venv
source venv/bin/activate  # Linux/Mac
# or: venv\Scripts\activate  # Windows

pip install -r env/requirements.txt

export T2MGPT_VQ_CKPT="models/T2M-GPT/pretrained/VQVAE/net_last.pth"
export T2MGPT_TRANS_CKPT="models/T2M-GPT/pretrained/VQTransformer_corruption05/net_best_fid.pth"

cd server
python app.py
```

The server listens on `localhost:50051`.

### 5. Use the Unity Tool

1. Open `Dissertation Plugin Playground/` in Unity.
2. Select a humanoid `Animator` in the scene.
3. Open **Window → MotionGen**.
4. Generate one or more versions from the **Generate** tab.
5. Inspect and preview them in the **Library** tab.
6. Use **Path Edit** if you want to correct root translation and bake it into the clip.

---

## Tests

Currently relevant repo-level test and debug entry points include:

| File | Purpose |
|------|---------|
| `motion-backend/test_generate.py` | Backend generation smoke test |
| `motion-backend/scripts/test_ping.py` | gRPC connectivity check |

---

## Project Structure

```text
├── README.md
├── .gitignore
├── PATH_SYSTEM.md
├── PIPELINE.md
│
├── motion-backend/
│   ├── env/
│   │   └── requirements.txt
│   ├── models/
│   │   ├── T2M-GPT/
│   │   └── MoMask/
│   ├── protos/
│   │   └── motion.proto
│   ├── scripts/
│   │   └── test_ping.py
│   ├── server/
│   │   ├── app.py
│   │   ├── bvh_writer.py
│   │   ├── motion_pb2.py
│   │   ├── motion_pb2_grpc.py
│   │   └── t2mgpt_exact_bvh.py
│   └── test_generate.py
│
└── Dissertation Plugin Playground/
    └── Assets/
        ├── MotionGen/
        │   ├── Editor/
        │   └── Generated/
        └── Plugins/
            └── MotionGen/
                └── Scripts/
                    ├── BvhImporter.cs
                    ├── BvhToAnimConverter.cs
                    ├── MotionClient.cs
                    ├── MotionGenGenerationHistory.cs
                    ├── MotionGenSettingsWindow.cs
                    ├── MotionGenWindow.cs
                    └── Generated/
                        ├── Motion.cs
                        └── MotionGrpc.cs
```

---

## License

This is a dissertation project by Jack Smith. The T2M-GPT and MoMask submodules remain subject to their own licences. The BVH Tools plugin is by Banana Yellow Games.
