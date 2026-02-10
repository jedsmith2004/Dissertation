import grpc
from concurrent import futures
import torch
import motion_pb2, motion_pb2_grpc
from dummy_bvh import DUMMY_BVH
    
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
        context.set_code(grpc.StatusCode.UNIMPLEMENTED)
        context.set_details("Generate not implemented yet")
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
