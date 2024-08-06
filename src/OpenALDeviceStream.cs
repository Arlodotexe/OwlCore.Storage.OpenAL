using System;
using CommunityToolkit.Diagnostics;
using NAudio.Wave;
using Silk.NET.OpenAL;
using System.IO;
using System.Linq;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// Writing wav data to this stream will play the audio samples on the corresponding device.
/// </summary>
public class OpenALDeviceStream : Stream, IWaveProvider
{
    private object _lockobj = new();
    private uint _source;
    private unsafe Context* _deviceContext;
    private unsafe Device* _device;
    private uint _alBuffer;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length =>
        ThrowHelper.ThrowNotSupportedException<long>("Length is not supported for continuous stream");

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
    public int Frequency { get; init; } = 16000;

    /// <summary>
    /// The device file which corresponds to the capture device that provides for this stream.
    /// </summary>
    public required OpenALDeviceFile DeviceFile { get; init; }

    /// <inheritdoc/>
    public override long Position
    {
        get => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
        set => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) =>
        ThrowHelper.ThrowNotSupportedException<long>("Seeking is not supported for continuous stream");

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        ThrowHelper.ThrowNotSupportedException("Length is not supported for continuous stream");

    /// <inheritdoc/>
    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        if (_source == default)
            OpenDevice();

        Guard.IsNotNull(OpenALApi);

        fixed (byte* pBuffer = buffer.Skip(offset).Take(count).ToArray())
        {
            OpenALApi.BufferData(_alBuffer, BufferFormat, pBuffer, count, Frequency);
        }
        
        var bufferError = OpenALApi.GetError();
        if (bufferError is not AudioError.NoError)
            throw new Exception(bufferError.ToString());

        OpenALApi.SetSourceProperty(_source, SourceInteger.Buffer, _alBuffer);

        var setSourceError = OpenALApi.GetError();
        if (setSourceError is not AudioError.NoError)
            throw new Exception(setSourceError.ToString());

        OpenALApi.SourcePlay(_source);

        var sourcePlayError = OpenALApi.GetError();
        if (sourcePlayError is not AudioError.NoError)
            throw new Exception(sourcePlayError.ToString());
    }

    /// <inheritdoc cref="IWaveProvider.Read"/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ThrowHelper.ThrowNotSupportedException<int>("Reading is not supported for playback device");

    /// <inheritdoc/>
    public WaveFormat WaveFormat => BufferFormat switch
    {
        BufferFormat.Mono8 => new WaveFormat(Frequency, 8, 1),
        BufferFormat.Mono16 => new WaveFormat(Frequency, 16, 1),
        BufferFormat.Stereo8 => new WaveFormat(Frequency, 8, 2),
        BufferFormat.Stereo16 => new WaveFormat(Frequency, 16, 2),
        _ => throw new ArgumentOutOfRangeException()
    };

    unsafe void OpenDevice()
    {
        Guard.IsNotNull(DeviceFile.Parent?.OpenALContext);

        OpenALApi ??= AL.GetApi();
        var parentContext = DeviceFile.Parent.OpenALContext;

        // Get output device
        _device = parentContext.OpenDevice(DeviceFile.Name);
        _deviceContext = parentContext.CreateContext(_device, null);

        parentContext.MakeContextCurrent(_deviceContext);

        _source = OpenALApi.GenSource();
        _alBuffer = OpenALApi.GenBuffer();
    }

    /// <inheritdoc/>
    protected override unsafe void Dispose(bool disposing)
    {
        if (OpenALApi is not null)
        {
            if (_source != default)
                OpenALApi.SourceStop(_source);

            if (_source != default)
                OpenALApi.DeleteSource(_source);

            DeviceFile.OpenALContext?.DestroyContext(_deviceContext);
            DeviceFile.OpenALContext?.CloseDevice(_device);

            OpenALApi.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
        // do nothing
    }
}