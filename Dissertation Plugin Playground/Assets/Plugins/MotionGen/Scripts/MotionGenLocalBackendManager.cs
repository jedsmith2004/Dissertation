#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

[Serializable]
public class MotionGenModelStatusSnapshot
{
    public string backend_version;
    public MotionGenModelStatusItem[] models;
}

[Serializable]
public class MotionGenModelStatusItem
{
    public string id;
    public string display_name;
    public string status;
    public string install_root;
    public string[] missing_files;
    public string[] corrupt_files;
}

[Serializable]
public class MotionGenInstallResult
{
    public bool ok;
    public string model;
    public MotionGenModelStatusItem status;
    public string error;
}

public static class MotionGenLocalBackendManager
{
    private static Process _backendProcess;
    private static string _lastBackendLogLine;

    public static string LastBackendLogLine => _lastBackendLogLine;
    public static bool IsBackendProcessRunning => _backendProcess != null && !_backendProcess.HasExited;

    public static string ResolveBackendRoot(MotionGenEditorSettings settings)
    {
        var fromSettings = (settings.backendRootPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromSettings))
            return Path.GetFullPath(fromSettings);

        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, "motion-backend");
    }

    public static string ResolveManifestPath(MotionGenEditorSettings settings)
    {
        var fromSettings = (settings.backendManifestPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromSettings))
            return Path.GetFullPath(fromSettings);

        return Path.Combine(ResolveBackendRoot(settings), "packaging", "backend_manifest.json");
    }

    public static string ResolvePythonExecutable(MotionGenEditorSettings settings)
    {
        var explicitPath = (settings.backendPythonExecutable ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var backendRoot = ResolveBackendRoot(settings);
        var venvPython = Path.Combine(backendRoot, "venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
            return venvPython;

        return "python";
    }

    public static async Task<(bool ok, string message)> PingBackendAsync(MotionGenEditorSettings settings)
    {
        try
        {
            using var client = new MotionClient(settings.serverHost, settings.serverPort);
            var reply = await client.PingAsync();
            if (reply == null)
                return (false, "Ping failed: no response.");

            return (true, $"Backend reachable: version={reply.Version}, cuda={reply.CudaAvailable}, device={reply.DeviceName}");
        }
        catch (Exception ex)
        {
            return (false, $"Ping failed: {ex.Message}");
        }
    }

    public static async Task<(bool ok, string message)> StartBackendAndWaitForHealthAsync(MotionGenEditorSettings settings)
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            return (false, "Local backend startup flow is currently Windows-only.");

        var backendRoot = ResolveBackendRoot(settings);
        if (!Directory.Exists(backendRoot))
            return (false, $"Backend root not found: {backendRoot}");

        if (!IsBackendProcessRunning)
        {
            var startupResult = StartBackendProcess(settings, backendRoot);
            if (!startupResult.ok)
                return startupResult;
        }

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < timeoutAt)
        {
            var ping = await PingBackendAsync(settings);
            if (ping.ok)
                return (true, $"Local backend started. {ping.message}");

            await Task.Delay(500);
        }

        return (false, "Backend process started, but health check timed out.");
    }

    public static (bool ok, string message) StopBackendProcess()
    {
        if (_backendProcess == null)
            return (true, "Backend process is not running.");

        try
        {
            if (!_backendProcess.HasExited)
                _backendProcess.Kill();

            _backendProcess.Dispose();
            _backendProcess = null;
            return (true, "Stopped backend process.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop backend: {ex.Message}");
        }
    }

    public static async Task<(bool ok, MotionGenModelStatusSnapshot snapshot, string message)> QueryModelStatusAsync(MotionGenEditorSettings settings)
    {
        var manifestPath = ResolveManifestPath(settings);
        if (!File.Exists(manifestPath))
            return (false, null, $"Manifest not found: {manifestPath}");

        var pythonExe = ResolvePythonExecutable(settings);
        var scriptPath = Path.Combine(ResolveBackendRoot(settings), "scripts", "model_manager.py");
        if (!File.Exists(scriptPath))
            return (false, null, $"Model manager script not found: {scriptPath}");

        var args = Quote(scriptPath) + " status --manifest " + Quote(manifestPath);
        var result = await RunProcessAsync(pythonExe, args, ResolveBackendRoot(settings), null);
        if (!result.ok)
            return (false, null, result.message);

        try
        {
            var snapshot = JsonUtility.FromJson<MotionGenModelStatusSnapshot>(result.stdout.Trim());
            if (snapshot == null)
                return (false, null, "Model status parse failed.");

            return (true, snapshot, "Model status refreshed.");
        }
        catch (Exception ex)
        {
            return (false, null, $"Model status parse failed: {ex.Message}");
        }
    }

    public static async Task<(bool ok, string message)> InstallModelAsync(MotionGenEditorSettings settings, string modelId, Action<string> onOutput)
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            return (false, "Model install flow is currently Windows-only.");

        var manifestPath = ResolveManifestPath(settings);
        if (!File.Exists(manifestPath))
            return (false, $"Manifest not found: {manifestPath}");

        var pythonExe = ResolvePythonExecutable(settings);
        var scriptPath = Path.Combine(ResolveBackendRoot(settings), "scripts", "model_manager.py");
        if (!File.Exists(scriptPath))
            return (false, $"Model manager script not found: {scriptPath}");

        var argsBuilder = new StringBuilder();
        argsBuilder.Append(Quote(scriptPath));
        argsBuilder.Append(" install --manifest ").Append(Quote(manifestPath));
        argsBuilder.Append(" --model ").Append(Quote(modelId));
        if (!string.IsNullOrWhiteSpace(settings.modelDownloadBaseUrl))
            argsBuilder.Append(" --base-url ").Append(Quote(settings.modelDownloadBaseUrl.Trim()));

        var result = await RunProcessAsync(pythonExe, argsBuilder.ToString(), ResolveBackendRoot(settings), onOutput);
        if (!result.ok)
            return (false, result.message);

        var finalLine = GetLastNonEmptyLine(result.stdout);
        try
        {
            var payload = JsonUtility.FromJson<MotionGenInstallResult>(finalLine);
            if (payload != null && payload.ok)
                return (true, $"Installed model '{modelId}' successfully.");

            if (payload != null && !string.IsNullOrWhiteSpace(payload.error))
                return (false, payload.error);
        }
        catch
        {
            // Keep fallback message when installer output is non-JSON.
        }

        return (true, $"Install command completed for model '{modelId}'.");
    }

    private static (bool ok, string message) StartBackendProcess(MotionGenEditorSettings settings, string backendRoot)
    {
        var startupScript = Path.Combine(backendRoot, "scripts", "start_local_backend.ps1");
        if (!File.Exists(startupScript))
            return (false, $"Startup script not found: {startupScript}");

        var pythonExe = ResolvePythonExecutable(settings);
        var args = $"-ExecutionPolicy Bypass -File {Quote(startupScript)} -Port {settings.serverPort} -PythonExe {Quote(pythonExe)}";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = args,
            WorkingDirectory = backendRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            _backendProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _backendProcess.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _lastBackendLogLine = eventArgs.Data;
            };
            _backendProcess.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _lastBackendLogLine = eventArgs.Data;
            };
            _backendProcess.Exited += (_, __) =>
            {
                _lastBackendLogLine = "Backend process exited.";
            };

            _backendProcess.Start();
            _backendProcess.BeginOutputReadLine();
            _backendProcess.BeginErrorReadLine();
            return (true, "Starting local backend process...");
        }
        catch (Exception ex)
        {
            _backendProcess = null;
            return (false, $"Failed to start backend process: {ex.Message}");
        }
    }

    private static async Task<(bool ok, string stdout, string message)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string> onOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                stdout.AppendLine(eventArgs.Data);
                onOutput?.Invoke(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                stderr.AppendLine(eventArgs.Data);
                onOutput?.Invoke(eventArgs.Data);
            };

            if (!process.Start())
                return (false, string.Empty, "Failed to start process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Run(() => process.WaitForExit());

            if (process.ExitCode != 0)
            {
                var message = !string.IsNullOrWhiteSpace(stderr.ToString())
                    ? stderr.ToString().Trim()
                    : stdout.ToString().Trim();
                return (false, stdout.ToString(), message);
            }

            return (true, stdout.ToString(), "OK");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static string GetLastNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Replace("\r", string.Empty).Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = lines[index].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
#endif
