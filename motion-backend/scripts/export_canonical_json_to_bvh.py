import json
import os
import sys
from pathlib import Path

import numpy as np

SCRIPT_DIR = Path(__file__).resolve().parent
SERVER_DIR = SCRIPT_DIR.parent / "server"
if str(SERVER_DIR) not in sys.path:
    sys.path.insert(0, str(SERVER_DIR))

from bvh_writer import write_bvh  # type: ignore[reportMissingImports]


def quat_xyzw_to_rotmat(quat_xyzw: np.ndarray) -> np.ndarray:
    q = quat_xyzw.astype(np.float64)
    norm = np.linalg.norm(q, axis=-1, keepdims=True)
    norm = np.where(norm == 0.0, 1.0, norm)
    q = q / norm

    x = q[..., 0]
    y = q[..., 1]
    z = q[..., 2]
    w = q[..., 3]

    xx = x * x
    yy = y * y
    zz = z * z
    ww = w * w
    xy = x * y
    xz = x * z
    yz = y * z
    xw = x * w
    yw = y * w
    zw = z * w

    rot = np.empty(q.shape[:-1] + (3, 3), dtype=np.float64)
    rot[..., 0, 0] = ww + xx - yy - zz
    rot[..., 0, 1] = 2.0 * (xy - zw)
    rot[..., 0, 2] = 2.0 * (xz + yw)
    rot[..., 1, 0] = 2.0 * (xy + zw)
    rot[..., 1, 1] = ww - xx + yy - zz
    rot[..., 1, 2] = 2.0 * (yz - xw)
    rot[..., 2, 0] = 2.0 * (xz - yw)
    rot[..., 2, 1] = 2.0 * (yz + xw)
    rot[..., 2, 2] = ww - xx - yy + zz
    return rot.astype(np.float32)


def load_canonical_json(json_path: Path):
    motion = json.loads(json_path.read_text(encoding="utf-8"))
    frames = motion.get("frames") or []
    if not frames:
        raise ValueError("No frames found in canonical JSON")

    fps = float(motion.get("fps") or 20)

    root_positions = np.array(
        [
            [
                float(frame["position"]["x"]),
                float(frame["position"]["y"]),
                float(frame["position"]["z"]),
            ]
            for frame in frames
        ],
        dtype=np.float32,
    )

    local_quats = np.array(
        [
            [
                [
                    float(joint["x"]),
                    float(joint["y"]),
                    float(joint["z"]),
                    float(joint["w"]),
                ]
                for joint in frame["localRotations"]
            ]
            for frame in frames
        ],
        dtype=np.float32,
    )

    rest_offsets = np.array(
        [
            [
                float(offset["x"]),
                float(offset["y"]),
                float(offset["z"]),
            ]
            for offset in motion["restOffsets"]
        ],
        dtype=np.float32,
    )

    return fps, root_positions, quat_xyzw_to_rotmat(local_quats), rest_offsets


def export_bvh(json_path: Path, out_path: Path):
    fps, root_positions, local_rotmats, rest_offsets = load_canonical_json(json_path)
    bvh_text = write_bvh(
        root_positions=root_positions,
        local_rotmats=local_rotmats,
        offsets=rest_offsets,
        fps=fps,
    )
    out_path.write_text(bvh_text, encoding="utf-8", newline="\n")
    return fps, len(root_positions)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("Usage: python export_canonical_json_to_bvh.py <input.json> [output.bvh]")

    input_path = Path(sys.argv[1]).resolve()
    output_path = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else input_path.with_suffix(".bvh")

    fps, frame_count = export_bvh(input_path, output_path)
    print(f"[OK] Wrote BVH: {output_path}")
    print(f"     Frames: {frame_count}")
    print(f"     FPS: {fps:g}")
