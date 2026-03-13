import json
import os
import re
import tempfile
from pathlib import Path
from typing import Iterable, Tuple

import numpy as np
import torch

from model_paths import MOMASK_DIR, momask_path
from momask_bvh import MoMaskBvhGenerator

with momask_path():
    from visualization.joints2bvh import Joint2BVHConvertor  # type: ignore[reportMissingImports]


class MoMaskEditBvhGenerator(MoMaskBvhGenerator):
    def __init__(self) -> None:
        super().__init__()
        self._process_file = None
        self._source_feet_threshold = float(os.getenv("MOMASK_EDIT_FEET_THRESHOLD", "0.002"))

    def _ensure_edit_loaded(self, device: torch.device) -> None:
        self._ensure_loaded(device)
        if self._process_file is None:
            with momask_path():
                from utils.motion_process import process_file  # type: ignore[reportMissingImports]

            self._process_file = process_file

    @torch.no_grad()
    def edit_joint_sequence(
        self,
        prompt: str,
        seed: int,
        source_joints: np.ndarray,
        source_fps: int,
        edit_ranges_seconds: Iterable[Tuple[float, float]],
    ) -> np.ndarray:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self._ensure_edit_loaded(device)

        assert self._vq_model is not None
        assert self._mask_transformer is not None
        assert self._res_transformer is not None
        assert self._mean is not None
        assert self._std is not None
        assert self._recover_from_ric is not None
        assert self._process_file is not None

        if source_joints.ndim != 3 or source_joints.shape[1:] != (22, 3):
            raise RuntimeError("MoMask editing expects source joints with shape [frames, 22, 3].")

        source_features = self._process_file(source_joints.astype(np.float32), self._source_feet_threshold)
        motion_length = int(source_features.shape[0])
        if motion_length < 8:
            raise RuntimeError("Source motion is too short for editing.")
        if motion_length > 196:
            raise RuntimeError("Source motion is too long for MoMask editing (max 196 feature frames).")

        torch.manual_seed(seed)
        np.random.seed(seed)

        normalized_motion = (source_features - self._mean) / self._std
        padded_motion = normalized_motion
        if motion_length < 196:
            padded_motion = np.concatenate(
                [normalized_motion, np.zeros((196 - motion_length, normalized_motion.shape[1]), dtype=np.float32)],
                axis=0,
            )

        motion_tensor = torch.from_numpy(padded_motion).unsqueeze(0).float().to(device)
        token_lens = torch.LongTensor([max(1, motion_length // 4)]).to(device).long()
        edited_motion_length = int(token_lens[0].item() * 4)
        captions = [prompt]

        tokens, _ = self._vq_model.encode(motion_tensor)
        edit_mask = self._build_edit_mask(
            token_count=int(tokens.shape[1]),
            motion_length=edited_motion_length,
            source_fps=max(1, int(source_fps or self._native_fps)),
            edit_ranges_seconds=edit_ranges_seconds,
            device=device,
        )

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
            edit_mask=edit_mask,
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
        edited_features = pred_denorm[:edited_motion_length]
        joints = self._recover_from_ric(torch.from_numpy(edited_features).float(), 22).numpy().astype(np.float32)
        return joints

    def edit_bvh(
        self,
        prompt: str,
        fps: int,
        seed: int,
        source_joints: np.ndarray,
        source_fps: int,
        edit_ranges_seconds: Iterable[Tuple[float, float]],
    ) -> Tuple[bytes, str, str]:
        joints = self.edit_joint_sequence(
            prompt=prompt,
            seed=seed,
            source_joints=source_joints,
            source_fps=source_fps,
            edit_ranges_seconds=edit_ranges_seconds,
        )
        requested_fps = max(1, int(fps or source_fps or self._native_fps))

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
                "pipeline": "momask_text_guided_edit_bvh",
                "prompt": prompt,
                "seed": seed,
                "requested_fps": requested_fps,
                "native_fps": self._native_fps,
                "native_frame_count": int(joints.shape[0]),
                "native_joint_count": int(joints.shape[1]),
                "edit_ranges_seconds": [[float(start), float(end)] for start, end in edit_ranges_seconds],
                "source_frame_count": int(source_joints.shape[0]),
                "source_joint_count": int(source_joints.shape[1]),
                "source_fps": int(source_fps),
                "checkpoint_name": self._model_name,
                "residual_checkpoint_name": self._res_name,
            }
        )
        return data, "momask_edited.bvh", meta

    @staticmethod
    def _build_edit_mask(
        token_count: int,
        motion_length: int,
        source_fps: int,
        edit_ranges_seconds: Iterable[Tuple[float, float]],
        device: torch.device,
    ) -> torch.Tensor:
        edit_mask = torch.zeros((1, token_count), dtype=torch.bool, device=device)
        any_range = False
        for start_seconds, end_seconds in edit_ranges_seconds:
            start_seconds = max(0.0, float(start_seconds))
            end_seconds = max(start_seconds, float(end_seconds))
            start_frame = int(np.floor(start_seconds * source_fps))
            end_frame = int(np.ceil(end_seconds * source_fps))
            start_token = max(0, min(token_count - 1, start_frame // 4))
            end_token = max(start_token + 1, min(token_count, max(start_token + 1, end_frame // 4)))
            edit_mask[:, start_token:end_token] = True
            any_range = True

        if not any_range:
            raise RuntimeError("At least one valid edit range is required.")

        if motion_length <= 0:
            raise RuntimeError("Source motion length is invalid.")

        return edit_mask
