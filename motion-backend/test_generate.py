
import os
import sys
import numpy as np
import torch

BACKEND = os.path.dirname(os.path.abspath(__file__))
T2MGPT  = os.path.join(BACKEND, "models", "T2M-GPT")
sys.path.insert(0, T2MGPT)

PROMPT = "A person does a handstand."
SEED   = 42
FPS    = 20

print(f"Prompt : {PROMPT}")
print(f"Seed   : {SEED}")
print()

import clip
import models.vqvae as vqvae
import models.t2m_trans as trans
from utils.motion_process import recover_root_rot_pos, recover_from_ric

device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
print(f"Device: {device}")

# Load CLIP
clip_model, _ = clip.load("ViT-B/32", device=device, jit=False)
clip.model.convert_weights(clip_model)
clip_model.eval()

# Load VQ-VAE
from types import SimpleNamespace
args = SimpleNamespace(
    dataname="t2m", nb_code=512, code_dim=512, output_emb_width=512,
    down_t=2, stride_t=2, width=512, depth=3, dilation_growth_rate=3,
    vq_act="relu", quantizer="ema_reset", mu=0.99,
    block_size=51, embed_dim_gpt=1024, clip_dim=512,
    num_layers=9, n_head_gpt=16, drop_out_rate=0.1, ff_rate=4,
)

vq_model = vqvae.HumanVQVAE(args, 512, 512, 512, 2, 2, 512, 3, 3)
vq_ckpt = torch.load(os.path.join(T2MGPT, "pretrained", "VQVAE", "net_last.pth"), map_location="cpu")
vq_model.load_state_dict(vq_ckpt["net"], strict=True)
vq_model.eval().to(device)

# Load GPT Transformer
trans_encoder = trans.Text2Motion_Transformer(
    num_vq=512, embed_dim=1024, clip_dim=512, block_size=51,
    num_layers=9, n_head=16, drop_out_rate=0.1, fc_rate=4,
)
trans_ckpt = torch.load(os.path.join(T2MGPT, "pretrained", "VQTransformer_corruption05", "net_best_fid.pth"), map_location="cpu")
trans_encoder.load_state_dict(trans_ckpt["trans"], strict=True)
trans_encoder.eval().to(device)

# Load Mean/Std
mean = np.load(os.path.join(T2MGPT, "dataset", "HumanML3D", "Mean.npy"))
std  = np.load(os.path.join(T2MGPT, "dataset", "HumanML3D", "Std.npy"))
print(f"Mean shape: {mean.shape}, Std shape: {std.shape}")

# ── Generate ──
torch.manual_seed(SEED)
np.random.seed(SEED)

with torch.no_grad():
    text_tokens = clip.tokenize([PROMPT], truncate=True).to(device)
    feat_clip = clip_model.encode_text(text_tokens).float()

    # Sample VQ tokens 
    index_motion = trans_encoder.sample(feat_clip, if_categorial=True)

    # Decode VQ tokens → 263-dim motion features
    pred_pose = vq_model.forward_decoder(index_motion)  # [1, T, 263]

print(f"VQ tokens generated: {index_motion.shape}")
print(f"Raw motion shape: {pred_pose.shape}")

# Denormalize
pred_np = pred_pose[0].cpu().numpy()
pred_denorm = pred_np * std + mean
pred_tensor = torch.from_numpy(pred_denorm).unsqueeze(0).float().to(device)

# Recover 3D joint positions (world space)
# Note: recover_from_ric returns joints where:
#   - XZ are in world space
#   - Y is ALREADY absolute height for joints 1-21 (NOT root-relative)
#   - Joint 0 (root/pelvis) Y comes from the raw height feature
# So NO additional Y correction is needed.
joints = recover_from_ric(pred_tensor, 22)[0].cpu().numpy()  # [T, 22, 3]

print(f"\nJoint positions shape: {joints.shape}")
print(f"X range: [{joints[:,:,0].min():.3f}, {joints[:,:,0].max():.3f}]")
print(f"Y range: [{joints[:,:,1].min():.3f}, {joints[:,:,1].max():.3f}]")
print(f"Z range: [{joints[:,:,2].min():.3f}, {joints[:,:,2].max():.3f}]")

# Print per-joint Y on frame 0 to verify skeleton structure
f0 = joints[0]
joint_names = [
    "pelvis", "L_hip", "R_hip", "spine1", "L_knee", "R_knee",
    "spine2", "L_ankle", "R_ankle", "spine3", "L_foot", "R_foot",
    "neck", "L_collar", "R_collar", "head", "L_shoulder", "R_shoulder",
    "L_elbow", "R_elbow", "L_wrist", "R_wrist"
]
print("\nFrame 0 joint heights (Y):")
for i, name in enumerate(joint_names):
    print(f"  {i:2d} {name:12s}  Y={f0[i,1]:.3f}")

