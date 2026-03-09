import os
import grpc

import sys
sys.path.append(os.path.join(os.path.dirname(__file__), "..", "server"))

import motion_pb2
import motion_pb2_grpc

def main():
    channel = grpc.insecure_channel("127.0.0.1:50051")
    stub = motion_pb2_grpc.MotionServiceStub(channel)

    pong = stub.Ping(motion_pb2.Empty())
    print("Ping:", pong)

    reply = stub.GetDummyBVH(motion_pb2.Empty())
    print("Got:", reply.filename, "bytes:", len(reply.data))

    out_dir = os.path.join(os.path.dirname(__file__), "..", "outputs")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, reply.filename or "dummy.bvh")
    with open(out_path, "wb") as f:
        f.write(reply.data)

    print("Saved to:", out_path)

if __name__ == "__main__":
    main()
