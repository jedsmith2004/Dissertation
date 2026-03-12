import os
import json
import grpc
from concurrent import futures
import torch
from dotenv import load_dotenv

# Load .env from the project root (one level up from server/)
load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

import motion_pb2, motion_pb2_grpc
from dummy_bvh import DUMMY_BVH
from t2mgpt_exact_bvh import T2MGPTExactBvhGenerator


t2mgpt_generator = T2MGPTExactBvhGenerator()
    
class MotionService(motion_pb2_grpc.MotionServiceServicer):
    def Ping(self, request, context):
        cuda_available = torch.cuda.is_available()
        device_name = torch.cuda.get_device_name(0) if cuda_available else "CPU"
        
        print(f"Ping received. CUDA: {cuda_available}, Device: {device_name}")
        
        return motion_pb2.PingResponse(
            version="0.1.0",
            cuda_available=cuda_available,
            device_name=device_name
        )
    
    def GetDummyBVH(self, request, context):
        bvh_text = DUMMY_BVH
        return motion_pb2.MotionReply(
            format=motion_pb2.BVH,
            data=bvh_text.encode("utf-8"),
            meta="fps=30",
            filename="dummy.bvh"
        )
    
    def Generate(self, request, context):
        try:
            if request.model != motion_pb2.T2M_GPT:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("Only T2M-GPT is enabled right now.")
                return motion_pb2.GenerateReply()

            fps = request.fps or 30
            duration_seconds = request.duration_seconds or 2.0
            out_dir = os.path.join(os.getcwd(), "outputs")

            data, filename, meta = t2mgpt_generator.generate_bvh(
                prompt=request.prompt,
                fps=fps,
                duration_seconds=duration_seconds,
                seed=request.seed
            )
            reply_format = motion_pb2.BVH

            return motion_pb2.GenerateReply(
                format=reply_format,
                data=data,
                meta=meta,
                filename=filename
            )
        except Exception as ex:
            if isinstance(ex, (RuntimeError, ModuleNotFoundError, ImportError)):
                context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            else:
                context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(ex))
            return motion_pb2.GenerateReply()

def generate_t2mgpt(prompt: str, fps: int, duration_seconds: float, seed: int, out_dir: str):
        # TODO: replace with real T2M-GPT inference call
        # For now, return dummy BVH to validate end-to-end contract
        os.makedirs(out_dir, exist_ok=True)
        filename = "t2mgpt_generated.bvh"
        path = os.path.join(out_dir, filename)

        bvh_text = DUMMY_BVH
        with open(path, "w", encoding="utf-8") as f:
            f.write(bvh_text)
        
        with open(path, "rb") as f:
            data = f.read()
        
        meta = {
            "model": "T2M-GPT",
            "prompt": prompt,
            "fps": fps,
            "duration_seconds": duration_seconds,
            "seed": seed
        }
        return data, filename, json.dumps(meta)

def generate_t2mgpt_json(prompt: str, fps: int, duration_seconds: float, seed: int, out_dir: str):
    raise RuntimeError("Legacy JSON generation has been removed. The active MotionGen pipeline now always returns exact BVH.")

def serve(port: int = 50051):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))

    motion_pb2_grpc.add_MotionServiceServicer_to_server(MotionService(), server)
    
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    print(f"[MotionGen] gRPC server listening on 0.0.0.0:{port}")
    server.wait_for_termination()

if __name__ == "__main__":
    serve()
