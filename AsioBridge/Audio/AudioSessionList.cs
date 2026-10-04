using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace AsioBridge.Audio;

public sealed record AudioSessionInfo(int ProcessId, string DisplayName, string ProcessName);

public static class AudioSessionList
{
    /// <summary>
    /// Enumerate render audio sessions on the default output device.
    /// Useful for picking which process to capture.
    /// </summary>
    public static IReadOnlyList<AudioSessionInfo> GetDefaultRenderSessions()
    {
        var result = new List<AudioSessionInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    int pid = (int)session.GetProcessID;
                    if (pid == 0) continue;

                    string procName = "";
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        procName = p.ProcessName;
                    }
                    catch
                    {
                        procName = $"pid:{pid}";
                    }

                    // Short label for the combo: process name, truncated if needed.
                    string display = string.IsNullOrWhiteSpace(procName) ? procName : procName;
                    if (display.Length > 18)
                        display = display.Substring(0, 18) + "…";

                    result.Add(new AudioSessionInfo(pid, display, procName));
                }
                catch
                {
                    // session may have expired mid-enumeration
                }
            }
        }
        catch
        {
            // no default device
        }

        return result
            .GroupBy(s => s.ProcessId)
            .Select(g => g.First())
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<(int Pid, string Title)> GetVisibleWindows()
    {
        var list = new List<(int, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                list.Add((p.Id, p.MainWindowTitle));
            }
            catch
            {
                // ignore
            }
            finally
            {
                p.Dispose();
            }
        }
        return list
            .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
