# MotionGen Pipeline: Click-to-Animation

Complete pipeline from clicking "Generate" in the MotionGen panel to the .anim being applied to the character (assuming auto-apply is selected).

---

## **1. Click "Generate" in MotionGen Panel (Unity)**

- User sets prompt, FPS, seed in Settings → clicks "Generate"
- Selects desired model (T2M-GPT or MoMask) from dropdown
- `MotionGenWindow.GenerateMotion()` coroutine starts

---

## **2. Create gRPC Request (MotionGenWindow.cs)**

```
MotionGenWindow gets settings:
  - Prompt: "walk forward"
  - FPS: 20
  - Seed: 12345
  - Model: T2MGPT (from dropdown)
  - DurationSeconds: 2.0

Creates MotionClient and calls:
  GenerateAsync(prompt, fps, durationSeconds, seed, format, model)
```

---

## **3. Send to Backend (MotionClient.cs)**

```
GenerateAsync() creates protobuf GenerateRequest:
  - Prompt = "walk forward"
  - Fps = 20
  - DurationSeconds = 2.0
  - Seed = 12345
  - Format = BVH (always)
  - Model = T2M_GPT (mapped from enum)

Sends gRPC message to server:127.0.0.1:50051
```

---

## **4. Backend Receives & Routes (app.py)**

```
MotionService.Generate() receives request:
  - Validates Model == T2M_GPT
  - Calls T2MGPTExactBvhGenerator.generate_bvh(
      prompt="walk forward",
      fps=20,
      duration_seconds=2.0,
      seed=12345
    )
```

---

## **5. Generate Exact Joints (t2mgpt_exact_bvh.py - Part A)**

```
Within t2mgpt_path() context (isolated to T2M-GPT):
  
  _ensure_loaded():
    - Loads CLIP model (ViT-B/32)
    - Loads VQ-VAE model
    - Loads Transformer (VQTransformer_corruption05)
    - Loads Mean/Std normalization vectors
    - Loads recover_from_ric function

generate_exact_joints():
  - Tokenizes prompt with CLIP
  - Encodes text → CLIP embedding
  - Samples motion codes from Transformer (conditional on CLIP)
  - Decodes codes with VQ-VAE → motion features (motion_dim × num_frames)
  - Denormalization: features * std + mean
  - recover_from_ric(): converts recovered features → 22 joint positions
    (shape: [num_frames, 22 joints, 3 coords])
  - Centers root Y position between hips
  - Returns joint array
```

---

## **6. Convert Joints to BVH (t2mgpt_exact_bvh.py - Part B)**

```
Within momask_path() context (isolated to MoMask):
  
  Joint2BVHConvertor.convert():
    - Reorders 22 joints to BVH skeleton order
    - Uses template BVH (MoMask's humanoid.bvh)
    - Applies inverse kinematics (IK):
      * 100 iterations of optimization
      * Enforces foot constraints
      * Solves for rotation angles matching joint positions
    - Outputs final BVH file

Frame timing adjustment:
  - Original frame time: 1/20 = 0.05 seconds
  - Converts to BVH "Frame Time: 0.05000" line

Returns to app.py:
  - BVH file bytes (ascii text)
  - Filename: "t2mgpt_generated_exact.bvh"
  - Meta: JSON with model, pipeline, prompt, seed, fps, joint count
```

---

## **7. Backend Sends Response**

```
GenerateReply message:
  - format = BVH
  - data = [BVH file bytes]
  - meta = "{model: T2M-GPT, pipeline: exact_recover_from_ric_to_momask_bvh, ...}"
  - filename = "t2mgpt_generated_exact.bvh"
```

---

## **8. Save BVH Asset (MotionGenWindow.cs)**

```
response.Data → written to disk:
  Assets/MotionGen/Generated/generated.bvh

AssetDatabase.Refresh():
  - Unity recognizes new .bvh file
  - Creates .bvh.meta file automatically
```

---

## **9. Convert BVH to AnimationClip (BvhToAnimConverter.cs)**

```
TryConvertBvhAssetToAnim(
  bvhAssetPath = "Assets/MotionGen/Generated/generated.bvh",
  targetAnimator = null  ← CRITICAL: no retargeting
)

Reads BVH file → calls BvhImporter.ImportAsHumanoid():
  
  Since targetAnimator == null:
    → ImportAsHumanoidFromSourceSkeleton():
      - Parses BVH hierarchy
      - Extracts joint names & rotations
      - Builds temporary skeleton GameObject
      - Iterates through BVH frames
      - For each frame:
        * Extracts quaternion rotations from BVH
        * Also extracts root position (XYZ)
      - Creates AnimationClip with EditorCurveBinding:
        * Position curves (for root XYZ)
        * Euler rotation curves (for each joint)
      - Sets clip frame rate from BVH
      - Returns clip + temporary Avatar

SaveOrReplaceAnimationClip():
  - Saves clip to Assets/MotionGen/Generated/generated.anim
  - AssetDatabase.SaveAssets()
  - AssetDatabase.Refresh()
```

