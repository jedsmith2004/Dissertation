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

    public async Task<GenerateReply> GenerateAsync(string prompt, int fps, float durationSeconds, int seed, MotionModel model)
    {
        var req = new GenerateRequest
        {
            Prompt = prompt,
            Fps = fps,
            DurationSeconds = durationSeconds,
            Seed = seed,
            Format = MotionFormat.Bvh,
            Model = model
        };

        return await _client.GenerateAsync(req);
    }

    public async Task<BatchGenerateReply> GenerateBatchAsync(string prompt, int fps, float durationSeconds, int count, bool useRandomSeed, int seed, MotionModel model)
    {
        var req = new BatchGenerateRequest
        {
            Prompt = prompt,
            Fps = fps,
            DurationSeconds = durationSeconds,
            Count = count,
            UseRandomSeed = useRandomSeed,
            Seed = seed,
            Format = MotionFormat.Bvh,
            Model = model
        };

        return await _client.GenerateBatchAsync(req);
    }

    public void Dispose()
    {
        _channel?.ShutdownAsync().Wait();
    }
}