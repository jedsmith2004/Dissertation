#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public partial class MotionGenWindow
{
    private const string DebugLogPath = @"C:\Users\jack\OneDrive\Desktop\Coding\Dissertation\.cursor\debug.log";
    private const string DebugRunId = "path-tab-crash-pre";

    private static MotionReplyMeta ParseMeta(string metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
            return null;

        try
        {
            return JsonUtility.FromJson<MotionReplyMeta>(metaJson);
        }
        catch
        {
            return null;
        }
    }

    private static string TryFormatTimestamp(string createdAtUtc)
    {
        if (DateTime.TryParse(createdAtUtc, out var timestamp))
            return timestamp.ToLocalTime().ToString("g");

        return createdAtUtc;
    }

    private static void RevealExport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path) || Directory.Exists(path))
        {
            EditorUtility.RevealInFinder(path);
            return;
        }

        var parentDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentDirectory) && Directory.Exists(parentDirectory))
            EditorUtility.RevealInFinder(parentDirectory);
    }

    private static void DebugLog(string location, string message, string hypothesisId, string dataJson)
    {
        try
        {
            File.AppendAllText(
                DebugLogPath,
                "{" +
                $"\"id\":\"{Guid.NewGuid():N}\"," +
                $"\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}," +
                $"\"runId\":\"{DebugRunId}\"," +
                $"\"hypothesisId\":\"{EscapeJson(hypothesisId)}\"," +
                $"\"location\":\"{EscapeJson(location)}\"," +
                $"\"message\":\"{EscapeJson(message)}\"," +
                $"\"data\":{(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson)}" +
                "}" + Environment.NewLine);
        }
        catch
        {
            // Intentionally swallow debug logging failures.
        }
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string ToJsonBool(bool value)
    {
        return value ? "true" : "false";
    }
}
#endif
