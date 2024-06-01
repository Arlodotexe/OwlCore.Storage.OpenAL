using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Enumeration;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A folder that contains files corresponding to available playback devices.
/// </summary>
public class OpenALDevicesFolder : IFolder
{
    /// <inheritdoc/>
    public string Id => "/openal/devices/";

    /// <inheritdoc/>
    public string Name => "Devices";

    /// <summary>
    /// The context used for retrieving devices, if set.
    /// </summary>
    public ALContext? OpenALContext { get; set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.All, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Start OpenAL-Soft
        OpenALContext ??= ALContext.GetApi(true);

        var defaultDeviceName = GetDefaultDevice(OpenALContext);
        yield return new OpenALDeviceFile
        {
            Id = "default",
            Name = defaultDeviceName,
            Parent = this,
            OpenALContext = OpenALContext,
        };

        var devices = GetDevices(OpenALContext);
        foreach (var deviceName in devices)
        {
            yield return new OpenALDeviceFile
            {
                Id = deviceName,
                Name = deviceName,
                Parent = this,
                OpenALContext = OpenALContext,
            };
        }
    }

    private unsafe string GetDefaultDevice(ALContext context)
    {
        // Check if enumeration extension is available.
        if (context.TryGetExtension(null, out Enumeration enumeration))
        {
            return enumeration.GetString(null, (GetEnumerationContextString)GetEnumerationContextStringList.DeviceSpecifiers);
        }

        throw new InvalidOperationException("OpenAL enumeration extension not available.");
    }

    private unsafe IEnumerable<string> GetDevices(ALContext context)
    {
        // Check if enumeration extension is available.
        if (context.TryGetExtension(null, out Enumeration enumeration))
        {
            return enumeration.GetStringList(GetEnumerationContextStringList.DeviceSpecifiers);
        }

        throw new InvalidOperationException("OpenAL enumeration extension not available.");
    }
}