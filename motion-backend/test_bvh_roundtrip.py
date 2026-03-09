"""
Visual comparison: original T2M-GPT stick figure vs BVH-reconstructed stick figure.
Verifies the IK → BVH pipeline is lossless.
"""
import os, sys
import numpy as np
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation

_T2M_DIR = os.path.join(os.path.dirname(__file__), "models", "T2M-GPT")
os.chdir(_T2M_DIR)

_SERVER_DIR = os.path.join(os.path.dirname(__file__), "server")
if _SERVER_DIR not in sys.path:
    sys.path.insert(0, _SERVER_DIR)

import torch
from t2mgpt_runtime import T2MGPTGenerator
from bvh_writer import PARENTS, JOINT_NAMES, CHILDREN, rotmat_to_euler_zyx

# ── Generate ──
gen = T2MGPTGenerator()
os.environ["T2MGPT_VQ_CKPT"] = os.environ.get("T2MGPT_VQ_CKPT", "")
os.environ["T2MGPT_TRANS_CKPT"] = os.environ.get("T2MGPT_TRANS_CKPT", "")

PROMPT = "a person walks forward"
SEED = 42
FPS = 20

# We need to get both the original positions AND the BVH data.
# First, do the same inference as generate_bvh but capture intermediate data.

gen._load()

import clip
from utils.motion_process import recover_root_rot_pos, recover_from_ric
from utils.paramUtil import t2m_kinematic_chain, t2m_raw_offsets
from utils.skeleton import Skeleton

torch.manual_seed(SEED)
np.random.seed(SEED)

text = clip.tokenize([PROMPT], truncate=True).to(gen.device)
feat_clip_text = gen.clip_model.encode_text(text).float()
token_ids = gen.trans_encoder.sample(feat_clip_text, if_categorial=True)
pred_pose = gen.vq_model.forward_decoder(token_ids)

if gen.mean is not None and gen.std is not None:
    pred_np = pred_pose[0].detach().cpu().numpy().astype(np.float32)
    pred_np = (pred_np * gen.std) + gen.mean
    pred_pose = torch.from_numpy(pred_np).unsqueeze(0).to(gen.device)

joint_xyz = recover_from_ric(pred_pose, 22)[0].detach().cpu().numpy()
T = joint_xyz.shape[0]
print(f"Frames: {T}")

# Fix root Y.
joint_xyz[:, 0, 1] = (joint_xyz[:, 1, 1] + joint_xyz[:, 2, 1]) / 2.0
# Ground feet.
foot_y = min(float(joint_xyz[0, 10, 1]), float(joint_xyz[0, 11, 1]))
joint_xyz[:, :, 1] -= foot_y

original_positions = joint_xyz.copy()

# ── Run IK ──
skeleton = Skeleton(
    torch.from_numpy(t2m_raw_offsets).float(),
    t2m_kinematic_chain,
    "cpu",
)
skeleton.get_offsets_joints(torch.from_numpy(joint_xyz[0]).float())

from utils.quaternion import qrot_np, qmul_np, qinv_np, qbetween_np

quat_params = skeleton.inverse_kinematics_np(
    joint_xyz, face_joint_idx=[2, 1, 14, 13], smooth_forward=True
)

# ── Forward kinematics to reconstruct ──
reconstructed = skeleton.forward_kinematics_np(
    quat_params, joint_xyz[:, 0, :], do_root_R=True
)

# ── Error analysis ──
error = np.linalg.norm(original_positions - reconstructed, axis=-1)  # (T, 22)
print(f"Mean joint error: {error.mean():.4f} m")
print(f"Max joint error:  {error.max():.4f} m")
print(f"Per-joint mean error (mm):")
for j in range(22):
    print(f"  {JOINT_NAMES[j]:15s}: {error[:, j].mean()*1000:.2f} mm")

# ── Animate side-by-side ──
BONES = []
for chain in t2m_kinematic_chain:
    for i in range(len(chain) - 1):
        BONES.append((chain[i], chain[i + 1]))

fig = plt.figure(figsize=(14, 6))
ax1 = fig.add_subplot(121, projection='3d')
ax2 = fig.add_subplot(122, projection='3d')

def update(frame):
    for ax, data, title in [(ax1, original_positions, "Original"),
                             (ax2, reconstructed, "IK Reconstructed")]:
        ax.cla()
        ax.set_title(f"{title} frame {frame}")
        ax.set_xlim(-1, 1)
        ax.set_ylim(0, 2)
        ax.set_zlim(-1, 1)
        ax.set_xlabel('X')
        ax.set_ylabel('Y')
        ax.set_zlabel('Z')

        pos = data[frame]
        ax.scatter(pos[:, 0], pos[:, 1], pos[:, 2], c='blue', s=10)
        for a, b in BONES:
            ax.plot([pos[a, 0], pos[b, 0]], [pos[a, 1], pos[b, 1]], [pos[a, 2], pos[b, 2]], 'b-', linewidth=1.5)

anim = FuncAnimation(fig, update, frames=T, interval=50)
plt.tight_layout()
plt.show()
