import json
import os
import random
from concurrent import futures
from typing import List, Tuple

import grpc
import torch
from dotenv import load_dotenv

import motion_pb2
import motion_pb2_grpc
from momask_bvh import MoMaskBvhGenerator
from t2mgpt_exact_bvh import T2MGPTExactBvhGenerator

load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

_GENERATORS = {
    motion_pb2.T2M_GPT: T2MGPTExactBvhGenerator(),
    motion_pb2.MOMASK: MoMaskBvhGenerator(),
}


class MotionService(motion_pb2_grpc.MotionServiceServicer):
    def Ping(self, request, context):
        cuda_available = torch.cuda.is_available()
        device_name = torch.cuda.get_device_name(0) if cuda_available else "CPU"
        return motion_pb2.PingResponse(
            version="0.1.0",
            cuda_available=cuda_available,
            device_name=device_name,
        )

    def Generate(self, request, context):
        generator = _resolve_generator(request.model)
        if generator is None:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Unsupported motion model: {_model_name(request.model)}.")
            return motion_pb2.GenerateReply()

        if request.format not in (motion_pb2.BVH, 0):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only BVH output is supported by the active MotionGen pipeline.")
            return motion_pb2.GenerateReply()

        try:
            data, filename, meta = generator.generate_bvh(
                prompt=request.prompt,
                fps=request.fps or 20,
                duration_seconds=request.duration_seconds or 2.0,
                seed=request.seed,
            )
            return motion_pb2.GenerateReply(
                format=motion_pb2.BVH,
                data=data,
                meta=meta,
                filename=filename,
            )
        except (RuntimeError, ModuleNotFoundError, ImportError) as ex:
            context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            context.set_details(str(ex))
            return motion_pb2.GenerateReply()
        except Exception as ex:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(ex))
            return motion_pb2.GenerateReply()

    def GenerateBatch(self, request, context):
        generator = _resolve_generator(request.model)
        if generator is None:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Unsupported motion model: {_model_name(request.model)}.")
            return motion_pb2.BatchGenerateReply()

        if request.format not in (motion_pb2.BVH, 0):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only BVH output is supported by the active MotionGen pipeline.")
            return motion_pb2.BatchGenerateReply()

        count = max(1, int(request.count or 1))
        use_random_seed = request.use_random_seed
        base_seed = int(request.seed)
        rng = random.SystemRandom()
        items = []

        try:
            for index in range(count):
                resolved_seed = rng.randint(0, 2_147_483_647) if use_random_seed else base_seed + index
                data, filename, meta = generator.generate_bvh(
                    prompt=request.prompt,
                    fps=request.fps or 20,
                    duration_seconds=request.duration_seconds or 2.0,
                    seed=resolved_seed,
                )

                meta_dict = _try_parse_meta(meta)
                meta_dict["batch_index"] = index
                meta_dict["batch_count"] = count
                meta_dict["resolved_seed"] = resolved_seed
                meta_dict["seed_mode"] = "random" if use_random_seed else "incrementing"

                items.append(
                    motion_pb2.GenerateReply(
                        format=motion_pb2.BVH,
                        data=data,
                        meta=json.dumps(meta_dict),
                        filename=_build_batch_filename(filename, index, resolved_seed),
                    )
                )

            return motion_pb2.BatchGenerateReply(items=items)
        except (RuntimeError, ModuleNotFoundError, ImportError) as ex:
            context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            context.set_details(str(ex))
            return motion_pb2.BatchGenerateReply()
        except Exception as ex:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(ex))
            return motion_pb2.BatchGenerateReply()

    def Edit(self, request, context):
        generator = _resolve_generator(request.model)
        if generator is None:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Unsupported motion model: {_model_name(request.model)}.")
            return motion_pb2.EditReply()

        if not hasattr(generator, "edit_bvh"):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Model {_model_name(request.model)} does not support text-guided editing.")
            return motion_pb2.EditReply()

        if request.format not in (motion_pb2.BVH, 0):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only BVH output is supported by the active MotionGen pipeline.")
            return motion_pb2.EditReply()

        try:
            source_joints, source_fps = _parse_joint_sequence(request.source_motion)
            edit_ranges = _normalize_edit_ranges(request.edit_ranges)
            data, filename, meta = generator.edit_bvh(
                prompt=request.prompt,
                fps=request.fps or source_fps or 20,
                seed=int(request.seed),
                source_joints=source_joints,
                source_fps=source_fps,
                edit_ranges=edit_ranges,
            )
            return motion_pb2.EditReply(
                item=motion_pb2.GenerateReply(
                    format=motion_pb2.BVH,
                    data=data,
                    meta=meta,
                    filename=filename,
                )
            )
        except ValueError as ex:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(str(ex))
            return motion_pb2.EditReply()
        except (RuntimeError, ModuleNotFoundError, ImportError) as ex:
            context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            context.set_details(str(ex))
            return motion_pb2.EditReply()
        except Exception as ex:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(ex))
            return motion_pb2.EditReply()

    def EditBatch(self, request, context):
        generator = _resolve_generator(request.model)
        if generator is None:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Unsupported motion model: {_model_name(request.model)}.")
            return motion_pb2.BatchEditReply()

        if not hasattr(generator, "edit_bvh"):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(f"Model {_model_name(request.model)} does not support text-guided editing.")
            return motion_pb2.BatchEditReply()

        if request.format not in (motion_pb2.BVH, 0):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only BVH output is supported by the active MotionGen pipeline.")
            return motion_pb2.BatchEditReply()

        count = max(1, int(request.count or 1))
        use_random_seed = request.use_random_seed
        base_seed = int(request.seed)
        rng = random.SystemRandom()
        items = []

        try:
            source_joints, source_fps = _parse_joint_sequence(request.source_motion)
            edit_ranges = _normalize_edit_ranges(request.edit_ranges)

            for index in range(count):
                resolved_seed = rng.randint(0, 2_147_483_647) if use_random_seed else base_seed + index
                data, filename, meta = generator.edit_bvh(
                    prompt=request.prompt,
                    fps=request.fps or source_fps or 20,
                    seed=resolved_seed,
                    source_joints=source_joints,
                    source_fps=source_fps,
                    edit_ranges=edit_ranges,
                )

                meta_dict = _try_parse_meta(meta)
                meta_dict["batch_index"] = index
                meta_dict["batch_count"] = count
                meta_dict["resolved_seed"] = resolved_seed
                meta_dict["seed_mode"] = "random" if use_random_seed else "incrementing"

                items.append(
                    motion_pb2.GenerateReply(
                        format=motion_pb2.BVH,
                        data=data,
                        meta=json.dumps(meta_dict),
                        filename=_build_batch_filename(filename, index, resolved_seed),
                    )
                )

            return motion_pb2.BatchEditReply(items=items)
        except ValueError as ex:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(str(ex))
            return motion_pb2.BatchEditReply()
        except (RuntimeError, ModuleNotFoundError, ImportError) as ex:
            context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            context.set_details(str(ex))
            return motion_pb2.BatchEditReply()
        except Exception as ex:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(ex))
            return motion_pb2.BatchEditReply()


