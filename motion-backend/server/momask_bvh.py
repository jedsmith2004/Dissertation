import json
import os
import re
import tempfile
from pathlib import Path
from typing import Optional, Tuple

import numpy as np
import torch

from model_paths import MOMASK_DIR, momask_path

with momask_path():
    from visualization.joints2bvh import Joint2BVHConvertor  # type: ignore[reportMissingImports]


class MoMaskBvhGenerator:
    def __init__(self) -> None:
        self._loaded = False
        self._model_name = os.getenv("MOMASK_MODEL_NAME", "t2m_nlayer8_nhead6_ld384_ff1024_cdp0.1_rvq6ns")
        self._res_name = os.getenv("MOMASK_RES_NAME", "tres_nlayer8_ld384_ff1024_rvq6ns_cdp0.2_sw")
        self._dataset_name = os.getenv("MOMASK_DATASET_NAME", "t2m")
        self._checkpoints_dir = Path(os.getenv("MOMASK_CHECKPOINTS_DIR", str(MOMASK_DIR / "checkpoints")))
        self._time_steps = int(os.getenv("MOMASK_TIME_STEPS", "18"))
        self._cond_scale = float(os.getenv("MOMASK_COND_SCALE", "4"))
        self._res_cond_scale = float(os.getenv("MOMASK_RES_COND_SCALE", "5"))
        self._temperature = float(os.getenv("MOMASK_TEMPERATURE", "1"))
        self._topkr = float(os.getenv("MOMASK_TOPKR", "0.9"))
        self._gumbel_sample = os.getenv("MOMASK_GUMBEL_SAMPLE", "").lower() in ("1", "true", "yes")
        self._native_fps = 20

        self._vq_model = None
        self._mask_transformer = None
        self._res_transformer = None
        self._mean: Optional[np.ndarray] = None
        self._std: Optional[np.ndarray] = None
        self._recover_from_ric = None

    def _ensure_loaded(self, device: torch.device) -> None:
        if self._loaded:
            return

        with momask_path():
            from models.mask_transformer.transformer import (  # type: ignore[reportMissingImports]
                MaskTransformer,
                ResidualTransformer,
            )
            from models.vq.model import RVQVAE  # type: ignore[reportMissingImports]
            from utils.get_opt import get_opt  # type: ignore[reportMissingImports]
            from utils.motion_process import recover_from_ric  # type: ignore[reportMissingImports]

        root_dir = self._checkpoints_dir / self._dataset_name / self._model_name
        model_opt_path = root_dir / "opt.txt"
        if not model_opt_path.exists():
            raise RuntimeError(
                f"MoMask checkpoint config not found at {model_opt_path}. "
                "Download the MoMask checkpoints and set MOMASK_CHECKPOINTS_DIR if needed."
            )

        old_cwd = Path.cwd()
        try:
            os.chdir(MOMASK_DIR)
            model_opt = get_opt(str(model_opt_path), device=device)
            vq_opt = get_opt(
                str(self._checkpoints_dir / self._dataset_name / model_opt.vq_name / "opt.txt"),
                device=device,
            )

            dim_pose = 251 if self._dataset_name == "kit" else 263
            vq_opt.dim_pose = dim_pose
            vq_model = RVQVAE(
                vq_opt,
                vq_opt.dim_pose,
                vq_opt.nb_code,
                vq_opt.code_dim,
                vq_opt.output_emb_width,
                vq_opt.down_t,
                vq_opt.stride_t,
                vq_opt.width,
                vq_opt.depth,
                vq_opt.dilation_growth_rate,
                vq_opt.vq_act,
                vq_opt.vq_norm,
            )
            vq_ckpt = torch.load(
                self._checkpoints_dir / self._dataset_name / vq_opt.name / "model" / "net_best_fid.tar",
                map_location="cpu",
            )
            model_key = "vq_model" if "vq_model" in vq_ckpt else "net"
            vq_model.load_state_dict(vq_ckpt[model_key])

            model_opt.num_tokens = vq_opt.nb_code
            model_opt.num_quantizers = vq_opt.num_quantizers
            model_opt.code_dim = vq_opt.code_dim

            mask_transformer = MaskTransformer(
                code_dim=model_opt.code_dim,
                cond_mode="text",
                latent_dim=model_opt.latent_dim,
                ff_size=model_opt.ff_size,
                num_layers=model_opt.n_layers,
                num_heads=model_opt.n_heads,
                dropout=model_opt.dropout,
                clip_dim=512,
                cond_drop_prob=model_opt.cond_drop_prob,
                clip_version="ViT-B/32",
                opt=model_opt,
            )
            mask_ckpt = torch.load(root_dir / "model" / "latest.tar", map_location="cpu")
            model_key = "t2m_transformer" if "t2m_transformer" in mask_ckpt else "trans"
            missing_keys, unexpected_keys = mask_transformer.load_state_dict(mask_ckpt[model_key], strict=False)
            if unexpected_keys or any(not key.startswith("clip_model.") for key in missing_keys):
                raise RuntimeError("MoMask transformer checkpoint is incompatible with the current code.")

            res_opt = get_opt(
                str(self._checkpoints_dir / self._dataset_name / self._res_name / "opt.txt"),
                device=device,
            )
            res_opt.num_quantizers = vq_opt.num_quantizers
            res_opt.num_tokens = vq_opt.nb_code
            res_transformer = ResidualTransformer(
                code_dim=vq_opt.code_dim,
                cond_mode="text",
                latent_dim=res_opt.latent_dim,
                ff_size=res_opt.ff_size,
                num_layers=res_opt.n_layers,
                num_heads=res_opt.n_heads,
                dropout=res_opt.dropout,
                clip_dim=512,
                shared_codebook=vq_opt.shared_codebook,
                cond_drop_prob=res_opt.cond_drop_prob,
                share_weight=res_opt.share_weight,
                clip_version="ViT-B/32",
                opt=res_opt,
            )
            res_ckpt = torch.load(
                self._checkpoints_dir / self._dataset_name / res_opt.name / "model" / "net_best_fid.tar",
                map_location="cpu",
            )
            missing_keys, unexpected_keys = res_transformer.load_state_dict(res_ckpt["res_transformer"], strict=False)
            if unexpected_keys or any(not key.startswith("clip_model.") for key in missing_keys):
                raise RuntimeError("MoMask residual transformer checkpoint is incompatible with the current code.")

            self._vq_model = vq_model.eval().to(device)
            self._mask_transformer = mask_transformer.eval().to(device)
            self._res_transformer = res_transformer.eval().to(device)
            self._mean = np.load(self._checkpoints_dir / self._dataset_name / model_opt.vq_name / "meta" / "mean.npy")
            self._std = np.load(self._checkpoints_dir / self._dataset_name / model_opt.vq_name / "meta" / "std.npy")
            self._recover_from_ric = recover_from_ric
            self._loaded = True
        finally:
            os.chdir(old_cwd)

    def _resolve_motion_length(self, duration_seconds: float, device: torch.device) -> torch.Tensor:
        requested_frames = max(4, int(round(max(duration_seconds, 0.1) * self._native_fps)))
        requested_frames = min(196, requested_frames)
        requested_frames = max(4, (requested_frames // 4) * 4)
        return torch.LongTensor([requested_frames // 4]).to(device).long()

    @torch.no_grad()
    def generate_exact_joints(self, prompt: str, duration_seconds: float, seed: int) -> np.ndarray:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self._ensure_loaded(device)

        assert self._vq_model is not None
        assert self._mask_transformer is not None
        assert self._res_transformer is not None
        assert self._mean is not None
        assert self._std is not None
        assert self._recover_from_ric is not None

        torch.manual_seed(seed)
        np.random.seed(seed)

        captions = [prompt]
        token_lens = self._resolve_motion_length(duration_seconds, device)
        motion_lengths = (token_lens * 4).detach().cpu().numpy()

        mids = self._mask_transformer.generate(
            captions,
            token_lens,
            timesteps=self._time_steps,
            cond_scale=self._cond_scale,
            temperature=self._temperature,
            topk_filter_thres=self._topkr,
            gsample=self._gumbel_sample,
        )
        pred_ids = self._res_transformer.generate(
            mids,
            captions,
            token_lens,
            temperature=1,
            cond_scale=self._res_cond_scale,
        )
        pred_motions = self._vq_model.forward_decoder(pred_ids)
        pred_np = pred_motions[0].detach().cpu().numpy().astype(np.float32)
        pred_denorm = pred_np * self._std + self._mean
        joint_data = pred_denorm[: int(motion_lengths[0])]
        joints = self._recover_from_ric(torch.from_numpy(joint_data).float(), 22).numpy().astype(np.float32)
        return joints

    def generate_bvh(self, prompt: str, fps: int, duration_seconds: float, seed: int) -> Tuple[bytes, str, str]:
        joints = self.generate_exact_joints(prompt=prompt, duration_seconds=duration_seconds, seed=seed)
        requested_fps = max(1, int(fps or self._native_fps))

        with tempfile.TemporaryDirectory(prefix="motiongen_momask_bvh_") as temp_dir:
            output_path = Path(temp_dir) / "momask_generated.bvh"

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
                lambda match: f"{match.group(1)}{1.0 / requested_fps:.8f}",
                bvh_text,
                count=1,
            )
            data = bvh_text.encode("utf-8")

        meta = json.dumps(
            {
                "model": "MoMask",
                "pipeline": "momask_text_to_bvh",
                "prompt": prompt,
                "seed": seed,
                "requested_fps": requested_fps,
                "native_fps": self._native_fps,
                "native_frame_count": int(joints.shape[0]),
                "native_joint_count": int(joints.shape[1]),
                "duration_seconds_request": float(duration_seconds or 0.0),
                "checkpoints_dir": str(self._checkpoints_dir),
                "checkpoint_name": self._model_name,
                "residual_checkpoint_name": self._res_name,
            }
        )
        return data, "momask_generated.bvh", meta
