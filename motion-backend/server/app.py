import os
import random
from concurrent import futures

import grpc
import torch
from dotenv import load_dotenv

import motion_pb2
import motion_pb2_grpc
from momask_bvh import MoMaskBvhGenerator
from service_utils import (
    build_batch_filename,
    normalize_edit_ranges,
    parse_joint_sequence,
    resolve_seed,
    validate_generator,
    with_batch_meta,
    wrap_generation_exception,
)
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
        generator, error_reply = validate_generator(
            model=request.model,
            output_format=request.format,
            generators=_GENERATORS,
            context=context,
            empty_reply=motion_pb2.GenerateReply(),
            require_edit=False,
        )
        if error_reply is not None:
            return error_reply

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
        except Exception as ex:
            return wrap_generation_exception(context, ex, motion_pb2.GenerateReply())

    def GenerateBatch(self, request, context):
        generator, error_reply = validate_generator(
            model=request.model,
            output_format=request.format,
            generators=_GENERATORS,
            context=context,
            empty_reply=motion_pb2.BatchGenerateReply(),
            require_edit=False,
        )
        if error_reply is not None:
            return error_reply

        count = max(1, int(request.count or 1))
        use_random_seed = request.use_random_seed
        base_seed = int(request.seed)
        rng = random.SystemRandom()
        items = []

        try:
            for index in range(count):
                resolved_seed = resolve_seed(index=index, base_seed=base_seed, use_random_seed=use_random_seed, rng=rng)
                data, filename, meta = generator.generate_bvh(
                    prompt=request.prompt,
                    fps=request.fps or 20,
                    duration_seconds=request.duration_seconds or 2.0,
                    seed=resolved_seed,
                )

                items.append(
                    motion_pb2.GenerateReply(
                        format=motion_pb2.BVH,
                        data=data,
                        meta=with_batch_meta(meta, index, count, resolved_seed, use_random_seed),
                        filename=build_batch_filename(filename, index, resolved_seed),
                    )
                )

            return motion_pb2.BatchGenerateReply(items=items)
        except Exception as ex:
            return wrap_generation_exception(context, ex, motion_pb2.BatchGenerateReply())

    def Edit(self, request, context):
        generator, error_reply = validate_generator(
            model=request.model,
            output_format=request.format,
            generators=_GENERATORS,
            context=context,
            empty_reply=motion_pb2.EditReply(),
            require_edit=True,
        )
        if error_reply is not None:
            return error_reply

        try:
            source_joints, source_fps = parse_joint_sequence(request.source_motion)
            edit_ranges = normalize_edit_ranges(request.edit_ranges)
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
        except Exception as ex:
            return wrap_generation_exception(context, ex, motion_pb2.EditReply())

    def EditBatch(self, request, context):
        generator, error_reply = validate_generator(
            model=request.model,
            output_format=request.format,
            generators=_GENERATORS,
            context=context,
            empty_reply=motion_pb2.BatchEditReply(),
            require_edit=True,
        )
        if error_reply is not None:
            return error_reply

        count = max(1, int(request.count or 1))
        use_random_seed = request.use_random_seed
        base_seed = int(request.seed)
        rng = random.SystemRandom()
        items = []

        try:
            source_joints, source_fps = parse_joint_sequence(request.source_motion)
            edit_ranges = normalize_edit_ranges(request.edit_ranges)

            for index in range(count):
                resolved_seed = resolve_seed(index=index, base_seed=base_seed, use_random_seed=use_random_seed, rng=rng)
                data, filename, meta = generator.edit_bvh(
                    prompt=request.prompt,
                    fps=request.fps or source_fps or 20,
                    seed=resolved_seed,
                    source_joints=source_joints,
                    source_fps=source_fps,
                    edit_ranges=edit_ranges,
                )

                items.append(
                    motion_pb2.GenerateReply(
                        format=motion_pb2.BVH,
                        data=data,
                        meta=with_batch_meta(meta, index, count, resolved_seed, use_random_seed),
                        filename=build_batch_filename(filename, index, resolved_seed),
                    )
                )

            return motion_pb2.BatchEditReply(items=items)
        except Exception as ex:
            return wrap_generation_exception(context, ex, motion_pb2.BatchEditReply())


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
