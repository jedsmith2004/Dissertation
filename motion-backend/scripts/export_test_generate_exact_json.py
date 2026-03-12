import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np
import torch

BACKEND = Path(__file__).resolve().parents[1]
T2MGPT = BACKEND / "models" / "T2M-GPT"
sys.path.insert(0, str(T2MGPT))

import clip  # type: ignore[reportMissingImports]
import models.t2m_trans as trans  # type: ignore[reportMissingImports]
import models.vqvae as vqvae  # type: ignore[reportMissingImports]
from utils.motion_process import recover_from_ric  # type: ignore[reportMissingImports]

JOINT_NAMES = [
    "pelvis", "L_hip", "R_hip", "spine1", "L_knee", "R_knee",
    "spine2", "L_ankle", "R_ankle", "spine3", "L_foot", "R_foot",
    "neck", "L_collar", "R_collar", "head", "L_shoulder", "R_shoulder",
    "L_elbow", "R_elbow", "L_wrist", "R_wrist",
]


def build_models(device: torch.device):
    clip_model, _ = clip.load("ViT-B/32", device=device, jit=False)
    clip.model.convert_weights(clip_model)
    clip_model.eval()

    from types import SimpleNamespace

    args = SimpleNamespace(
        dataname="t2m", nb_code=512, code_dim=512, output_emb_width=512,
        down_t=2, stride_t=2, width=512, depth=3, dilation_growth_rate=3,
        vq_act="relu", quantizer="ema_reset", mu=0.99,
        block_size=51, embed_dim_gpt=1024, clip_dim=512,
        num_layers=9, n_head_gpt=16, drop_out_rate=0.1, ff_rate=4,
    )

    vq_model = vqvae.HumanVQVAE(args, 512, 512, 512, 2, 2, 512, 3, 3)
    vq_ckpt = torch.load(T2MGPT / "pretrained" / "VQVAE" / "net_last.pth", map_location="cpu")
    vq_model.load_state_dict(vq_ckpt["net"], strict=True)
    vq_model.eval().to(device)

    trans_encoder = trans.Text2Motion_Transformer(
        num_vq=512, embed_dim=1024, clip_dim=512, block_size=51,
        num_layers=9, n_head=16, drop_out_rate=0.1, fc_rate=4,
    )
    trans_ckpt = torch.load(T2MGPT / "pretrained" / "VQTransformer_corruption05" / "net_best_fid.pth", map_location="cpu")
    trans_encoder.load_state_dict(trans_ckpt["trans"], strict=True)
    trans_encoder.eval().to(device)

    mean = np.load(T2MGPT / "dataset" / "HumanML3D" / "Mean.npy")
    std = np.load(T2MGPT / "dataset" / "HumanML3D" / "Std.npy")
    return clip_model, vq_model, trans_encoder, mean, std


@torch.no_grad()
def generate_exact_joints(prompt: str, seed: int, device: torch.device):
    clip_model, vq_model, trans_encoder, mean, std = build_models(device)

    torch.manual_seed(seed)
    np.random.seed(seed)

    text_tokens = clip.tokenize([prompt], truncate=True).to(device)
    feat_clip = clip_model.encode_text(text_tokens).float()
    index_motion = trans_encoder.sample(feat_clip, if_categorial=True)
    pred_pose = vq_model.forward_decoder(index_motion)

    pred_np = pred_pose[0].detach().cpu().numpy().astype(np.float32)
    pred_denorm = pred_np * std + mean
    pred_tensor = torch.from_numpy(pred_denorm).unsqueeze(0).float().to(device)

    joints = recover_from_ric(pred_tensor, 22)[0].detach().cpu().numpy().astype(np.float32)
    joints[:, 0, 1] = (joints[:, 1, 1] + joints[:, 2, 1]) / 2.0
    return joints


def save_exact_json(out_path: Path, prompt: str, seed: int, fps: int, joints: np.ndarray):
    frames = []
    for frame in joints:
        frames.append(
            {
                "joints": [
                    {"x": float(j[0]), "y": float(j[1]), "z": float(j[2])}
                    for j in frame
                ]
            }
        )

    payload = {
        "schema": "t2mgpt-test-generate-exact-v1",
        "prompt": prompt,
        "seed": seed,
        "fps": fps,
        "jointNames": JOINT_NAMES,
        "frames": frames,
    }
    out_path.write_text(json.dumps(payload), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="Export the exact recover_from_ric joint stream used by test_generate.py")
    parser.add_argument("--prompt", default="A person does a handstand.")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--fps", type=int, default=20)
    parser.add_argument("--out", default=str(BACKEND.parent / "Dissertation Plugin Playground" / "Assets" / "MotionGen" / "Generated" / "t2mgpt_test_generate_exact.json"))
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    joints = generate_exact_joints(args.prompt, args.seed, device)
    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    save_exact_json(out_path, args.prompt, args.seed, args.fps, joints)

    print(f"[OK] Wrote exact joint JSON: {out_path}")
    print(f"     Frames: {len(joints)}")
    print(f"     Prompt: {args.prompt}")
    print(f"     Seed: {args.seed}")


if __name__ == "__main__":
    main()
