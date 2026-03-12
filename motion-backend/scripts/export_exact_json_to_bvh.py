import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np

BACKEND = Path(__file__).resolve().parents[1]
MOMASK_DIR = BACKEND / "models" / "MoMask"
if str(MOMASK_DIR) not in sys.path:
    sys.path.insert(0, str(MOMASK_DIR))

from visualization.joints2bvh import Joint2BVHConvertor  # type: ignore[reportMissingImports]


def load_exact_json(json_path: Path) -> np.ndarray:
    payload = json.loads(json_path.read_text(encoding="utf-8"))
    frames = payload.get("frames") or []
    if not frames:
        raise ValueError("No frames found in exact joint JSON")

    joints = np.array(
        [
            [
                [float(joint["x"]), float(joint["y"]), float(joint["z"])]
                for joint in frame["joints"]
            ]
            for frame in frames
        ],
        dtype=np.float32,
    )
    return joints


def export_bvh(json_path: Path, bvh_path: Path, iterations: int, foot_ik: bool) -> None:
    joints = load_exact_json(json_path)
    bvh_path.parent.mkdir(parents=True, exist_ok=True)

    old_cwd = Path.cwd()
    try:
        os.chdir(MOMASK_DIR)
        converter = Joint2BVHConvertor()
        converter.convert(joints, filename=str(bvh_path), iterations=iterations, foot_ik=foot_ik)
    finally:
        os.chdir(old_cwd)


def main() -> None:
    default_input = BACKEND.parent / "Dissertation Plugin Playground" / "Assets" / "MotionGen" / "Generated" / "t2mgpt_test_generate_exact.json"
    default_output = default_input.with_suffix(".bvh")

    parser = argparse.ArgumentParser(description="Convert exact T2M-GPT joint JSON to Blender-compatible BVH using MoMask IK.")
    parser.add_argument("--input", default=str(default_input))
    parser.add_argument("--output", default=str(default_output))
    parser.add_argument("--iterations", type=int, default=100)
    parser.add_argument("--foot-ik", action="store_true")
    args = parser.parse_args()

    input_path = Path(args.input).resolve()
    output_path = Path(args.output).resolve()

    export_bvh(input_path, output_path, iterations=args.iterations, foot_ik=args.foot_ik)
    print(f"[OK] Wrote BVH: {output_path}")
    print(f"     Source JSON: {input_path}")
    print(f"     Foot IK: {args.foot_ik}")
    print(f"     Iterations: {args.iterations}")


if __name__ == "__main__":
    main()