def _build_batch_filename(filename: str, index: int, seed: int) -> str:
    stem, ext = os.path.splitext(filename or "generated.bvh")
    safe_ext = ext or ".bvh"
    safe_stem = stem or "generated"
    return f"{safe_stem}_v{index + 1:03d}_s{seed}{safe_ext}"


def _try_parse_meta(meta: str) -> dict:
    if not meta:
        return {}

    try:
        parsed = json.loads(meta)
    except json.JSONDecodeError:
        return {"raw_meta": meta}

    return parsed if isinstance(parsed, dict) else {"raw_meta": meta}


def _resolve_generator(model: int):
    if model in _GENERATORS:
        return _GENERATORS[model]

    if model == 0:
        return _GENERATORS[motion_pb2.T2M_GPT]

    return None


def _model_name(model: int) -> str:
    try:
        return motion_pb2.MotionModel.Name(model)
    except ValueError:
        return str(model)


def _parse_joint_sequence(source_motion: motion_pb2.MotionJointSequence):
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


def _normalize_edit_ranges(edit_ranges: List[motion_pb2.EditRange]) -> List[Tuple[float, float]]:
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


def serve(port: int = 50051):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    motion_pb2_grpc.add_MotionServiceServicer_to_server(MotionService(), server)
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    print(f"[MotionGen] gRPC server listening on 0.0.0.0:{port}")
    server.wait_for_termination()


if __name__ == "__main__":
    port_text = os.getenv("MOTIONGEN_BACKEND_PORT", "50051")
    try:
        serve(int(port_text))
    except ValueError:
        serve(50051)
