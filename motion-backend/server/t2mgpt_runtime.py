import json
import os
import sys
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import torch


_MODEL_DIR = Path(__file__).resolve().parents[1] / "models" / "T2M-GPT"
if str(_MODEL_DIR) not in sys.path:
    sys.path.insert(0, str(_MODEL_DIR))


class T2MGPTGenerator:
    """Lazy-loaded T2M-GPT runtime for text-to-motion generation."""

    def __init__(self):
        self._loaded = False
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self.clip_model = None
        self.vq_model = None
        self.trans_encoder = None
        self.mean = None
        self.std = None

    def _build_default_args(self):
        return SimpleNamespace(
            dataname=os.getenv("T2MGPT_DATANAME", "t2m"),
            quantizer=os.getenv("T2MGPT_QUANTIZER", "ema_reset"),
            nb_code=int(os.getenv("T2MGPT_NB_CODE", "512")),
            code_dim=int(os.getenv("T2MGPT_CODE_DIM", "512")),
            output_emb_width=int(os.getenv("T2MGPT_OUTPUT_EMB_WIDTH", "512")),
            down_t=int(os.getenv("T2MGPT_DOWN_T", "2")),
            stride_t=int(os.getenv("T2MGPT_STRIDE_T", "2")),
            width=int(os.getenv("T2MGPT_WIDTH", "512")),
            depth=int(os.getenv("T2MGPT_DEPTH", "3")),
            dilation_growth_rate=int(os.getenv("T2MGPT_DILATION_GROWTH_RATE", "3")),
            vq_act=os.getenv("T2MGPT_VQ_ACT", "relu"),
            block_size=int(os.getenv("T2MGPT_BLOCK_SIZE", "51")),
            embed_dim_gpt=int(os.getenv("T2MGPT_EMBED_DIM_GPT", "1024")),
            clip_dim=int(os.getenv("T2MGPT_CLIP_DIM", "512")),
            num_layers=int(os.getenv("T2MGPT_NUM_LAYERS", "9")),
            n_head_gpt=int(os.getenv("T2MGPT_N_HEAD_GPT", "16")),
            drop_out_rate=float(os.getenv("T2MGPT_DROPOUT", "0.1")),
            ff_rate=int(os.getenv("T2MGPT_FF_RATE", "4")),
            mu=float(os.getenv("T2MGPT_MU", "0.99")),
        )

    def _resolve_required_paths(self):
        vq_ckpt = os.getenv("T2MGPT_VQ_CKPT", "").strip()
        trans_ckpt = os.getenv("T2MGPT_TRANS_CKPT", "").strip()
        if not vq_ckpt or not trans_ckpt:
            raise RuntimeError(
                "Missing T2M-GPT checkpoints. Set T2MGPT_VQ_CKPT and T2MGPT_TRANS_CKPT environment variables."
            )
        if not os.path.exists(vq_ckpt):
            raise RuntimeError(f"T2MGPT_VQ_CKPT not found: {vq_ckpt}")
        if not os.path.exists(trans_ckpt):
            raise RuntimeError(f"T2MGPT_TRANS_CKPT not found: {trans_ckpt}")
        return vq_ckpt, trans_ckpt

    def _try_load_normalization_stats(self):
        mean_env = os.getenv("T2MGPT_MEAN_NPY", "").strip()
        std_env = os.getenv("T2MGPT_STD_NPY", "").strip()

        candidates = []
        if mean_env and std_env:
            candidates.append((mean_env, std_env))

        root = Path(__file__).resolve().parents[1]
        candidates.extend(
            [
                (str(root / "dataset" / "HumanML3D" / "Mean.npy"), str(root / "dataset" / "HumanML3D" / "Std.npy")),
                (str(root / "models" / "T2M-GPT" / "dataset" / "HumanML3D" / "Mean.npy"), str(root / "models" / "T2M-GPT" / "dataset" / "HumanML3D" / "Std.npy")),
            ]
        )

        for mean_path, std_path in candidates:
            if os.path.exists(mean_path) and os.path.exists(std_path):
                self.mean = np.load(mean_path).astype(np.float32)
                self.std = np.load(std_path).astype(np.float32)
                print(f"[MotionGen] Loaded normalization stats: {mean_path} , {std_path}")
                return

        self.mean = None
        self.std = None
        print("[MotionGen] Warning: Mean/Std stats not found; motion may look unnatural. Set T2MGPT_MEAN_NPY and T2MGPT_STD_NPY.")

    def _load(self):
        if self._loaded:
            return

        import clip
        import models.vqvae as vqvae
        import models.t2m_trans as trans

        args = self._build_default_args()
        vq_ckpt_path, trans_ckpt_path = self._resolve_required_paths()

        clip_model, _ = clip.load("ViT-B/32", device=self.device, jit=False)
        clip.model.convert_weights(clip_model)
        clip_model.eval()
        for p in clip_model.parameters():
            p.requires_grad = False

        vq_model = vqvae.HumanVQVAE(
            args,
            args.nb_code,
            args.code_dim,
            args.output_emb_width,
            args.down_t,
            args.stride_t,
            args.width,
            args.depth,
            args.dilation_growth_rate,
        )

        trans_encoder = trans.Text2Motion_Transformer(
            num_vq=args.nb_code,
            embed_dim=args.embed_dim_gpt,
            clip_dim=args.clip_dim,
            block_size=args.block_size,
            num_layers=args.num_layers,
            n_head=args.n_head_gpt,
            drop_out_rate=args.drop_out_rate,
            fc_rate=args.ff_rate,
        )

        vq_ckpt = torch.load(vq_ckpt_path, map_location="cpu")
        trans_ckpt = torch.load(trans_ckpt_path, map_location="cpu")

        vq_model.load_state_dict(vq_ckpt["net"], strict=True)
        trans_encoder.load_state_dict(trans_ckpt["trans"], strict=True)

        vq_model.eval().to(self.device)
        trans_encoder.eval().to(self.device)

        self.clip_model = clip_model
        self.vq_model = vq_model
        self.trans_encoder = trans_encoder
        self._try_load_normalization_stats()
        self._loaded = True

    @staticmethod
    def _resample(values: np.ndarray, target_len: int) -> np.ndarray:
        if len(values) == target_len:
            return values
        if len(values) <= 1:
            return np.repeat(values, target_len, axis=0)

        old_x = np.linspace(0.0, 1.0, num=len(values))
        new_x = np.linspace(0.0, 1.0, num=target_len)

        if values.ndim == 1:
            return np.interp(new_x, old_x, values)

        out = np.zeros((target_len, values.shape[1]), dtype=np.float32)
        for i in range(values.shape[1]):
            out[:, i] = np.interp(new_x, old_x, values[:, i])
        return out

    @torch.no_grad()
    def generate_json(self, prompt: str, fps: int, duration_seconds: float, seed: int):
        self._load()

        import clip
        from utils.motion_process import recover_root_rot_pos, recover_from_ric

        if seed:
            torch.manual_seed(seed)
            np.random.seed(seed)

        text = clip.tokenize([prompt], truncate=True).to(self.device)
        feat_clip_text = self.clip_model.encode_text(text).float()

        token_ids = self.trans_encoder.sample(feat_clip_text, if_categorial=True)
        pred_pose = self.vq_model.forward_decoder(token_ids)  # [1, T, 263]

        if self.mean is not None and self.std is not None and pred_pose.shape[-1] == self.mean.shape[0] == self.std.shape[0]:
            pred_np = pred_pose[0].detach().cpu().numpy().astype(np.float32)
            pred_np = (pred_np * self.std) + self.mean
            pred_pose = torch.from_numpy(pred_np).unsqueeze(0).to(self.device)

        joint_xyz = recover_from_ric(pred_pose, 22)[0].detach().cpu().numpy()  # [T,22,3]

        r_rot_quat, _ = recover_root_rot_pos(pred_pose)
        r_rot_quat = r_rot_quat[0].detach().cpu().numpy()  # [T,4]

        # Fix: Root joint (0) Y from recover_from_ric is on an unreliable
        # scale (often above the head).  Place it midway between the two
        # hip joints (1 & 2) which have correct absolute Y values.
        joint_xyz[:, 0, 1] = (joint_xyz[:, 1, 1] + joint_xyz[:, 2, 1]) / 2.0

        # ── Separate into world trajectory + root-relative body pose ──
        root_traj = joint_xyz[:, 0, :].copy()             # [T, 3]
        local_joints = joint_xyz - joint_xyz[:, 0:1, :]   # [T, 22, 3]

        # Ground the trajectory: shift Y so first-frame feet sit at Y=0.
        # world_foot_y = root_traj_y + local_foot_y → want ≈ 0
        min_local_foot_y = min(float(local_joints[0, 10, 1]),
                               float(local_joints[0, 11, 1]))
        root_traj[:, 1] -= (root_traj[0, 1] + min_local_foot_y)

        # Centre XZ at origin (frame 0 starts at 0).
        root_traj[:, 0] -= root_traj[0, 0]
        root_traj[:, 2] -= root_traj[0, 2]

        root_traj = root_traj.astype(np.float32)
        local_joints = local_joints.astype(np.float32)

        yaw_rad = 2.0 * np.arctan2(r_rot_quat[:, 2], r_rot_quat[:, 0])
        yaw_deg = np.degrees(yaw_rad)

        target_frames = max(2, int(max(0.1, duration_seconds) * max(1, fps)))
        pos_rs = self._resample(root_traj, target_frames)
        yaw_rs = self._resample(yaw_deg.astype(np.float32), target_frames)

        joints_local_rs = np.zeros((target_frames, 22, 3), dtype=np.float32)
        for j in range(22):
            joints_local_rs[:, j, :] = self._resample(local_joints[:, j, :], target_frames)

        frames = []
        for i in range(target_frames):
            joints_payload = []
            for j in range(22):
                joints_payload.append(
                    {
                        "x": float(joints_local_rs[i, j, 0]),
                        "y": float(joints_local_rs[i, j, 1]),
                        "z": float(joints_local_rs[i, j, 2]),
                    }
                )

            frames.append(
                {
                    "position": {
                        "x": float(pos_rs[i, 0]),
                        "y": float(pos_rs[i, 1]),
                        "z": float(pos_rs[i, 2]),
                    },
                    "rotationEuler": {
                        "x": 0.0,
                        "y": float(yaw_rs[i]),
                        "z": 0.0,
                    },
                    "joints": joints_payload,
                }
            )

        motion_json = {"fps": fps, "frames": frames}
        payload = json.dumps(motion_json).encode("utf-8")

        meta = {
            "model": "T2M-GPT",
            "prompt": prompt,
            "fps": fps,
            "duration_seconds": duration_seconds,
            "seed": seed,
            "format": "JSON",
            "mode": "real",
            "denormalized": self.mean is not None and self.std is not None,
        }

        return payload, "t2mgpt_generated.json", json.dumps(meta)
    # ─────────────────────────────────────────────────────────────
    # BVH generation (truly lossless — uses model's own rotations)
    # ─────────────────────────────────────────────────────────────

    @torch.no_grad()
    def generate_bvh(self, prompt: str, fps: int, duration_seconds: float, seed: int):
        """Generate a BVH file using the model's OWN per-joint local rotations.

        The T2M-GPT 263-dim feature vector already contains:
          - [0]       root Y angular velocity
          - [1:3]     root XZ linear velocity (local frame)
          - [3]       root Y height
          - [4:67]    21 joint root-relative positions
          - [67:193]  21 joint cont6d local rotations  ← the real rotations
          - [193:259] 22 joint velocities
          - [259:263] foot contacts

        Previous versions used inverse_kinematics_np (direction-based IK via
        qbetween_np) which only recovered bone *direction* and lost all
        twist/roll.  This version extracts the cont6d rotations directly,
        preserving shoulder twist, forearm pronation, head yaw, etc.
        """
        self._load()

        import clip
        from bvh_writer import write_bvh
        from utils.motion_process import recover_root_rot_pos, recover_from_ric
        from utils.paramUtil import t2m_kinematic_chain, t2m_raw_offsets
        from utils.skeleton import Skeleton
        from utils.quaternion import cont6d_to_matrix_np

        if seed:
            torch.manual_seed(seed)
            np.random.seed(seed)

        # ── Inference ──
        text = clip.tokenize([prompt], truncate=True).to(self.device)
        feat_clip_text = self.clip_model.encode_text(text).float()
        token_ids = self.trans_encoder.sample(feat_clip_text, if_categorial=True)
        pred_pose = self.vq_model.forward_decoder(token_ids)  # [1, T, 263]

        # Denormalize.
        if (self.mean is not None and self.std is not None
                and pred_pose.shape[-1] == self.mean.shape[0] == self.std.shape[0]):
            pred_np = pred_pose[0].detach().cpu().numpy().astype(np.float32)
            pred_np = (pred_np * self.std) + self.mean
            pred_pose = torch.from_numpy(pred_np).unsqueeze(0).to(self.device)

        T = pred_pose.shape[1]

        # ── 1. Recover root rotation & position from angular/linear velocity ──
        r_rot_quat, r_pos = recover_root_rot_pos(pred_pose.float())
        r_rot_quat = r_rot_quat[0].cpu().numpy()  # (T, 4) — (w, x, y, z)
        r_pos = r_pos[0].cpu().numpy()             # (T, 3) — world root pos

        # ── 2. Extract cont6d local rotations for joints 1-21 ──
        pred_np = pred_pose[0].cpu().numpy()  # (T, 263)
        cont6d_21 = pred_np[:, 67:193].reshape(T, 21, 6)  # (T, 21, 6)

        # ── 3. Convert root quaternion → rotation matrix → cont6d ──
        root_rotmats = self._quat_to_rotmat_np(r_rot_quat)  # (T, 3, 3)
        root_cont6d = np.concatenate([
            root_rotmats[:, :, 0],   # first column  (T, 3)
            root_rotmats[:, :, 1],   # second column (T, 3)
        ], axis=-1)  # (T, 6)

        # ── 4. Combine into (T, 22, 6) and convert to rotation matrices ──
        cont6d_all = np.concatenate([
            root_cont6d[:, np.newaxis, :],  # (T, 1, 6) — root
            cont6d_21,                       # (T, 21, 6) — joints 1-21
        ], axis=1)  # (T, 22, 6)

        local_rotmats = cont6d_to_matrix_np(cont6d_all)  # (T, 22, 3, 3)

        # ── 5. Build skeleton to get bone-length-scaled offsets ──
        joint_xyz = recover_from_ric(pred_pose.float(), 22)[0].detach().cpu().numpy()

        skeleton = Skeleton(
            torch.from_numpy(t2m_raw_offsets).float(),
            t2m_kinematic_chain,
            "cpu",
        )
        skeleton.get_offsets_joints(torch.from_numpy(joint_xyz[0]).float())
        offsets = skeleton._offset.numpy().copy()  # (22, 3)
        offsets[0] = [0.0, 0.0, 0.0]  # root offset in BVH = origin

        # ── 6. Root position — raw model output, no post-processing ──
        root_pos = r_pos.copy()  # (T, 3)

        # ── 7. Write BVH at model's native frame rate (20 fps) ──
        native_fps = 20.0
        bvh_text = write_bvh(
            root_positions=root_pos.astype(np.float32),
            local_rotmats=local_rotmats,
            offsets=offsets,
            fps=native_fps,
        )

        payload = bvh_text.encode("utf-8")
        meta = {
            "model": "T2M-GPT",
            "prompt": prompt,
            "fps": fps,
            "duration_seconds": duration_seconds,
            "seed": seed,
            "format": "BVH",
            "mode": "real",
            "denormalized": self.mean is not None and self.std is not None,
        }
        return payload, "t2mgpt_generated.bvh", json.dumps(meta)

    @staticmethod
    def _quat_to_rotmat_np(q: np.ndarray) -> np.ndarray:
        """Quaternion (w,x,y,z) → 3×3 rotation matrix, batched.

        Parameters
        ----------
        q : (..., 4)

        Returns
        -------
        R : (..., 3, 3)
        """
        q = q / (np.linalg.norm(q, axis=-1, keepdims=True) + 1e-8)
        w, x, y, z = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
        R = np.empty(q.shape[:-1] + (3, 3), dtype=np.float64)
        R[..., 0, 0] = 1 - 2*(y*y + z*z)
        R[..., 0, 1] = 2*(x*y - z*w)
        R[..., 0, 2] = 2*(x*z + y*w)
        R[..., 1, 0] = 2*(x*y + z*w)
        R[..., 1, 1] = 1 - 2*(x*x + z*z)
        R[..., 1, 2] = 2*(y*z - x*w)
        R[..., 2, 0] = 2*(x*z - y*w)
        R[..., 2, 1] = 2*(y*z + x*w)
        R[..., 2, 2] = 1 - 2*(x*x + y*y)
        return R