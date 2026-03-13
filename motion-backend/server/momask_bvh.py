import json
import os
import re
import tempfile
from pathlib import Path
from typing import List, Optional, Tuple

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
        self._motion_process = None
        self._param_util = None
        self._skeleton_cls = None

    def _ensure_loaded(self, device: torch.device) -> None:
        if self._loaded:
            return

        with momask_path():
            from models.mask_transformer.transformer import (  # type: ignore[reportMissingImports]
                MaskTransformer,
                ResidualTransformer,
            )
            from models.vq.model import RVQVAE  # type: ignore[reportMissingImports]
            from common.skeleton import Skeleton  # type: ignore[reportMissingImports]
            from utils.get_opt import get_opt  # type: ignore[reportMissingImports]
            from utils import motion_process, paramUtil  # type: ignore[reportMissingImports]
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
            self._motion_process = motion_process
            self._param_util = paramUtil
            self._skeleton_cls = Skeleton
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

    def _convert_joints_to_motion_features(self, source_joints: np.ndarray) -> np.ndarray:
        if source_joints.ndim != 3 or source_joints.shape[1] != 22 or source_joints.shape[2] != 3:
            raise RuntimeError("source_motion must be a [frames, 22, 3] joint sequence.")

        assert self._motion_process is not None
        assert self._param_util is not None
        assert self._skeleton_cls is not None

        motion_process = self._motion_process
        param_util = self._param_util

        n_raw_offsets = torch.from_numpy(param_util.t2m_raw_offsets)
        target_skeleton = self._skeleton_cls(n_raw_offsets, param_util.t2m_kinematic_chain, "cpu")
        target_offsets = target_skeleton.get_offsets_joints(torch.from_numpy(source_joints[0]).float())

        motion_process.l_idx1, motion_process.l_idx2 = 5, 8
        motion_process.fid_r, motion_process.fid_l = [8, 11], [7, 10]
        motion_process.face_joint_indx = [2, 1, 17, 16]
        motion_process.r_hip, motion_process.l_hip = 2, 1
        motion_process.n_raw_offsets = n_raw_offsets
        motion_process.kinematic_chain = param_util.t2m_kinematic_chain
        motion_process.tgt_offsets = target_offsets

        features, _, _, _ = motion_process.process_file(source_joints, feet_thre=0.002)
        if features.ndim != 2 or features.shape[1] != 263:
            raise RuntimeError("Failed to convert source joints into MoMask feature vectors.")

        return features.astype(np.float32)

    def _build_edit_mask(
        self,
        seq_len: int,
        motion_length_frames: int,
        source_fps: int,
        edit_ranges: List[Tuple[float, float]],
    ) -> torch.Tensor:
        if seq_len <= 0:
            raise RuntimeError("Edit mask cannot be created for an empty token sequence.")

        edit_mask = torch.zeros((1, seq_len), dtype=torch.bool)
        clip_duration = max(0.001, motion_length_frames / float(max(1, source_fps)))

        for start_seconds, end_seconds in edit_ranges:
            start_seconds = max(0.0, float(start_seconds))
            end_seconds = max(start_seconds, float(end_seconds))
            if end_seconds <= start_seconds:
                continue

            start_ratio = min(1.0, start_seconds / clip_duration)
            end_ratio = min(1.0, end_seconds / clip_duration)
            start_token = int(np.floor(start_ratio * seq_len))
            end_token = int(np.ceil(end_ratio * seq_len))

            start_token = max(0, min(seq_len - 1, start_token))
            end_token = max(start_token + 1, min(seq_len, end_token))
            edit_mask[:, start_token:end_token] = True

        if not torch.any(edit_mask):
            edit_mask[:, :] = True

        return edit_mask

    @torch.no_grad()
    def edit_bvh(
        self,
        prompt: str,
        fps: int,
        seed: int,
        source_joints: np.ndarray,
        source_fps: int,
        edit_ranges: List[Tuple[float, float]],
    ) -> Tuple[bytes, str, str]:
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

        source_features = self._convert_joints_to_motion_features(source_joints)
        max_motion_length = 196
        motion_length = max(4, min(max_motion_length, int(source_features.shape[0])))
        motion_length = max(4, (motion_length // 4) * 4)

        trimmed = source_features[:motion_length]
        normalized = (trimmed - self._mean) / self._std
        motion = torch.from_numpy(normalized)[None].to(device)

        token_lens = torch.LongTensor([motion_length // 4]).to(device).long()
        captions = [prompt]

        tokens, _ = self._vq_model.encode(motion)
        edit_mask = self._build_edit_mask(
            seq_len=tokens.shape[1],
            motion_length_frames=motion_length,
            source_fps=max(1, int(source_fps or self._native_fps)),
            edit_ranges=edit_ranges,
        ).to(device)

        mids = self._mask_transformer.edit(
            captions,
            tokens[..., 0].clone(),
            token_lens,
            timesteps=self._time_steps,
            cond_scale=self._cond_scale,
            temperature=self._temperature,
            topk_filter_thres=self._topkr,
            gsample=self._gumbel_sample,
            force_mask=False,
            edit_mask=edit_mask.clone(),
        )
        mids = self._res_transformer.generate(
            mids,
            captions,
            token_lens,
            temperature=1,
            cond_scale=self._res_cond_scale,
        )

        pred_motions = self._vq_model.forward_decoder(mids)
        pred_np = pred_motions[0].detach().cpu().numpy().astype(np.float32)
        pred_denorm = pred_np * self._std + self._mean
        joint_data = pred_denorm[:motion_length]
        joints = self._recover_from_ric(torch.from_numpy(joint_data).float(), 22).numpy().astype(np.float32)

        requested_fps = max(1, int(fps or self._native_fps))

        with tempfile.TemporaryDirectory(prefix="motiongen_momask_edit_bvh_") as temp_dir:
            output_path = Path(temp_dir) / "momask_edited.bvh"

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
                "pipeline": "momask_text_edit_bvh",
                "prompt": prompt,
                "seed": seed,
                "requested_fps": requested_fps,
                "native_fps": self._native_fps,
                "native_frame_count": int(joints.shape[0]),
                "native_joint_count": int(joints.shape[1]),
                "source_native_frame_count": int(source_joints.shape[0]),
                "source_fps": int(source_fps),
                "edit_ranges": [[float(start), float(end)] for start, end in edit_ranges],
                "checkpoint_name": self._model_name,
                "residual_checkpoint_name": self._res_name,
            }
        )
        return data, "momask_edited.bvh", meta

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
