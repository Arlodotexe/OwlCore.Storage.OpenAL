using Silk.NET.OpenAL;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A device file that represents a capture device.
/// </summary>
public class OpenALCaptureDeviceFile : IChildFile, ICreatedAtOffset
{
    /// <summary>
    /// Creates a new instance of <see cref="OpenALCaptureDeviceFile"/>.
    /// </summary>
    public OpenALCaptureDeviceFile()
    {
        CreatedAt = new OpenAlCaptureCreatedAtProperty(this);
        CreatedAtOffset = new OpenAlCaptureCreatedAtOffsetProperty(this);
    }

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
    /// Gets the singleton stream used to capture OpenAL context across file opens.
    /// </summary>
    public OpenALCaptureDeviceStream? CaptureStream { get; private set; }

    /// <summary>
    /// The format to use for audio device capture streaming. Default is <see cref="BufferFormat.Stereo16"/>.
    /// </summary>
    public BufferFormat Format { get; set; } = BufferFormat.Stereo16;

    /// <summary>
    /// The sample rate, or frequency, to use for device capture streaming. Default is 16000.
    /// </summary>
    public uint Frequency { get; set; } = 16000;

    /// <inheritdoc/>
    public ICreatedAtProperty CreatedAt { get; init; }

    /// <inheritdoc/>
    public ICreatedAtOffsetProperty CreatedAtOffset { get; init; }

    /// <inheritdoc/>
    public Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolder?>(Parent);

    /// <inheritdoc/>
    public async Task<Stream> OpenStreamAsync(FileAccess accessMode, CancellationToken cancellationToken = default)
    {
        return CaptureStream ??= new OpenALCaptureDeviceStream
        {
            DeviceFile = this,
            Frequency = Frequency,
            BufferFormat = Format,
        };
    }
}