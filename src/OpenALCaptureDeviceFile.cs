using Silk.NET.OpenAL;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A device file that represents a capture device.
/// </summary>
public class OpenALCaptureDeviceFile : IChildFile
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <inheritdoc/>
    public required string Name { get; init; }

    /// <summary>
    /// The containing folder, if any.
    /// </summary>
    public required OpenALCaptureDevicesFolder Parent { get; init; }

    /// <summary>
    /// The context used for retrieving devices, if set.
    /// </summary>
    public ALContext? OpenALContext { get; set; }

    /// <summary>
    /// The format to use for audio device capture streaming. Default is <see cref="BufferFormat.Stereo16"/>.
    /// </summary>
    public BufferFormat Format { get; set; } = BufferFormat.Stereo16;

    /// <summary>
    /// The sample rate, or frequency, to use for device capture streaming. Default is 16000.
    /// </summary>
    public uint Frequency { get; set; } = 16000;

    /// <inheritdoc/>
    public Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolder?>(Parent);

    /// <inheritdoc/>
    public async Task<Stream> OpenStreamAsync(FileAccess accessMode, CancellationToken cancellationToken = default)
    {
        return new OpenALCaptureDeviceStream
        {
            DeviceFile = this,
            Frequency = Frequency,
            BufferFormat = Format,
        };
    }
}