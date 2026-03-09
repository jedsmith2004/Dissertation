"""
BVH file writer for the 22-joint SMPL/HumanML3D skeleton.

Converts per-frame local joint rotations (as 3×3 matrices or Euler ZYX)
and root positions into a standard BVH text file that Unity can import
directly via its Humanoid retargeting pipeline.
"""

from __future__ import annotations

import numpy as np
from typing import List, Tuple

# ──────────────────────────────────────────────────────────────────────
# Skeleton definition
# ──────────────────────────────────────────────────────────────────────
# Joint order matches T2M-GPT / HumanML3D exactly.
JOINT_NAMES: List[str] = [
    "Pelvis",       # 0
    "L_Hip",        # 1
    "R_Hip",        # 2
    "Spine1",       # 3
    "L_Knee",       # 4
    "R_Knee",       # 5
    "Spine2",       # 6
    "L_Ankle",      # 7
    "R_Ankle",      # 8
    "Spine3",       # 9
    "L_Foot",       # 10
    "R_Foot",       # 11
    "Neck",         # 12
    "L_Collar",     # 13
    "R_Collar",     # 14
    "Head",         # 15
    "L_Shoulder",   # 16
    "R_Shoulder",   # 17
    "L_Elbow",      # 18
    "R_Elbow",      # 19
    "L_Wrist",      # 20
    "R_Wrist",      # 21
]

# Parent index for each joint (-1 = root).
PARENTS: List[int] = [
    -1,  # 0  Pelvis
     0,  # 1  L_Hip
     0,  # 2  R_Hip
     0,  # 3  Spine1
     1,  # 4  L_Knee
     2,  # 5  R_Knee
     3,  # 6  Spine2
     4,  # 7  L_Ankle
     5,  # 8  R_Ankle
     6,  # 9  Spine3
     7,  # 10 L_Foot
     8,  # 11 R_Foot
     9,  # 12 Neck
     9,  # 13 L_Collar
     9,  # 14 R_Collar
    12,  # 15 Head
    13,  # 16 L_Shoulder
    14,  # 17 R_Shoulder
    16,  # 18 L_Elbow
    17,  # 19 R_Elbow
    18,  # 20 L_Wrist
    19,  # 21 R_Wrist
]

# Children map (built from PARENTS).
CHILDREN: List[List[int]] = [[] for _ in range(22)]
for _i, _p in enumerate(PARENTS):
    if _p >= 0:
        CHILDREN[_p].append(_i)


# ──────────────────────────────────────────────────────────────────────
# Rotation math helpers
# ──────────────────────────────────────────────────────────────────────

def rotmat_to_euler_zyx(R: np.ndarray) -> np.ndarray:
    """Convert a batch of 3×3 rotation matrices to ZYX-intrinsic Euler angles (degrees).

    BVH convention:  Zrotation  Yrotation  Xrotation
    (intrinsic ZYX = extrinsic XYZ)

    Parameters
    ----------
    R : np.ndarray, shape (..., 3, 3)

    Returns
    -------
    np.ndarray, shape (..., 3) — angles in degrees, order [Z, Y, X].
    """
    # Clamp to avoid NaN from asin.
    sy = np.clip(R[..., 0, 2], -1.0, 1.0)
    y = np.arcsin(sy)

    # Check for gimbal lock (|cos(y)| ≈ 0).
    cy = np.cos(y)
    gimbal = np.abs(cy) < 1e-6

    # Normal case.
    z = np.where(gimbal, np.float64(0.0), np.arctan2(-R[..., 0, 1], R[..., 0, 0]))
    x = np.where(gimbal, np.arctan2(-R[..., 1, 2], R[..., 1, 1]), np.arctan2(-R[..., 1, 2], R[..., 2, 2]))

    return np.degrees(np.stack([z, y, x], axis=-1))


# ──────────────────────────────────────────────────────────────────────
# Compute rest-pose offsets from the first frame's world positions
# ──────────────────────────────────────────────────────────────────────

def compute_offsets(world_positions_frame0: np.ndarray) -> np.ndarray:
    """Compute BVH OFFSET for each joint from a single frame of world positions.

    Parameters
    ----------
    world_positions_frame0 : (22, 3)

    Returns
    -------
    offsets : (22, 3) — each joint's offset from its parent (root offset = root pos).
    """
    offsets = np.zeros((22, 3), dtype=np.float64)
    offsets[0] = world_positions_frame0[0]  # root offset = its initial position
    for j in range(1, 22):
        offsets[j] = world_positions_frame0[j] - world_positions_frame0[PARENTS[j]]
    return offsets


