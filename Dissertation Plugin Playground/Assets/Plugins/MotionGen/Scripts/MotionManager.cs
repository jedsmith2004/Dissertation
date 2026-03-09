using System;
using System.Text;
using UnityEngine;

public class MotionManager : MonoBehaviour
{
    [Header("Server Settings")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 50051;

    private MotionClient _client;

    public event Action<string, bool, string> OnPingReceived;
    public event Action<string, string> OnBVHReceived; // bvhContent, filename

    private void Start()
    {
        _client = new MotionClient(host, port);
    }

    private void OnDestroy()
    {
        _client?.Dispose();
    }

    public void TestPing()
    {
        try
        {
            var response = _client.Ping();
            Debug.Log($"[MotionGen] Ping OK: v{response.Version}, CUDA: {response.CudaAvailable}, Device: {response.DeviceName}");
            OnPingReceived?.Invoke(response.Version, response.CudaAvailable, response.DeviceName);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MotionGen] Ping failed: {ex.Message}");
        }
    }

    public void RequestDummyBVH()
    {
        try
        {
            var response = _client.GetDummyBVH();
            string bvhContent = Encoding.UTF8.GetString(response.Data.ToByteArray());
            Debug.Log($"[MotionGen] Got BVH: {response.Filename}, {response.Data.Length} bytes");
            OnBVHReceived?.Invoke(bvhContent, response.Filename);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MotionGen] GetDummyBVH failed: {ex.Message}");
        }
    }
}