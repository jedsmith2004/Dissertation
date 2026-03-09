using System;
using System.Threading.Tasks;
using Grpc.Core;
using Motion;

public class MotionClient : IDisposable
{
    private readonly Channel _channel;
    private readonly MotionService.MotionServiceClient _client;

    public MotionClient(string host = "127.0.0.1", int port = 50051)
    {
        _channel = new Channel(host, port, ChannelCredentials.Insecure);
        _client = new MotionService.MotionServiceClient(_channel);
    }

    public PingResponse Ping() => _client.Ping(new Empty());
    public async Task<PingResponse> PingAsync() => await _client.PingAsync(new Empty());

    public MotionReply GetDummyBVH() => _client.GetDummyBVH(new Empty());
    public async Task<MotionReply> GetDummyBVHAsync() => await _client.GetDummyBVHAsync(new Empty());

    public async Task<GenerateReply> GenerateAsync(string prompt, int fps, float durationSeconds, int seed, MotionFormat format)
    {
        var req = new GenerateRequest
        {
            Prompt = prompt,
            Fps = fps,
            DurationSeconds = durationSeconds,
            Seed = seed,
            Format = format,
            Model = MotionModel.T2MGpt
        };

        return await _client.GenerateAsync(req);
    }

    public void Dispose()
    {
        _channel?.ShutdownAsync().Wait();
    }
}