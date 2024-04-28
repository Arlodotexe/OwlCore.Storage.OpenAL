using CommunityToolkit.Diagnostics;
using NAudio.Wave;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using System;
using System.IO;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A continuous stream with no seeking, position or writing that fills Read buffers with as many audio samples as can fit.
/// </summary>
public class OpenALCaptureDeviceStream : Stream, IWaveProvider
{
    private AudioCapture<BufferFormat>? _audioCapture;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => ThrowHelper.ThrowNotSupportedException<long>("Length is not supported for continuous stream");

    /// <summary>
    /// The OpenAL API used for audio capture.
    /// </summary>
    public AL? OpenALApi { get; set; }

    /// <summary>
    /// The buffer format to use for audio capture. Default is <see cref="BufferFormat.Stereo16"/>.
    /// </summary>
    public BufferFormat BufferFormat { get; init; } = BufferFormat.Stereo16;

    /// <summary>
    /// The frequency, or sample rate, to use for audio capture. Default is 16000.
    /// </summary>
    public uint Frequency { get; init; } = 16000;

    /// <summary>
    /// The device file which corresponds to the capture device that provides for this stream.
    /// </summary>
    public required OpenALCaptureDeviceFile DeviceFile { get; init; }

    /// <inheritdoc/>
    public override long Position
    {
        get => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
        set => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => ThrowHelper.ThrowNotSupportedException<long>("Seeking is not supported for continuous stream");

    /// <inheritdoc/>
    public override void SetLength(long value) => ThrowHelper.ThrowNotSupportedException("Length is not supported for continuous stream");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => ThrowHelper.ThrowNotSupportedException("Writing is not supported for continuous stream");

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = GetBytesPerSample(BufferFormat);
        int samplesNeeded = count / bytesPerSample;
        int actualBytesToCopy = samplesNeeded * bytesPerSample;

        Read(buffer, samplesNeeded);

        return actualBytesToCopy;
    }

    /// <inheritdoc/>
    public WaveFormat WaveFormat => BufferFormat switch
    {
        BufferFormat.Mono8 => new WaveFormat((int)Frequency, 8, 1),
        BufferFormat.Mono16 => new WaveFormat((int)Frequency, 16, 1),
        BufferFormat.Stereo8 => new WaveFormat((int)Frequency, 8, 2),
        BufferFormat.Stereo16 => new WaveFormat((int)Frequency, 16, 2),
    };

    /// <summary>
    /// Reads <paramref name="sampleCount"/> samples into the provided <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The buffer to load samples into.</param>
    /// <param name="sampleCount">The number of samples to load.</param>
    public unsafe void Read(byte[] buffer, int sampleCount)
    {
        _audioCapture ??= CreateCaptureForDevice();
        if (!_audioCapture.IsRunning)
            _audioCapture.Start();

        fixed (byte* ptr = buffer)
        {
            _audioCapture.CaptureSamples(ptr, sampleCount);
        }
    }

    unsafe AudioCapture<BufferFormat> CreateCaptureForDevice()
    {
        OpenALApi ??= AL.GetApi(true);
        var context = DeviceFile.Parent?.OpenALContext ?? ALContext.GetApi(true);

        // Get input device
        var inputDevice = context.OpenDevice(DeviceFile.Name);
        var inputDeviceContext = context.CreateContext(inputDevice, null);
        context.MakeContextCurrent(inputDeviceContext);

        // Get capture api for input devices
        if (!context.TryGetExtension(inputDevice, out Capture capture))
            throw new InvalidOperationException($"Couldn't open capture for input device {DeviceFile.Name}");

        return _audioCapture ??= capture.CreateCapture<BufferFormat>(DeviceFile.Name, Frequency, BufferFormat);
    }

    private int GetBytesPerSample(BufferFormat format) => format switch
    {
        BufferFormat.Mono8 => 1,
        BufferFormat.Mono16 => 2,
        BufferFormat.Stereo8 => 2,
        BufferFormat.Stereo16 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    /// <inheritdoc/>
    public override void Flush()
    {
        _audioCapture?.Stop();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        _audioCapture?.Dispose();
        base.Dispose(disposing);
    }
}