using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DualAudio.Platform;

/// <summary>
/// Temporarily selects the capture source as the Windows playback default.
/// Communications is deliberately left alone. Windows has no supported public
/// setter for this setting: IPolicyConfig is an undocumented Windows interface.
/// If it is unavailable, the caller can offer manual selection in Sound settings.
/// </summary>
internal sealed class DefaultOutputLease : IDisposable
{
    private static readonly object PolicyGate = new();
    private static readonly Role[] ManagedRoles = { Role.Console, Role.Multimedia };
    private readonly string sourceId;
    private readonly Dictionary<Role, string> previous;
    private bool disposed;

    private DefaultOutputLease(string sourceId, Dictionary<Role, string> previous)
    {
        this.sourceId = sourceId;
        this.previous = previous;
    }

    /// <summary>Best-effort restoration failure, available after Dispose.</summary>
    public Exception? RestoreError { get; private set; }

    public static DefaultOutputLease Acquire(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Не выбрано устройство — источник звука.", nameof(sourceId));

        lock (PolicyGate)
        {
            using var devices = new MMDeviceEnumerator();
            using (var source = devices.GetDevice(sourceId))
            {
                if (source.DataFlow != DataFlow.Render || source.State != DeviceState.Active)
                    throw new InvalidOperationException("Источник звука отключён или недоступен.");
            }

            // Read both roles before changing either. A missing default is an
            // acquisition error, since it could not be faithfully restored later.
            var previous = new Dictionary<Role, string>();
            foreach (var role in ManagedRoles)
                previous.Add(role, ReadDefaultId(devices, role));

            var lease = new DefaultOutputLease(sourceId, previous);
            if (previous[Role.Console] == sourceId && previous[Role.Multimedia] == sourceId)
                return lease;

            object? policyObject = null;
            try
            {
                var policy = CreatePolicy(out policyObject);
                foreach (var role in ManagedRoles)
                {
                    if (!StringComparer.Ordinal.Equals(previous[role], sourceId))
                        SetDefault(policy, sourceId, role);
                }

                // Do not report success when an OS version or policy rejects the
                // request without a useful HRESULT.
                foreach (var role in ManagedRoles)
                {
                    if (!StringComparer.Ordinal.Equals(ReadDefaultId(devices, role), sourceId))
                        throw new InvalidOperationException("Windows не применила выбранный выход звука.");
                }
                return lease;
            }
            catch (Exception error)
            {
                // One role can succeed before the other fails. Restore every
                // role still owned by this lease, including API side effects.
                lease.RestoreCore(devices);
                lease.disposed = true;
                if (lease.RestoreError is not null)
                {
                    throw new AggregateException(
                        "Не удалось назначить источник выходом Windows и полностью восстановить прежний выход. Выберите выход вручную в параметрах звука.",
                        error, lease.RestoreError);
                }
                throw new InvalidOperationException(
                    "Не удалось назначить источник выходом Windows. Выберите его вручную в параметрах звука или отключите автоматическое переключение.", error);
            }
            finally
            {
                ReleasePolicy(policyObject);
            }
        }
    }

    public void Dispose()
    {
        lock (PolicyGate)
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                using var devices = new MMDeviceEnumerator();
                RestoreCore(devices);
            }
            catch (Exception error)
            {
                RestoreError = error;
            }
        }
    }

    private void RestoreCore(MMDeviceEnumerator devices)
    {
        var failures = new List<Exception>();
        var restore = new List<KeyValuePair<Role, string>>();
        foreach (var role in ManagedRoles)
        {
            try
            {
                // Only undo our own changes. Respect a device the user selected
                // while the app was running, and skip roles already on source
                // when the lease was acquired.
                if (!StringComparer.Ordinal.Equals(previous[role], sourceId) &&
                    StringComparer.Ordinal.Equals(ReadDefaultId(devices, role), sourceId))
                    restore.Add(new KeyValuePair<Role, string>(role, previous[role]));
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        object? policyObject = null;
        try
        {
            if (restore.Count > 0)
            {
                var policy = CreatePolicy(out policyObject);
                foreach (var entry in restore)
                {
                    try
                    {
                        using var destination = devices.GetDevice(entry.Value);
                        if (destination.State != DeviceState.Active)
                            throw new InvalidOperationException("Прежнее устройство вывода отключено. Выберите другой выход в параметрах звука Windows.");

                        // Recheck immediately before each change in case the
                        // user changed the setting while restoration began.
                        if (StringComparer.Ordinal.Equals(ReadDefaultId(devices, entry.Key), sourceId))
                            SetDefault(policy, entry.Value, entry.Key);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            ReleasePolicy(policyObject);
        }

        RestoreError = failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Не удалось полностью восстановить прежний выход Windows.", failures)
        };
    }

    private static string ReadDefaultId(MMDeviceEnumerator devices, Role role)
    {
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, role);
        return device.ID;
    }

    private static IPolicyConfig CreatePolicy(out object policyObject)
    {
        policyObject = new PolicyConfigClient();
        return (IPolicyConfig)policyObject;
    }

    private static void SetDefault(IPolicyConfig policy, string deviceId, Role role)
    {
        Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
    }

    private static void ReleasePolicy(object? policyObject)
    {
        if (policyObject is not null && Marshal.IsComObject(policyObject))
            Marshal.FinalReleaseComObject(policyObject);
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private sealed class PolicyConfigClient { }

    // Vtable layout follows IPolicyConfig (Windows 7 and later), with
    // ResetDeviceFormat included. Do not substitute the older Vista IID:
    // its SetDefaultEndpoint slot differs by one.
    // ABI reference: github.com/fredemmott/AudioDeviceLib/blob/master/src/PolicyConfig.h
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
