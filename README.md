# Smart Motion Editing — AI-Generated Animation in Unity

A dissertation project that brings **text-to-motion generation** directly into the Unity Editor. Type a natural language prompt (e.g. *"a person walks forward"*), and the system generates a retargetable Humanoid animation clip — ready for use on any character.

The project consists of two parts:

| Component | Location | Language |
|-----------|----------|----------|
| **Motion Backend** — deep-learning inference server | `motion-backend/` | Python |
| **Unity Plugin** — editor tooling + runtime playback | `Dissertation Plugin Playground/` | C# |

They communicate over **gRPC** on `localhost:50051`.

---

## Conceptual Overview

### The Problem

Creating realistic human motion for games and interactive applications is time-consuming and expensive. Motion capture requires specialised hardware and actors; hand-animation demands significant artistic skill. Recent deep-learning models can generate plausible motion from text descriptions, but they output raw numeric data (joint positions/rotations) that is not directly usable in a game engine.

### The Solution

This project bridges that gap with an end-to-end pipeline:

1. **Text → Motion** — A pre-trained T2M-GPT model converts a text prompt into a sequence of 263-dimensional motion feature vectors (HumanML3D representation).
2. **Motion → Skeleton** — The feature vectors are decoded into 22-joint SMPL skeleton poses (positions or local rotation matrices) and root trajectories.
3. **Skeleton → Animation Clip** — The Unity plugin retargets the generated skeleton onto any Humanoid avatar using direction-based IK solving and Unity's `HumanPoseHandler` muscle system, producing a standard `.anim` clip.
4. **Editing** — The generated clip can be previewed, keyframe-edited, and refined directly in the Unity Editor.

The result is a **prompt-to-playable-animation** workflow that takes seconds rather than hours.

---

## Architecture

```
┌──────────────────────── Unity Editor ────────────────────────┐
│                                                               │
│  MotionGenWindow (Editor Window)                              │
│    │  Prompt: "a person waves hello"                          │
│    │  Settings: FPS, duration, seed, format (BVH/JSON)        │
│    │                                                          │
│    ├─── Generate ──► MotionClient ──── gRPC ────┐             │
│    │                                             │             │
│    │                           ┌─────────────────▼──────────┐ │
│    │                           │   Python Backend (50051)   │ │
│    │                           │                            │ │
│    │                           │   CLIP text encoder        │ │
│    │                           │        ↓                   │ │
│    │                           │   GPT Transformer          │ │
│    │                           │        ↓                   │ │
│    │                           │   VQ-VAE decoder           │ │
│    │                           │        ↓                   │ │
│    │                           │   263-dim features         │ │
│    │                           │        ↓                   │ │
│    │                           │   BVH writer / JSON export │ │
│    │                           └─────────────┬──────────────┘ │
│    │                                         │                │
│    ├── JSON path ◄── GenerateReply ──────────┘                │
│    │   └► direction-based IK + HumanPoseHandler               │
│    │      → Humanoid muscle curves → .anim clip               │
│    │                                                          │
│    ├── BVH path ◄── GenerateReply                             │
│    │   └► BvhImporter → AvatarBuilder + HumanPoseHandler      │
│    │      → Humanoid muscle curves → .anim clip               │
│    │                                                          │
│    └── Preview / Keyframe Edit / Apply to Character           │
│                                                               │
│  SMPLMotionPlayer (Runtime)                                   │
│    └── Plays exported .smpl.json on any Humanoid at runtime   │
└───────────────────────────────────────────────────────────────┘
```

---

## Motion Backend (`motion-backend/`)

A **gRPC server** that wraps a T2M-GPT text-to-motion model for on-demand inference.

### Key Components

