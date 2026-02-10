import grpc
import sys
import os

sys.path.append(os.path.join(os.path.dirname(__file__), 'server'))

import server.motion_pb2 as motion_pb2
import server.motion_pb2_grpc as motion_pb2_grpc

def run():
    with grpc.insecure_channel('localhost:50051') as channel:
        stub = motion_pb2_grpc.MotionServiceStub(channel)
        response = stub.Ping(motion_pb2.Empty())
        print(f"Backend Version: {response.version}")
        print(f"CUDA Available: {response.cuda_available}")
        print(f"Device Name: {response.device_name}")

if __name__ == '__main__':
    run()