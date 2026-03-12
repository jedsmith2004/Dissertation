import json
import os
import re
import sys
import tempfile
from contextlib import contextmanager
from pathlib import Path
from typing import Optional, Tuple

import numpy as np
import torch

BACKEND = Path(__file__).resolve().parents[1]
T2MGPT = BACKEND / "models" / "T2M-GPT"
MOMASK_DIR = BACKEND / "models" / "MoMask"


@contextmanager
def t2mgpt_path():
    """Context manager that temporarily sets sys.path to include only T2M-GPT."""
    original_path = sys.path.copy()
    # Remove MoMask if present, ensure T2M-GPT is at front
    sys.path = [p for p in sys.path if str(MOMASK_DIR) not in p]
    if str(T2MGPT) not in sys.path:
        sys.path.insert(0, str(T2MGPT))
    try:
        yield
    finally:
        sys.path = original_path


@contextmanager
def momask_path():
    """Context manager that temporarily sets sys.path to include only MoMask."""
    original_path = sys.path.copy()
    # Remove T2M-GPT if present, ensure MoMask is at front
    sys.path = [p for p in sys.path if str(T2MGPT) not in p]
    if str(MOMASK_DIR) not in sys.path:
        sys.path.insert(0, str(MOMASK_DIR))
    try:
        yield
    finally:
        sys.path = original_path


# Import MoMask's Joint2BVH converter with isolated path
with momask_path():
    from visualization.joints2bvh import Joint2BVHConvertor  # type: ignore[reportMissingImports]


class T2MGPTExactBvhGenerator:
    """Generate the exact recover_from_ric joint motion and convert it to BVH.

    This is the authoritative pipeline used by the Unity MotionGen panel:
    T2M-GPT -> exact recover_from_ric joints -> MoMask IK BVH conversion.
    """

    def __init__(self) -> None:
        self._loaded = False
        self._clip_model = None
        self._vq_model = None
        self._trans_encoder = None
        self._mean: Optional[np.ndarray] = None
        self._std: Optional[np.ndarray] = None
        self._clip_module = None
        self._recover_from_ric = None

    def _ensure_loaded(self, device: torch.device) -> None:
        if self._loaded:
            return

        import clip  # type: ignore[reportMissingImports]
        from types import SimpleNamespace

        # Load T2M-GPT modules with isolated path
        with t2mgpt_path():
            import models.t2m_trans as trans  # type: ignore[reportMissingImports]
            import models.vqvae as vqvae  # type: ignore[reportMissingImports]
            from utils.motion_process import recover_from_ric  # type: ignore[reportMissingImports]

        clip_model, _ = clip.load("ViT-B/32", device=device, jit=False)
        clip.model.convert_weights(clip_model)
        clip_model.eval()

        args = SimpleNamespace(
            dataname="t2m",
            nb_code=512,
            code_dim=512,
            output_emb_width=512,
            down_t=2,
            stride_t=2,
            width=512,
            depth=3,
            dilation_growth_rate=3,
            vq_act="relu",
            quantizer="ema_reset",
            mu=0.99,
            block_size=51,
            embed_dim_gpt=1024,
            clip_dim=512,
            num_layers=9,
            n_head_gpt=16,
            drop_out_rate=0.1,
            ff_rate=4,
        )

        vq_model = vqvae.HumanVQVAE(args, 512, 512, 512, 2, 2, 512, 3, 3)
        vq_ckpt = torch.load(T2MGPT / "pretrained" / "VQVAE" / "net_last.pth", map_location="cpu")
        vq_model.load_state_dict(vq_ckpt["net"], strict=True)
        vq_model.eval().to(device)

        trans_encoder = trans.Text2Motion_Transformer(
            num_vq=512,
            embed_dim=1024,
            clip_dim=512,
            block_size=51,
            num_layers=9,
            n_head=16,
            drop_out_rate=0.1,
            fc_rate=4,
        )
        trans_ckpt = torch.load(T2MGPT / "pretrained" / "VQTransformer_corruption05" / "net_best_fid.pth", map_location="cpu")
        trans_encoder.load_state_dict(trans_ckpt["trans"], strict=True)
        trans_encoder.eval().to(device)

        self._clip_module = clip
        self._clip_model = clip_model
        self._vq_model = vq_model
        self._trans_encoder = trans_encoder
        self._mean = np.load(T2MGPT / "dataset" / "HumanML3D" / "Mean.npy")
        self._std = np.load(T2MGPT / "dataset" / "HumanML3D" / "Std.npy")
        self._recover_from_ric = recover_from_ric
        self._loaded = True

    @torch.no_grad()
    def generate_exact_joints(self, prompt: str, seed: int) -> np.ndarray:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self._ensure_loaded(device)

        assert self._clip_module is not None
        assert self._clip_model is not None
        assert self._vq_model is not None
        assert self._trans_encoder is not None
        assert self._mean is not None
        assert self._std is not None
        assert self._recover_from_ric is not None

        torch.manual_seed(seed)
        np.random.seed(seed)

        text_tokens = self._clip_module.tokenize([prompt], truncate=True).to(device)
        feat_clip = self._clip_model.encode_text(text_tokens).float()
        index_motion = self._trans_encoder.sample(feat_clip, if_categorial=True)
        pred_pose = self._vq_model.forward_decoder(index_motion)

        pred_np = pred_pose[0].detach().cpu().numpy().astype(np.float32)
        pred_denorm = pred_np * self._std + self._mean
        pred_tensor = torch.from_numpy(pred_denorm).unsqueeze(0).float().to(device)

        joints = self._recover_from_ric(pred_tensor, 22)[0].detach().cpu().numpy().astype(np.float32)
        joints[:, 0, 1] = (joints[:, 1, 1] + joints[:, 2, 1]) / 2.0
        return joints

    def generate_bvh(self, prompt: str, fps: int, duration_seconds: float, seed: int) -> Tuple[bytes, str, str]:
        joints = self.generate_exact_joints(prompt=prompt, seed=seed)
        requested_fps = max(1, int(fps or 20))

        with tempfile.TemporaryDirectory(prefix="motiongen_exact_bvh_") as temp_dir:
            output_path = Path(temp_dir) / "t2mgpt_generated_exact.bvh"

            old_cwd = Path.cwd()
            try:
                os.chdir(MOMASK_DIR)
                converter = Joint2BVHConvertor()
                converter.convert(joints, filename=str(output_path), iterations=100, foot_ik=False)
            finally:
                os.chdir(old_cwd)

            bvh_text = output_path.read_text(encoding="utf-8")
            bvh_text = re.sub(
                r"(Frame Time:\s+)([\d\.]+)",
                lambda m: f"{m.group(1)}{1.0 / requested_fps:.8f}",
                bvh_text,
                count=1,
            )
            data = bvh_text.encode("utf-8")

        meta = json.dumps(
            {
                "model": "T2M-GPT",
                "pipeline": "exact_recover_from_ric_to_momask_bvh",
                "prompt": prompt,
                "seed": seed,
                "requested_fps": requested_fps,
                "native_frame_count": int(joints.shape[0]),
                "native_joint_count": int(joints.shape[1]),
                "duration_seconds_request": float(duration_seconds or 0.0),
            }
        )
        return data, "t2mgpt_generated_exact.bvh", meta
