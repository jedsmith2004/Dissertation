"""Quick smoke test for t2mgpt_runtime.py generate_json after fixes."""
import sys, os, json

BACKEND = os.path.dirname(os.path.abspath(__file__))
T2MGPT  = os.path.join(BACKEND, "models", "T2M-GPT")
sys.path.insert(0, os.path.join(BACKEND, "server"))
sys.path.insert(0, T2MGPT)

os.environ["T2MGPT_VQ_CKPT"] = os.path.join(T2MGPT, "pretrained", "VQVAE", "net_last.pth")
os.environ["T2MGPT_TRANS_CKPT"] = os.path.join(T2MGPT, "pretrained", "VQTransformer_corruption05", "net_best_fid.pth")

from t2mgpt_runtime import T2MGPTGenerator

gen = T2MGPTGenerator()
data, filename, meta = gen.generate_json("a person walks forward", fps=20, duration_seconds=2.0, seed=42)

motion = json.loads(data)
frames = motion["frames"]
print(f"Frames: {len(frames)}, FPS: {motion['fps']}")

f0 = frames[0]
print(f"Frame 0 position: x={f0['position']['x']:.4f} y={f0['position']['y']:.4f} z={f0['position']['z']:.4f}")

j0 = f0["joints"]
joint_names = [
    "pelvis","R_hip","L_hip","spine1","R_knee","L_knee",
    "spine2","R_ankle","L_ankle","spine3","R_foot","L_foot",
    "neck","L_collar","R_collar","head","L_shoulder","R_shoulder",
    "L_elbow","R_elbow","L_wrist","R_wrist"
]
print("\nFrame 0 joint positions (root-relative):")
for i, name in enumerate(joint_names):
    print(f"  {i:2d} {name:12s}  x={j0[i]['x']:+.3f}  y={j0[i]['y']:+.3f}  z={j0[i]['z']:+.3f}")

# Verify root is at origin (root-relative = always 0,0,0)
r = j0[0]
print(f"\nRoot joint (should be ~0,0,0): x={r['x']:.4f} y={r['y']:.4f} z={r['z']:.4f}")

# Check root trajectory speed
import math
total_dist = 0
for i in range(1, len(frames)):
    dx = frames[i]["position"]["x"] - frames[i-1]["position"]["x"]
    dz = frames[i]["position"]["z"] - frames[i-1]["position"]["z"]
    total_dist += math.sqrt(dx*dx + dz*dz)
duration = len(frames) / motion["fps"]
avg_speed = total_dist / duration if duration > 0 else 0
print(f"\nTrajectory: total XZ distance={total_dist:.2f}m over {duration:.1f}s = {avg_speed:.2f} m/s")

# Check last frame for travel distance
fN = frames[-1]
print(f"Last frame position: x={fN['position']['x']:.4f} y={fN['position']['y']:.4f} z={fN['position']['z']:.4f}")
print(f"Meta: {meta}")
