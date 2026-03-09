"""
Smoke test: generate a BVH file from T2M-GPT and verify it's valid.
"""
import os, sys

# Must run from the T2M-GPT directory so relative imports resolve.
_T2M_DIR = os.path.join(os.path.dirname(__file__), "models", "T2M-GPT")
os.chdir(_T2M_DIR)

_SERVER_DIR = os.path.join(os.path.dirname(__file__), "server")
if _SERVER_DIR not in sys.path:
    sys.path.insert(0, _SERVER_DIR)

from t2mgpt_runtime import T2MGPTGenerator

gen = T2MGPTGenerator()
payload, filename, meta = gen.generate_bvh(
    prompt="a person walks forward",
    fps=30,
    duration_seconds=2.0,
    seed=42,
)

bvh_text = payload.decode("utf-8")
lines = bvh_text.split("\n")
print(f"[OK] Generated BVH: {filename}")
print(f"     Lines: {len(lines)}")
print(f"     Meta: {meta}")
print()

# Check header.
assert lines[0] == "HIERARCHY", f"Expected HIERARCHY, got: {lines[0]}"
assert lines[1].startswith("ROOT "), f"Expected ROOT, got: {lines[1]}"

# Find MOTION section.
motion_idx = None
for i, line in enumerate(lines):
    if line.strip() == "MOTION":
        motion_idx = i
        break
assert motion_idx is not None, "Could not find MOTION section!"

frames_line = lines[motion_idx + 1]
assert frames_line.startswith("Frames:"), f"Expected 'Frames:', got: {frames_line}"
n_frames = int(frames_line.split(":")[1].strip())
print(f"     Frames: {n_frames}")

frame_time_line = lines[motion_idx + 2]
frame_time = float(frame_time_line.split(":")[1].strip())
print(f"     Frame time: {frame_time:.6f}s (= {1.0/frame_time:.1f} fps)")

# Check a data line.
data_lines = [l for l in lines[motion_idx+3:] if l.strip()]
print(f"     Data lines: {len(data_lines)}")
assert len(data_lines) == n_frames, f"Mismatch: {len(data_lines)} data lines vs {n_frames} frames"

first_frame_values = data_lines[0].split()
# Root = 6 channels, 21 other joints = 3 each, total = 6 + 21*3 = 69
# Actually: 22 joints × 3 rot channels + 3 root pos channels = 69
# Wait — let's count: root has 6 (3 pos + 3 rot), 21 joints have 3 rot each = 6 + 63 = 69
# BUT leaf joints (end sites) have no channels. Joints with children get 3 channels.
# Let me count non-leaf joints:
# Joint 10 (L_Foot), 11 (R_Foot), 15 (Head), 20 (L_Wrist), 21 (R_Wrist) are leaves
# So: root = 6 channels, 16 non-leaf non-root joints = 3 each = 48, 
# plus leaves... wait, leaves still get CHANNELS in BVH if they have JOINT, only End Site has none.
# In our skeleton, leaves are End Sites below the last JOINT in each chain.
# Actual channels: root(6) + all 21 other JOINTs(3 each) = 6 + 63 = 69
print(f"     Values per frame: {len(first_frame_values)} (expected 69)")
assert len(first_frame_values) == 69, f"Expected 69 values, got {len(first_frame_values)}"

# Save for inspection.
out_path = os.path.join(os.path.dirname(__file__), "outputs", "test_bvh_output.bvh")
os.makedirs(os.path.dirname(out_path), exist_ok=True)
with open(out_path, "w") as f:
    f.write(bvh_text)

# Print first 25 lines of the BVH for visual inspection.
print("\n--- BVH header (first 25 lines) ---")
for line in lines[:25]:
    print(line)

print("\n--- First frame data ---")
vals = [float(v) for v in first_frame_values]
print(f"  Root pos: ({vals[0]:.4f}, {vals[1]:.4f}, {vals[2]:.4f})")
print(f"  Root rot: Z={vals[3]:.2f}° Y={vals[4]:.2f}° X={vals[5]:.2f}°")

print("\n[PASS] BVH generation smoke test passed!")