| File | Purpose |
|------|---------|
| `server/app.py` | gRPC server entry point (port 50051, 4 worker threads) |
| `server/t2mgpt_runtime.py` | Core inference engine — lazy-loads CLIP + VQ-VAE + GPT Transformer, generates motion from text |
| `server/bvh_writer.py` | Converts per-frame local rotation matrices + root positions into standard BVH files |
| `server/dummy_bvh.py` | Hardcoded minimal BVH for connectivity testing |
| `protos/motion.proto` | gRPC service contract (shared between Python and C#) |

### gRPC Service

Defined in `motion.proto`:

| RPC | Request → Response | Description |
|-----|-------------------|-------------|
| `Ping` | `Empty` → `PingResponse` | Health check — returns version, CUDA availability, GPU name |
| `GetDummyBVH` | `Empty` → `MotionReply` | Returns a hardcoded test BVH (connectivity validation) |
| `Generate` | `GenerateRequest` → `GenerateReply` | Text-to-motion generation |

`GenerateRequest` fields: `prompt`, `fps`, `duration_seconds`, `seed`, `format` (BVH or JSON), `model` (T2M_GPT or MoMask).

### Inference Pipeline (`T2MGPTGenerator`)

1. **Text encoding** — CLIP ViT-B/32 encodes the prompt into a latent vector.
2. **Token generation** — A GPT Transformer autoregressively generates VQ codebook indices conditioned on the text embedding.
3. **Motion decoding** — A VQ-VAE decoder maps the token sequence back to 263-dimensional HumanML3D motion features.
4. **Denormalization** — Features are scaled back using pre-computed Mean/Std statistics from the training data.
5. **Post-processing** — The 263-dim vector is decomposed into:
   - **JSON path**: 22 joint world positions via `recover_from_ric`, root trajectory extraction, foot grounding, speed capping, and FPS resampling.
   - **BVH path**: 21-joint cont6d local rotations (feature indices 67–193), root quaternion recovery, conversion to rotation matrices, BVH skeleton construction, and frame resampling.

### Models (Git Submodules)

| Submodule | Description |
|-----------|-------------|
| `models/T2M-GPT/` | Primary model — VQ-VAE + GPT Transformer for text-to-motion (includes pretrained checkpoints, HumanML3D data utilities) |
| `models/MoMask/` | Alternative model — masked transformer approach (not yet integrated into the server) |

### Configuration

The inference engine is configured via environment variables:

| Variable | Required | Description |
|----------|----------|-------------|
| `T2MGPT_VQ_CKPT` | Yes | Path to VQ-VAE checkpoint (`.pth`) |
| `T2MGPT_TRANS_CKPT` | Yes | Path to GPT Transformer checkpoint (`.pth`) |
| `T2MGPT_MEAN_NPY` / `T2MGPT_STD_NPY` | No | Normalization statistics (auto-discovered from model directories) |
| `T2MGPT_NB_CODE` | No | VQ codebook size (default: 512) |
| `T2MGPT_EMBED_DIM_GPT` | No | GPT embedding dimension (default: 1024) |
| `T2MGPT_NUM_LAYERS` | No | Transformer layers (default: 9) |

### Skeleton

Both output formats use the **22-joint SMPL/HumanML3D skeleton**:

```
Pelvis ─┬─ L_Hip ── L_Knee ── L_Ankle ── L_Foot
        ├─ R_Hip ── R_Knee ── R_Ankle ── R_Foot
        └─ Spine1 ── Spine2 ── Spine3 ─┬─ Neck ── Head
                                        ├─ L_Collar ── L_Shoulder ── L_Elbow ── L_Wrist
                                        └─ R_Collar ── R_Shoulder ── R_Elbow ── R_Wrist
```

BVH output uses 69 channels per frame (6 root channels + 21 joints × 3 rotation channels, ZYX Euler order).

---

## Unity Plugin (`Dissertation Plugin Playground/`)

A Unity 6000.2+ project containing the **Smart Motion Editing** plugin (`com.jacksmith.smart-motion-editing`).

### Key Components

| File | Purpose |
|------|---------|
| `Assets/Plugins/MotionGen/Scripts/MotionClient.cs` | gRPC client wrapper — sends requests to the Python backend |
| `Assets/Plugins/MotionGen/Scripts/MotionGenWindow.cs` | Main editor window — prompt input, generation, retargeting, preview, keyframe editing (1600+ lines) |
| `Assets/Plugins/MotionGen/Scripts/MotionGenSettingsWindow.cs` | Settings UI + `MotionGenEditorSettings` ScriptableObject |
| `Assets/Plugins/MotionGen/Scripts/BvhImporter.cs` | Full BVH parser → Humanoid AnimationClip converter |
| `Assets/Plugins/MotionGen/Scripts/SMPLMotionPlayer.cs` | Runtime component for playing generated motion on any Humanoid |
| `Assets/Plugins/MotionGen/Scripts/MotionManager.cs` | Runtime gRPC wrapper MonoBehaviour |
| `Assets/Plugins/MotionGen/Scripts/MotionTestUI.cs` | Debug IMGUI panel for testing connectivity |
| `Assets/Plugins/MotionGen/Scripts/Generated/Motion.cs` | Auto-generated Protobuf message classes |
| `Assets/Plugins/MotionGen/Scripts/Generated/MotionGrpc.cs` | Auto-generated gRPC service stubs |

### Editor Workflow (`MotionGenWindow`)

Accessed via **Window → MotionGen**, the editor window provides:

- **Generate Panel** — Text prompt input, generation settings (FPS, duration, seed, output format), and a Generate button that calls the backend and produces an AnimationClip.
- **Scene & Animation Panel** — Humanoid avatar detection, clip preview with play/pause/stop/loop controls, timeline scrubbing, and pose sampling.
- **Keyframe Panel** — Create and navigate keyframes on the active clip for manual refinement.
- **Animations Panel** — Lists all generated clips for quick access.
- **Retarget Calibration** — Captures a T-pose reference to compute per-bone correction quaternions that improve retargeting accuracy.
- **SMPL Sidecar Export** — Optionally exports a `.smpl.json` file with per-frame bone rotations for runtime playback.

### Retargeting Pipeline

The plugin supports two retargeting paths depending on the output format:

**JSON Path** (direction-based IK):
1. Parse 22-joint world positions from the backend's JSON response.
2. Convert to Unity coordinate space (Z-flip).
3. For each frame and each bone, compute the target bone direction from the joint positions.
4. Solve bone rotations using the bind-pose direction and a parent-aware rotation chain.
5. Apply two-bone IK refinement for arms and legs to match endpoint positions.
6. Feed the solved skeleton into `HumanPoseHandler` to extract Unity muscle values.
7. Bake muscle curves into a Humanoid-compatible `.anim` clip.

**BVH Path** (direct rotation import):
1. Parse the BVH text into a joint hierarchy and per-frame Euler rotations.
2. Build a temporary Unity skeleton (GameObjects) matching the BVH structure.
3. Construct a Humanoid Avatar via `AvatarBuilder.BuildHumanAvatar` with the SMPL→Unity bone mapping.
4. For each frame, apply the BVH rotations (with coordinate handedness conversion) and sample via `HumanPoseHandler`.
5. Bake muscle + root motion curves into a `.anim` clip.

### Runtime Playback (`SMPLMotionPlayer`)

A MonoBehaviour that plays generated motion at runtime on any Humanoid character without requiring FBX import:
- Supports both SMPL sidecar format (per-frame bone quaternions) and raw generated JSON (22-joint positions with live IK retargeting).
- Configurable playback speed, looping, and auto-play on start.

### Dependencies

| Package | Purpose |
|---------|---------|
| Google.Protobuf 3.33.5 | Protocol Buffer serialization |
| Grpc.Core 2.46.6 | gRPC client (native `grpc_csharp_ext` for Win/Mac/Linux) |
| Grpc.Net.Client 2.76.0 | .NET gRPC client support |
| BVH Tools (Banana Yellow Games) | Third-party BVH parsing/recording utilities |

---

## Getting Started

### Prerequisites

- **Unity 6000.2** or later
- **Python 3.10+** with CUDA-capable GPU (recommended) or CPU
- **T2M-GPT model checkpoints** — downloaded separately (see below)

### 1. Clone the Repository

```bash
git clone --recurse-submodules https://github.com/jedsmith2004/Dissertation.git
cd Dissertation
```

If you already cloned without `--recurse-submodules`:
```bash
git submodule update --init --recursive
```

### 2. Download Model Checkpoints (not in git — too large)

The pretrained weights (~1 GB) are too large for GitHub and must be downloaded separately. They go into `motion-backend/models/T2M-GPT/pretrained/`.

**Option A — Use the official download script (requires `gdown`):**
```bash
pip install gdown
cd motion-backend/models/T2M-GPT
bash dataset/prepare/download_model.sh
```
This downloads and unzips the checkpoints into:
```
motion-backend/models/T2M-GPT/pretrained/
├── VQVAE/
│   ├── net_last.pth          (75 MB)  ← VQ-VAE checkpoint
│   └── net_best_fid.pth      (75 MB)
└── VQTransformer_corruption05/
    └── net_best_fid.pth      (872 MB) ← GPT Transformer checkpoint
```

**Option B — Manual download:**

Download `VQTrans_pretrained.zip` from [Google Drive](https://drive.google.com/file/d/1LaOvwypF-jM2Axnq5dc-Iuvv3w_G-WDE) and unzip it into `motion-backend/models/T2M-GPT/pretrained/`.

**Option C — Copy from another machine:**

If you already have the checkpoints on another machine, copy the entire `pretrained/` folder:
```
motion-backend/models/T2M-GPT/pretrained/
```

### 3. Download HumanML3D Data (normalization stats)

The inference pipeline needs `Mean.npy` and `Std.npy` from the HumanML3D dataset. These are auto-discovered from several locations. If they're not already present:

```bash
cd motion-backend/models/T2M-GPT
mkdir -p dataset/HumanML3D
```

Then place `Mean.npy` and `Std.npy` in `dataset/HumanML3D/`. These can be obtained from the [HumanML3D dataset](https://github.com/EricGuo5513/HumanML3D).

> **Note:** The server auto-searches for these files in `dataset/HumanML3D/`, `checkpoints/t2m/*/meta/`, and other common locations. If you have the full T2M-GPT checkpoints directory, they may already be present there.

### 4. Backend Setup

```bash
cd motion-backend

# Create and activate a virtual environment
python -m venv venv
source venv/bin/activate  # Linux/Mac
# or: venv\Scripts\activate  # Windows

# Install dependencies
pip install -r env/requirements.txt
pip install git+https://github.com/openai/CLIP.git

# Set checkpoint paths (adjust if your paths differ)
export T2MGPT_VQ_CKPT="models/T2M-GPT/pretrained/VQVAE/net_last.pth"
export T2MGPT_TRANS_CKPT="models/T2M-GPT/pretrained/VQTransformer_corruption05/net_best_fid.pth"

# Start the server (CWD must be inside T2M-GPT for model imports)
cd models/T2M-GPT
python -m server.app
```

The server will listen on `localhost:50051`.

### 5. Unity Setup

1. Open the `Dissertation Plugin Playground/` folder in Unity 6000.2+.
2. Unity will regenerate the `Library/` folder on first open (this takes a few minutes).
3. Open the plugin window: **Window → MotionGen**.
4. Ensure the Python backend is running.
5. Type a motion prompt and click **Generate**.
6. The generated clip will appear in `Assets/MotionGen/Generated/` and can be previewed and applied to any Humanoid character in the scene.

---

## Tests

The backend includes several test scripts:

| Test | Description |
|------|-------------|
| `test_bvh_gen.py` | BVH generation smoke test — validates hierarchy, frame count, channel count (69 per frame) |
| `test_bvh_roundtrip.py` | IK round-trip verification — generates motion, runs inverse/forward kinematics, compares with 3D visualisation |
| `test_generate.py` | End-to-end generation with 3D matplotlib animation and optional GIF export |
| `test_server_gen.py` | JSON generation smoke test — validates frame structure, joint positions, trajectory speed |

Run from the `motion-backend/` directory:
```bash
cd models/T2M-GPT
python -m pytest ../../test_bvh_gen.py -v
```

---

## Project Structure

```
├── README.md
├── .gitignore
│
├── motion-backend/                      # Python inference server
│   ├── server/
│   │   ├── app.py                       # gRPC server entry point
│   │   ├── t2mgpt_runtime.py            # T2M-GPT inference engine
│   │   ├── bvh_writer.py                # BVH file generation
│   │   ├── dummy_bvh.py                 # Test BVH data
│   │   ├── motion_pb2.py                # Generated Protobuf bindings
│   │   └── motion_pb2_grpc.py           # Generated gRPC stubs
│   ├── protos/
│   │   └── motion.proto                 # gRPC service definition
│   ├── proto-gen/                       # C# Protobuf code generator
│   ├── models/
│   │   ├── T2M-GPT/                     # T2M-GPT submodule (primary)
│   │   └── MoMask/                      # MoMask submodule (future)
│   ├── scripts/                         # Manual test/debug scripts
│   ├── env/                             # Python environment config
│   └── test_*.py                        # Test suite
│
└── Dissertation Plugin Playground/      # Unity 6000.2 project
    ├── Assets/
    │   ├── Plugins/MotionGen/Scripts/   # Core plugin code
    │   │   ├── MotionClient.cs          # gRPC client
    │   │   ├── MotionGenWindow.cs       # Editor window (main UI)
    │   │   ├── MotionGenSettingsWindow.cs
    │   │   ├── BvhImporter.cs           # BVH → Humanoid clip
    │   │   ├── SMPLMotionPlayer.cs      # Runtime motion playback
    │   │   ├── MotionManager.cs         # Runtime gRPC wrapper
    │   │   ├── MotionTestUI.cs          # Debug UI
    │   │   └── Generated/               # Auto-generated Protobuf/gRPC
    │   ├── Plugins/BVH Tools/           # Third-party BVH utilities
    │   ├── MotionGen/Generated/         # Generated clips and models
    │   └── MotionGen/Editor/            # Editor settings asset
    ├── Packages/
    │   ├── manifest.json
    │   └── com.jacksmith.smart-motion-editing/  # Plugin package manifest
    └── ProjectSettings/                 # Unity project configuration
```

---

## License

This is a dissertation project by Jack Smith. The T2M-GPT and MoMask model submodules are subject to their respective licences. The BVH Tools plugin is by Banana Yellow Games.
