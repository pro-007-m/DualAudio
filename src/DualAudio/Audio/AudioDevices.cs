using NAudio.CoreAudioApi;

namespace DualAudio.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (по умолчанию)" : Name;
}

public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> ListActiveOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = GetDefaultId(enumerator);
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                result.Add(new(device.ID, device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
        }
        return result.OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string? DefaultId()
    {
        using var enumerator = new MMDeviceEnumerator();
        return GetDefaultId(enumerator);
    }

    private static string? GetDefaultId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
