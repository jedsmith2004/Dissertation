import json
import os
from typing import List, Tuple, TypeVar

import grpc

import motion_pb2

T = TypeVar("T")


def build_batch_filename(filename: str, index: int, seed: int) -> str:
    stem, ext = os.path.splitext(filename or "generated.bvh")
    safe_ext = ext or ".bvh"
    safe_stem = stem or "generated"
    return f"{safe_stem}_v{index + 1:03d}_s{seed}{safe_ext}"


def try_parse_meta(meta: str) -> dict:
    if not meta:
        return {}

    try:
        parsed = json.loads(meta)
    except json.JSONDecodeError:
        return {"raw_meta": meta}

    return parsed if isinstance(parsed, dict) else {"raw_meta": meta}


def with_batch_meta(meta: str, index: int, count: int, resolved_seed: int, use_random_seed: bool) -> str:
    meta_dict = try_parse_meta(meta)
    meta_dict["batch_index"] = index
    meta_dict["batch_count"] = count
    meta_dict["resolved_seed"] = resolved_seed
    meta_dict["seed_mode"] = "random" if use_random_seed else "incrementing"
    return json.dumps(meta_dict)


def resolve_generator(model: int, generators: dict):
    if model in generators:
        return generators[model]
    if model == 0:
        return generators.get(motion_pb2.T2M_GPT)
    return None


def model_name(model: int) -> str:
    try:
        return motion_pb2.MotionModel.Name(model)
    except ValueError:
        return str(model)


def parse_joint_sequence(source_motion: motion_pb2.MotionJointSequence):
    import numpy as np

    if source_motion is None:
        raise ValueError("source_motion is required for editing.")

    frame_count = int(source_motion.frame_count)
    joint_count = int(source_motion.joint_count)
    source_fps = max(1, int(source_motion.fps or 20))

    if frame_count < 4:
        raise ValueError("source_motion.frame_count must be at least 4.")
    if joint_count != 22:
        raise ValueError("source_motion.joint_count must be 22 for MoMask editing.")

    expected_values = frame_count * joint_count * 3
    values = list(source_motion.joint_positions)
    if len(values) != expected_values:
        raise ValueError(
            f"source_motion.joint_positions length mismatch: expected {expected_values}, got {len(values)}."
        )

    joints = np.asarray(values, dtype=np.float32).reshape(frame_count, joint_count, 3)
    return joints, source_fps


def normalize_edit_ranges(edit_ranges: List[motion_pb2.EditRange]) -> List[Tuple[float, float]]:
    if not edit_ranges:
        raise ValueError("At least one edit range is required.")

    normalized: List[Tuple[float, float]] = []
    for entry in edit_ranges:
        start = max(0.0, float(entry.start_seconds))
        end = max(start, float(entry.end_seconds))
        if end <= start:
            continue
        normalized.append((start, end))

    if not normalized:
        raise ValueError("Edit ranges must contain at least one non-empty interval.")

    return normalized


def resolve_seed(index: int, base_seed: int, use_random_seed: bool, rng) -> int:
    if use_random_seed:
        return rng.randint(0, 2_147_483_647)
    return base_seed + index


def reply_error(context, code: grpc.StatusCode, details: str, empty_reply: T) -> T:
    context.set_code(code)
    context.set_details(details)
    return empty_reply


def validate_generator(
    *,
    model: int,
    output_format: int,
    generators: dict,
    context,
    empty_reply: T,
    require_edit: bool,
):
    generator = resolve_generator(model, generators)
    if generator is None:
        return None, reply_error(
            context,
            grpc.StatusCode.INVALID_ARGUMENT,
            f"Unsupported motion model: {model_name(model)}.",
            empty_reply,
        )

    if require_edit and not hasattr(generator, "edit_bvh"):
        return None, reply_error(
            context,
            grpc.StatusCode.INVALID_ARGUMENT,
            f"Model {model_name(model)} does not support text-guided editing.",
            empty_reply,
        )

    if output_format not in (motion_pb2.BVH, 0):
        return None, reply_error(
            context,
            grpc.StatusCode.INVALID_ARGUMENT,
            "Only BVH output is supported by the active MotionGen pipeline.",
            empty_reply,
        )

    return generator, None


def wrap_generation_exception(context, ex: Exception, empty_reply: T) -> T:
    if isinstance(ex, ValueError):
        return reply_error(context, grpc.StatusCode.INVALID_ARGUMENT, str(ex), empty_reply)
    if isinstance(ex, (RuntimeError, ModuleNotFoundError, ImportError)):
        return reply_error(context, grpc.StatusCode.FAILED_PRECONDITION, str(ex), empty_reply)
    return reply_error(context, grpc.StatusCode.INTERNAL, str(ex), empty_reply)