# ──────────────────────────────────────────────────────────────────────
# Build the HIERARCHY string
# ──────────────────────────────────────────────────────────────────────

def _hierarchy_recursive(joint_idx: int, offsets: np.ndarray, depth: int) -> str:
    indent = "\t" * depth
    name = JOINT_NAMES[joint_idx]
    ox, oy, oz = offsets[joint_idx]
    children = CHILDREN[joint_idx]

    lines: List[str] = []

    if joint_idx == 0:
        lines.append(f"HIERARCHY")
        lines.append(f"ROOT {name}")
    else:
        lines.append(f"{indent}JOINT {name}")

    lines.append(f"{indent}{{")

    lines.append(f"{indent}\tOFFSET {ox:.6f} {oy:.6f} {oz:.6f}")

    if joint_idx == 0:
        lines.append(f"{indent}\tCHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation")
    else:
        lines.append(f"{indent}\tCHANNELS 3 Zrotation Yrotation Xrotation")

    if len(children) > 0:
        for child in children:
            lines.append(_hierarchy_recursive(child, offsets, depth + 1))
    else:
        # Leaf joint — add an End Site.
        lines.append(f"{indent}\tEnd Site")
        lines.append(f"{indent}\t{{")
        lines.append(f"{indent}\t\tOFFSET 0.000000 0.000000 0.000000")
        lines.append(f"{indent}\t}}")

    lines.append(f"{indent}}}")
    return "\n".join(lines)


def build_hierarchy(offsets: np.ndarray) -> str:
    return _hierarchy_recursive(0, offsets, 0)


# ──────────────────────────────────────────────────────────────────────
# Build the MOTION block
# ──────────────────────────────────────────────────────────────────────

def _dfs_order() -> List[int]:
    """Return joint indices in the DFS order that matches the HIERARCHY."""
    result: List[int] = []

    def _visit(idx: int):
        result.append(idx)
        for c in CHILDREN[idx]:
            _visit(c)

    _visit(0)
    return result


_DFS_ORDER = _dfs_order()


def build_motion(
    root_positions: np.ndarray,
    local_eulers_zyx: np.ndarray,
    fps: float,
) -> str:
    """Build the MOTION section of a BVH file.

    Parameters
    ----------
    root_positions : (T, 3) — root world position per frame.
    local_eulers_zyx : (T, 22, 3) — local Euler angles in degrees (Z, Y, X order) per joint per frame.
    fps : float

    Returns
    -------
    str — the MOTION block.
    """
    n_frames = len(root_positions)
    frame_time = 1.0 / max(1.0, fps)

    lines: List[str] = []
    lines.append("MOTION")
    lines.append(f"Frames: {n_frames}")
    lines.append(f"Frame Time: {frame_time:.8f}")

    for i in range(n_frames):
        values: List[str] = []
        for j in _DFS_ORDER:
            if j == 0:
                # Root: 6 channels → Xpos Ypos Zpos Zrot Yrot Xrot
                px, py, pz = root_positions[i]
                values.append(f"{px:.6f}")
                values.append(f"{py:.6f}")
                values.append(f"{pz:.6f}")
            # Every joint (including root): 3 rotation channels
            zr, yr, xr = local_eulers_zyx[i, j]
            values.append(f"{zr:.6f}")
            values.append(f"{yr:.6f}")
            values.append(f"{xr:.6f}")

        lines.append(" ".join(values))

    return "\n".join(lines)


# ──────────────────────────────────────────────────────────────────────
# Full BVH assembly
# ──────────────────────────────────────────────────────────────────────

def write_bvh(
    root_positions: np.ndarray,
    local_rotmats: np.ndarray,
    offsets: np.ndarray,
    fps: float,
) -> str:
    """Assemble a complete BVH string.

    Parameters
    ----------
    root_positions : (T, 3)
    local_rotmats : (T, 22, 3, 3) — per-joint local rotation matrices.
    offsets : (22, 3) — precomputed BVH OFFSET per joint (T-pose direction × bone length).
    fps : float

    Returns
    -------
    str — complete BVH file content.
    """
    hierarchy = build_hierarchy(offsets)
    eulers = rotmat_to_euler_zyx(local_rotmats)  # (T, 22, 3)
    motion = build_motion(root_positions, eulers, fps)
    return hierarchy + "\n" + motion + "\n"