---

## **10. Apply to Character (MotionGenWindow.cs)**

```
if (_autoApplyOnGenerate && _selectedAnimator != null):
  
  ApplyGeneratedClipNonDestructive(generatedClip):
    - Gets selected Animator's Humanoid rig
    - Applies AnimationClip to Animator
    - Sets clip as active/playing
    
Final result:
  ✅ Character plays the generated motion in the viewport
  ✅ Both .bvh and .anim files saved to Assets/MotionGen/Generated/
```

---

## **Key Design Points**

- **Isolated imports** - T2M-GPT and MoMask use separate sys.path contexts (no namespace pollution)
- **No retargeting** - Avatar is null, so BVH → .anim uses source skeleton (preserves exact motion)
- **Exact recovery** - `recover_from_ric()` reconstructs canonical 22-joint positions from T2M-GPT features
- **IK conversion** - MoMask converts joint positions to skeleton rotations via IK solver

---

# MotionGen Pipeline: Local Backend Packaging + Model Install (Phase 2)

## **1. Backend Startup From Unity Settings**

- User opens **MotionGen Settings**
- Clicks **Start Local Backend**
- Unity launches `motion-backend/scripts/start_local_backend.ps1`
- PowerShell resolves Python executable and runs `server/app.py`
- Unity pings gRPC health until backend is reachable

## **2. Backend Health Check**

- **Ping Backend** uses `MotionClient.PingAsync()`
- Status text reports version + CUDA/device info when reachable

## **3. Model Status Discovery**

- **Refresh Model Status** invokes:
  - `python scripts/model_manager.py status --manifest packaging/backend_manifest.json`
- Script evaluates required files and returns per-model state:
  - `installed`
  - `missing`
  - `corrupt` (checksum mismatch when checksum is configured)

## **4. On-Demand Model Install**

- **Install T2M-GPT** / **Install MoMask** invokes:
  - `python scripts/model_manager.py install --manifest ... --model <id> [--base-url ...]`
- Installer downloads artifact files to expected checkpoint paths
- Optional checksum validation is applied when `sha256` is present
- Unity refreshes status after install completes

---

# MotionGen Pipeline: Text-Guided Clip Editing (MoMask)

Complete Phase 1 edit pipeline from selecting an existing clip in Library to saving edited non-destructive variants.

---

## **1. Select Source Clip + Edit Inputs (Unity)**

- User selects a generated clip in **Library**
- In **Edit Selected Clip (MoMask)**, user sets:
  - edit prompt
  - one or more edit windows (`start_seconds`, `end_seconds`)
  - edited version count and seed mode

---

## **2. Build Edit Request (MotionGenWindow.cs)**

```
MotionGenWindow.GenerateEditedBatchAsync(...):
  - samples source clip on selected humanoid Animator
  - extracts 22 joint world positions per frame
  - flattens into MotionJointSequence.joint_positions
  - builds BatchEditRequest
```

---

## **3. Send to Backend (MotionClient.cs)**

```
MotionClient.EditBatchAsync(...) sends:
  - prompt
  - fps
  - count / use_random_seed / seed
  - format = BVH
  - model = MOMASK
  - source_motion = MotionJointSequence
  - edit_ranges = repeated EditRange
```

---

## **4. Backend Edit Routing (app.py)**

```
MotionService.EditBatch():
  - validates model + format
  - parses source_motion into [frames, 22, 3]
  - normalizes edit ranges
  - resolves seed per requested variant
  - calls MoMaskBvhGenerator.edit_bvh(...)
```

---

## **5. MoMask Edit Inference (momask_bvh.py)**

```
edit_bvh(...):
  - converts source joints -> MoMask feature space
  - encodes source motion tokens with RVQVAE
  - builds token-level edit mask from requested time windows
  - runs MaskTransformer.edit(...) + ResidualTransformer.generate(...)
  - decodes edited features and recovers 22-joint positions
  - converts edited joints to BVH via Joint2BVHConvertor
```

---

## **6. Save + Import Edited Variants (Unity)**

```
MotionGenWindow.SaveEditedBatch(...):
  - writes edited BVH files (external + mirrored assets)
  - imports each mirrored BVH to .anim
  - stores non-destructive history item with provenance:
      isEditResult, sourceSessionId, sourceItemId,
      sourceClipAssetPath, editPrompt, editRanges
```

---

## **7. Preview / Apply / Path Edit as Normal**

- Edited clips are normal library items and work with:
  - preview/apply
  - path edit
  - post-processing
