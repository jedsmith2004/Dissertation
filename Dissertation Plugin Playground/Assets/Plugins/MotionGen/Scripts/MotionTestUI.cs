using UnityEngine;

public class MotionTestUI : MonoBehaviour
{
    [SerializeField] private MotionManager motionManager;

    private string _statusText = "Ready";
    private string _bvhPreview = "";

    private void OnEnable()
    {
        if (motionManager != null)
        {
            motionManager.OnPingReceived += HandlePing;
            motionManager.OnBVHReceived += HandleBVH;
        }
    }

    private void OnDisable()
    {
        if (motionManager != null)
        {
            motionManager.OnPingReceived -= HandlePing;
            motionManager.OnBVHReceived -= HandleBVH;
        }
    }

    private void Awake()
    {
        if (motionManager == null)
        {
            motionManager = GetComponent<MotionManager>()
                ?? FindFirstObjectByType<MotionManager>();
        }
    }

    private void HandlePing(string version, bool cuda, string device)
    {
        _statusText = $"Connected: v{version} | CUDA: {cuda} | {device}";
    }

    private void HandleBVH(string bvhContent, string filename)
    {
        _bvhPreview = bvhContent.Length > 200 
            ? bvhContent.Substring(0, 200) + "..." 
            : bvhContent;
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 400));
        
        GUILayout.Label("Motion Backend Test", GUI.skin.box);
        GUILayout.Label(_statusText);

        if (GUILayout.Button("Ping Server"))
            motionManager.TestPing();

        if (GUILayout.Button("Get Dummy BVH"))
            motionManager.RequestDummyBVH();

        GUILayout.Label("BVH Preview:");
        GUILayout.TextArea(_bvhPreview, GUILayout.Height(150));

        GUILayout.EndArea();
    }
}