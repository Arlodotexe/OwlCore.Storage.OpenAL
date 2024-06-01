using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Enumeration;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A folder that contains files corresponding to available capture devices.
/// </summary>
public class OpenALCaptureDevicesFolder : IFolder
{
    /// <inheritdoc/>
    public string Id => "openal_capture_devices";

    /// <inheritdoc/>
    public string Name => "Capture devices";

    /// <summary>
    /// The context used for retrieving devices, if set.
    /// </summary>
    public ALContext? OpenALContext { get; set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.All, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Start OpenAL-Soft
        OpenALContext ??= ALContext.GetApi(true);

        var defaultDeviceName = GetDefaultCaptureDevice(OpenALContext);
        yield return new OpenALCaptureDeviceFile
        {
            Id = "default",
            Name = defaultDeviceName,
            Parent = this,
            OpenALContext = OpenALContext,
        };

        var devices = GetCaptureDevices(OpenALContext);
        foreach (var deviceName in devices)
        {
            yield return new OpenALCaptureDeviceFile
            {
                Id = deviceName,
                Name = deviceName,
                Parent = this,
                OpenALContext = OpenALContext,
            };
        }
    }

    private unsafe string GetDefaultCaptureDevice(ALContext context)
    {
        // Check if enumeration extension is available.
        if (context.TryGetExtension(null, out Enumeration enumeration))
        {
            return enumeration.GetString(null, (GetEnumerationContextString)Silk.NET.OpenAL.Extensions.EXT.Enumeration.GetCaptureEnumerationContextString.DefaultCaptureDeviceSpecifier);
        }

        throw new InvalidOperationException("OpenAL enumeration extension not available.");
    }

    private unsafe IEnumerable<string> GetCaptureDevices(ALContext context)
    {
        // Check if enumeration extension is available.
        if (context.TryGetExtension(null, out Enumeration enumeration))
        {
            return enumeration.GetStringList((GetEnumerationContextStringList)Silk.NET.OpenAL
                .Extensions.EXT.Enumeration.GetCaptureContextStringList.CaptureDeviceSpecifiers);
        }

        throw new InvalidOperationException("OpenAL enumeration extension not available.");
    }
}