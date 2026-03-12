import os
from concurrent import futures

import grpc
import torch
from dotenv import load_dotenv

import motion_pb2
import motion_pb2_grpc
from t2mgpt_exact_bvh import T2MGPTExactBvhGenerator

load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

_GENERATOR = T2MGPTExactBvhGenerator()


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
        if request.model != motion_pb2.T2M_GPT:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only T2M_GPT is supported by the active MotionGen pipeline.")
            return motion_pb2.GenerateReply()

        if request.format not in (motion_pb2.BVH, 0):
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("Only BVH output is supported by the active MotionGen pipeline.")
            return motion_pb2.GenerateReply()

        try:
            data, filename, meta = _GENERATOR.generate_bvh(
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


def serve(port: int = 50051):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    motion_pb2_grpc.add_MotionServiceServicer_to_server(MotionService(), server)
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    print(f"[MotionGen] gRPC server listening on 0.0.0.0:{port}")
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