root_heights = joints[:, 0, 1]
print(f"\nRoot Y over time — min: {root_heights.min():.3f}, max: {root_heights.max():.3f}, "
      f"range: {root_heights.max() - root_heights.min():.3f}")

# Fix: Joint 0 (root) Y from r_pos is unreliable (different scale).
# Place root Y midway between the two hips instead.
joints[:, 0, 1] = (joints[:, 1, 1] + joints[:, 2, 1]) / 2.0

print(f"Fixed root Y (avg of hips) — min: {joints[:, 0, 1].min():.3f}, max: {joints[:, 0, 1].max():.3f}")
print()

n_frames = joints.shape[0]

# ── Animate as 3D stick figure ──
import matplotlib
matplotlib.use("TkAgg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d import Axes3D
import matplotlib.animation as animation

SKELETON_CHAINS = [
    [0, 1, 4, 7, 10],        # left leg
    [0, 2, 5, 8, 11],        # right leg
    [0, 3, 6, 9, 12, 15],    # spine → head
    [9, 13, 16, 18, 20],     # left arm
    [9, 14, 17, 19, 21],     # right arm
]
CHAIN_COLORS = ["blue", "red", "black", "cyan", "orange"]

fig = plt.figure(figsize=(10, 8))
ax = fig.add_subplot(111, projection="3d")

# Fixed-size window that follows the root joint each frame.
# Use the body size (max spread across any single frame) to set the window.
per_frame_spread = np.array([
    joints[i].max(axis=0) - joints[i].min(axis=0) for i in range(n_frames)
])
body_size = per_frame_spread.max() * 0.75  # half-extent with padding
WINDOW = max(body_size, 1.5)  # at least 1.5 units so it's not too tight

print(f"Typical body spread per frame: {per_frame_spread.mean(axis=0)}")
print(f"View window half-extent: {WINDOW:.2f}")


def update(frame_idx):
    ax.cla()

    jf = joints[frame_idx]  # (22, 3) — x, y_up, z
    root = jf[0]            # follow the root joint

    # Camera follows root — fixed window around it
    # matplotlib 3D: X plot = X data, Y plot = Z data, Z plot = Y data (up)
    ax.set_xlim(root[0] - WINDOW, root[0] + WINDOW)
    ax.set_ylim(root[2] - WINDOW, root[2] + WINDOW)
    ax.set_zlim(0, WINDOW * 2)  # ground at 0, up
    ax.set_xlabel("X")
    ax.set_ylabel("Z")
    ax.set_zlabel("Y (up)")
    ax.set_title(f'"{PROMPT}"\nFrame {frame_idx + 1}/{n_frames}', fontsize=10)
    ax.view_init(elev=20, azim=-60)

    # Scatter joints (X, Z, Y mapping so Y is up)
    ax.scatter(jf[:, 0], jf[:, 2], jf[:, 1], c="royalblue", s=50, depthshade=True, zorder=5)

    # Label key joints
    for idx, name in [(0, "root"), (15, "head"), (20, "L.hand"), (21, "R.hand"), (10, "L.foot"), (11, "R.foot")]:
        ax.text(jf[idx, 0], jf[idx, 2], jf[idx, 1] + 0.05, f" {name}", fontsize=8, color="dimgray")

    # Draw skeleton
    for chain, color in zip(SKELETON_CHAINS, CHAIN_COLORS):
        xs = [jf[i, 0] for i in chain]
        zs = [jf[i, 2] for i in chain]
        ys = [jf[i, 1] for i in chain]
        ax.plot(xs, zs, ys, c=color, linewidth=3, zorder=4)

    # Ground plane marker
    ax.plot([root[0] - 0.3, root[0] + 0.3], [root[2], root[2]], [0, 0], c="gray", linewidth=1, alpha=0.5)


ani = animation.FuncAnimation(fig, update, frames=n_frames, interval=1000 / FPS, repeat=True)
plt.tight_layout()
print("Displaying animation... Close the window when done.")
plt.show()

# Optionally save as GIF
save_gif = input("Save as GIF? (y/n): ").strip().lower()
if save_gif == "y":
    out_path = os.path.join(BACKEND, "test_output.gif")
    print(f"Saving to {out_path} ...")
    ani.save(out_path, writer="pillow", fps=FPS)
    print("Done!")
