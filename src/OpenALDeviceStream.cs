using System;
using CommunityToolkit.Diagnostics;
using NAudio.Wave;
using Silk.NET.OpenAL;
using System.IO;
using System.Linq;

namespace OwlCore.Storage.OpenAL;

/// <summary>
/// A streaming audio playback implementation using OpenAL for real-time audio output.
/// Writing PCM audio data to this stream will play the audio samples on the corresponding OpenAL device.
/// </summary>
/// <remarks>
/// This class implements a buffered streaming approach where:
/// 1. Audio data written via Write() is accumulated in memory
/// 2. When enough data is available, streaming begins with multiple OpenAL buffers
/// 3. As OpenAL consumes buffers, they are refilled with new data and re-queued
/// 4. This allows for continuous real-time playback without audio gaps
/// 
/// The streaming uses 4 buffers of 64KB each by default, providing smooth playback
/// while minimizing memory usage and latency.
/// </remarks>
public class OpenALDeviceStream : Stream, IWaveProvider
{
    #region Private Fields

    /// <summary>
    /// Thread synchronization lock to ensure thread-safe access to streaming state.
    /// </summary>
    private object _lockobj = new();

    /// <summary>
    /// OpenAL source handle - represents the audio source that plays the buffered audio data.
    /// </summary>
    private uint _source;

    /// <summary>
    /// Unsafe pointer to the OpenAL device context for this audio device.
    /// </summary>
    private unsafe Context* _deviceContext;

    /// <summary>
    /// Unsafe pointer to the OpenAL device handle.
    /// </summary>
    private unsafe Device* _device;

    #endregion

    #region Streaming Constants

    /// <summary>
    /// Number of OpenAL buffers to use for streaming audio.
    /// Using 4 buffers provides good balance between memory usage and smooth playback.
    /// </summary>
    private const int NUM_BUFFERS = 4;

    /// <summary>
    /// Size of each OpenAL buffer in bytes (64KB).
    /// Larger buffers reduce the frequency of buffer swapping but increase memory usage and latency.
    /// </summary>
    private const int BUFFER_SIZE = 65536;

    #endregion

    #region Streaming State

    /// <summary>
    /// Array of OpenAL buffer handles used for streaming audio data.
    /// These buffers are cycled: filled with data, queued for playback, then refilled when consumed.
    /// </summary>
    private uint[] _alBuffers = new uint[NUM_BUFFERS];

    /// <summary>
    /// Memory stream that accumulates all incoming audio data from Write() calls.
    /// Data is consumed from the front as it's loaded into OpenAL buffers.
    /// </summary>
    private MemoryStream _accumulatedData = new();

    /// <summary>
    /// Flag indicating whether streaming has been initialized and started.
    /// Once true, ProcessCompletedBuffers() will be called on each Write().
    /// </summary>
    private bool _isStreaming = false;

    /// <summary>
    /// Flag indicating whether audio playback has been started via OpenAL.
    /// Prevents multiple calls to SourcePlay() on the same source.
    /// </summary>
    private bool _playbackStarted = false;

    /// <summary>
    /// Timestamp of the last buffer processing check (currently unused).
    /// Could be used for periodic buffer maintenance or debugging.
    /// </summary>
    private DateTime _lastBufferCheck = DateTime.MinValue;

    #endregion

    #region Stream Properties

    /// <inheritdoc/>
    /// <remarks>This stream only supports writing audio data, not reading.</remarks>
    public override bool CanRead => false;

    /// <inheritdoc/>
    /// <remarks>This stream represents a continuous audio output and does not support seeking.</remarks>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    /// <remarks>This stream accepts audio data for playback via Write() operations.</remarks>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    /// <remarks>Continuous audio streams have no predetermined length.</remarks>
    public override long Length =>
        ThrowHelper.ThrowNotSupportedException<long>("Length is not supported for continuous stream");

    #endregion

    #region Audio Configuration Properties

    /// <summary>
    /// The OpenAL API instance used for all audio operations.
    /// This is initialized when the device is first opened.
    /// </summary>
    public AL? OpenALApi { get; set; }

    /// <summary>
    /// The audio buffer format that determines the bit depth and channel configuration.
    /// Default is <see cref="BufferFormat.Stereo16"/> (16-bit stereo).
    /// </summary>
    /// <remarks>
    /// Common formats:
    /// - Mono8: 8-bit mono (1 byte per sample)
    /// - Mono16: 16-bit mono (2 bytes per sample) 
    /// - Stereo8: 8-bit stereo (2 bytes per sample)
    /// - Stereo16: 16-bit stereo (4 bytes per sample)
    /// </remarks>
    public BufferFormat BufferFormat { get; set; } = BufferFormat.Stereo16;

    /// <summary>
    /// The audio sample rate (frequency) in Hz for playback.
    /// Default is 16000 Hz. Common values are 8000, 16000, 22050, 44100, 48000.
    /// </summary>
    /// <remarks>
    /// The sample rate must match the sample rate of the audio data being written,
    /// otherwise playback will be at incorrect speed or pitch.
    /// </remarks>
    public int Frequency { get; set; } = 16000;

    /// <summary>
    /// The OpenAL device file that represents the audio output device for this stream.
    /// </summary>
    public required OpenALDeviceFile DeviceFile { get; init; }

    #endregion

    #region Stream Position/Seek (Not Supported)

    /// <inheritdoc/>
    /// <remarks>Position tracking is not supported for continuous audio streams.</remarks>
    public override long Position
    {
        get => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
        set => ThrowHelper.ThrowNotSupportedException<long>("Position is not supported for continuous stream");
    }

    /// <inheritdoc/>
    /// <remarks>Seeking is not supported for continuous audio streams.</remarks>
    public override long Seek(long offset, SeekOrigin origin) =>
        ThrowHelper.ThrowNotSupportedException<long>("Seeking is not supported for continuous stream");

    /// <inheritdoc/>
    /// <remarks>Setting length is not supported for continuous audio streams.</remarks>
    public override void SetLength(long value) =>
        ThrowHelper.ThrowNotSupportedException("Length is not supported for continuous stream");

    #endregion

    #region Core Streaming Methods

    /// <summary>
    /// Writes PCM audio data to the stream for playback.
    /// </summary>
    /// <param name="buffer">The buffer containing audio data to write.</param>
    /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <remarks>
    /// This method:
    /// 1. Accumulates the provided audio data in an internal buffer
    /// 2. Initializes streaming when enough data is available (2x buffer size)
    /// 3. Processes completed OpenAL buffers if streaming is active
    /// 
    /// The audio data should be raw PCM samples matching the configured BufferFormat and Frequency.
    /// </remarks>
    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        lock (_lockobj)
        {
            // Initialize OpenAL device on first write
            if (_source == default)
                OpenDevice();

            Guard.IsNotNull(OpenALApi);

            // Extract and accumulate the audio data from the provided buffer
            var data = buffer.Skip(offset).Take(count).ToArray();
            _accumulatedData.Write(data, 0, data.Length);

            // Start streaming when we have accumulated enough data to fill at least 2 buffers
            // This ensures smooth initial playback without immediate buffer underruns
            if (!_isStreaming && _accumulatedData.Length >= BUFFER_SIZE * 2)
            {
                StartStreaming();
            }

            // Continue processing any completed buffers if streaming is active
            if (_isStreaming)
            {
                ProcessCompletedBuffers();
            }
        }
    }

    /// <inheritdoc cref="IWaveProvider.Read"/>
    /// <remarks>This stream is for audio output only - reading is not supported.</remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        ThrowHelper.ThrowNotSupportedException<int>("Reading is not supported for playback device");

    /// <summary>
    /// Gets the wave format configuration for this audio stream.
    /// </summary>
    /// <returns>A <see cref="WaveFormat"/> object describing the audio format.</returns>
    /// <remarks>
    /// The wave format is derived from the <see cref="BufferFormat"/> and <see cref="Frequency"/> properties.
    /// This is used by NAudio components to understand the audio data format.
    /// </remarks>
    public WaveFormat WaveFormat => BufferFormat switch
    {
        BufferFormat.Mono8 => new WaveFormat(Frequency, 8, 1),
        BufferFormat.Mono16 => new WaveFormat(Frequency, 16, 1),
        BufferFormat.Stereo8 => new WaveFormat(Frequency, 8, 2),
        BufferFormat.Stereo16 => new WaveFormat(Frequency, 16, 2),
        _ => throw new ArgumentOutOfRangeException()
    };

    #endregion

    #region Device Management

    /// <summary>
    /// Initializes the OpenAL device, context, and buffers for audio playback.
    /// </summary>
    /// <remarks>
    /// This method:
    /// 1. Opens the OpenAL audio device specified by DeviceFile
    /// 2. Creates an OpenAL context for the device
    /// 3. Generates an audio source for playback
    /// 4. Creates the streaming buffers for audio data
    /// 5. Logs device and format information for debugging
    /// </remarks>
    /// <exception cref="Exception">Thrown if OpenAL buffer generation fails.</exception>
    unsafe void OpenDevice()
    {
        Guard.IsNotNull(DeviceFile.Parent?.OpenALContext);

        // Initialize OpenAL API if not already done
        OpenALApi ??= AL.GetApi();
        var parentContext = DeviceFile.Parent.OpenALContext;

        // Open the audio output device and create context
        _device = parentContext.OpenDevice(DeviceFile.Name);
        _deviceContext = parentContext.CreateContext(_device, null);
        parentContext.MakeContextCurrent(_deviceContext);

        // Log device and audio format information for debugging
        Console.WriteLine($"OpenAL Device: {DeviceFile.Name}");
        Console.WriteLine($"Audio Format: {BufferFormat}, Frequency: {Frequency}Hz");

        // Calculate and log audio format details for verification
        int bytesPerSample = BufferFormat switch
        {
            BufferFormat.Mono8 => 1,     // 8-bit mono: 1 byte per sample
            BufferFormat.Mono16 => 2,    // 16-bit mono: 2 bytes per sample
            BufferFormat.Stereo8 => 2,   // 8-bit stereo: 1 byte × 2 channels = 2 bytes per sample
            BufferFormat.Stereo16 => 4,  // 16-bit stereo: 2 bytes × 2 channels = 4 bytes per sample
            _ => 2
        };

        int channels = BufferFormat switch
        {
            BufferFormat.Mono8 or BufferFormat.Mono16 => 1,
            BufferFormat.Stereo8 or BufferFormat.Stereo16 => 2,
            _ => 1
        };

        Console.WriteLine($"Expected bytes per sample: {bytesPerSample}");
        Console.WriteLine($"Expected channels: {channels}");

        // Create OpenAL source (represents the audio emitter/player)
        _source = OpenALApi.GenSource();

        // Generate the streaming buffers that will hold audio data
        fixed (uint* pBuffers = _alBuffers)
        {
            OpenALApi.GenBuffers(NUM_BUFFERS, pBuffers);
        }

        // Check for OpenAL errors during buffer creation
        var bufferError = OpenALApi.GetError();
        if (bufferError is not AudioError.NoError)
            throw new Exception($"GenBuffers error: {bufferError}");

        Console.WriteLine($"Generated {NUM_BUFFERS} OpenAL buffers and 1 source");
    }

    #endregion

    #region Resource Cleanup

    /// <summary>
    /// Releases all OpenAL resources including source, buffers, context, and device.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected override unsafe void Dispose(bool disposing)
    {
        if (OpenALApi is not null)
        {
            // Stop playback if active
            if (_source != default)
                OpenALApi.SourceStop(_source);

            // Clean up OpenAL source
            if (_source != default)
                OpenALApi.DeleteSource(_source);

            // Clean up streaming buffers
            if (_alBuffers.Any(b => b != default))
            {
                fixed (uint* pBuffers = _alBuffers)
                {
                    OpenALApi.DeleteBuffers(NUM_BUFFERS, pBuffers);
                }
            }

            // Clean up OpenAL context and device
            DeviceFile.Parent?.OpenALContext?.DestroyContext(_deviceContext);
            DeviceFile.Parent?.OpenALContext?.CloseDevice(_device);

            OpenALApi.Dispose();
        }

        base.Dispose(disposing);
    }    /// <summary>
         /// Flushes any pending audio data to ensure it gets played.
         /// </summary>
         /// <remarks>
         /// For streaming audio, OpenAL manages the buffer queues automatically,
         /// so no special flush operation is required.
         /// </remarks>
    public override void Flush()
    {
        // For streaming audio, flush means ensuring all queued data is played
        // We don't need to do anything special here as OpenAL handles the queues
    }

    #endregion

    #region Streaming Implementation
    
    /// <summary>
    /// Initializes the streaming process by filling initial OpenAL buffers and starting playback.
    /// </summary>
    /// <remarks>
    /// This method is called when enough audio data has been accumulated to begin streaming.
    /// It performs the following steps:
    /// 1. Fills as many OpenAL buffers as possible with accumulated audio data
    /// 2. Queues the filled buffers for OpenAL playback
    /// 3. Starts audio playback if buffers were successfully queued
    /// 
    /// After this method completes, the streaming flag is set and ProcessCompletedBuffers()
    /// will be called on subsequent Write() operations to maintain the audio stream.
    /// </remarks>
    private unsafe void StartStreaming()
    {
        Guard.IsNotNull(OpenALApi);

        Console.WriteLine("StartStreaming called");
        Console.WriteLine($"Accumulated data: {_accumulatedData.Length} bytes");

        // Mark streaming as active - this enables buffer processing on future Write() calls
        _isStreaming = true;

        // Fill initial buffers with accumulated data
        var buffersQueued = FillInitialBuffers();

        // Queue the filled buffers for OpenAL playback
        if (buffersQueued > 0)
            QueueBuffersForPlayback(buffersQueued);

        // Start audio playback if we haven't already and have buffers ready
        StartPlaybackIfReady(buffersQueued);
    }

    /// <summary>
    /// Fills as many OpenAL buffers as possible with accumulated audio data.
    /// </summary>
    /// <returns>The number of buffers successfully filled and prepared for queuing.</returns>
    /// <remarks>
    /// We try to fill as many of our NUM_BUFFERS as possible to provide a good buffer
    /// of audio data before starting playback, reducing chance of underruns.
    /// </remarks>
    private unsafe int FillInitialBuffers()
    {
        int buffersQueued = 0;

        for (int i = 0; i < NUM_BUFFERS && _accumulatedData.Length > 0; i++)
        {
            var bufferData = ReadAudioDataForBuffer();
            if (bufferData.Length == 0)
                break;

            if (FillSingleBuffer(i, bufferData))
            {
                buffersQueued++;
                Console.WriteLine($"Buffer {i}: Successfully filled and marked for queuing");
            }
        }

        Console.WriteLine($"Total buffers to queue: {buffersQueued}");
        return buffersQueued;
    }

    /// <summary>
    /// Reads audio data from the accumulated buffer for a single OpenAL buffer.
    /// </summary>
    /// <returns>The audio data read from the accumulated buffer.</returns>
    /// <remarks>
    /// This ensures audio plays in the same order it was written (FIFO order).
    /// </remarks>
    private byte[] ReadAudioDataForBuffer()
    {
        // Determine how much data to read for this buffer
        // Use the smaller of BUFFER_SIZE or whatever data we have left
        var dataToRead = Math.Min(BUFFER_SIZE, (int)_accumulatedData.Length);
        var bufferData = new byte[dataToRead];

        // Read from the front of our accumulated data (FIFO order)
        _accumulatedData.Seek(0, SeekOrigin.Begin);
        var bytesRead = _accumulatedData.Read(bufferData, 0, dataToRead);

        Console.WriteLine($"Reading {bytesRead} bytes from accumulated data");

        // Return only the bytes actually read
        if (bytesRead < dataToRead)
        {
            var actualData = new byte[bytesRead];
            Array.Copy(bufferData, actualData, bytesRead);
            return actualData;
        }

        return bufferData;
    }

    /// <summary>
    /// Fills a single OpenAL buffer with audio data and performs validation.
    /// </summary>
    /// <param name="bufferIndex">The index of the buffer being filled.</param>
    /// <param name="bufferData">The audio data to load into the buffer.</param>
    /// <returns>True if the buffer was successfully filled, false otherwise.</returns>
    /// <remarks>
    /// This is where the raw audio bytes get sent to the audio system.
    /// </remarks>
    private unsafe bool FillSingleBuffer(int bufferIndex, byte[] bufferData)
    {
        Guard.IsNotNull(OpenALApi);
        var bytesRead = bufferData.Length;
        if (bytesRead <= 0)
            return false;

        // Debug validation: Log first few bytes of audio data for troubleshooting
        // This helps identify issues like receiving WAV headers instead of raw PCM data
        LogBufferDataForDebugging(bufferIndex, bufferData);

        // Load the PCM audio data into the OpenAL buffer
        fixed (byte* pBuffer = bufferData)
        {
            OpenALApi.BufferData(_alBuffers[bufferIndex], BufferFormat, pBuffer, bytesRead, Frequency);
        }

        // Check for OpenAL errors after loading buffer data
        var bufferError = OpenALApi.GetError();
        if (bufferError is not AudioError.NoError)
        {
            Console.WriteLine($"BufferData error for buffer {bufferIndex}: {bufferError}");
            throw new Exception($"BufferData error: {bufferError}");
        }

        // Remove the consumed data from our accumulation buffer
        // This prevents the same data from being processed again
        RemoveDataFromFront(bytesRead);
        return true;
    }

    /// <summary>
    /// Logs buffer data for debugging purposes.
    /// </summary>
    /// <param name="bufferIndex">The index of the buffer being logged.</param>
    /// <param name="bufferData">The buffer data to log.</param>
    /// <remarks>
    /// This helps identify issues like receiving WAV headers instead of raw PCM data
    /// and checks for silence which might indicate a problem.
    /// </remarks>
    private static void LogBufferDataForDebugging(int bufferIndex, byte[] bufferData)
    {
        var bytesRead = bufferData.Length;

        // Debug validation: Log first few bytes of audio data for troubleshooting
        if (bytesRead >= 8)
        {
            Console.WriteLine($"Buffer {bufferIndex} first bytes: {bufferData[0]:X2} {bufferData[1]:X2} {bufferData[2]:X2} {bufferData[3]:X2} {bufferData[4]:X2} {bufferData[5]:X2} {bufferData[6]:X2} {bufferData[7]:X2}");
        }

        // Debug validation: Check for silence (all zeros) which might indicate a problem
        bool isAllZeros = true;
        for (int j = 0; j < Math.Min(bytesRead, 100); j++)
        {
            if (bufferData[j] != 0)
            {
                isAllZeros = false;
                break;
            }
        }
        Console.WriteLine($"Buffer {bufferIndex}: Is silence (first 100 bytes): {isAllZeros}");
    }

    /// <summary>
    /// Queues the filled buffers for OpenAL playback.
    /// </summary>
    /// <param name="buffersQueued">The number of buffers to queue for playback.</param>
    /// <remarks>
    /// OpenAL maintains an internal queue of buffers to play in sequence.
    /// Using stackalloc is efficient for small, temporary arrays.
    /// </remarks>
    private unsafe void QueueBuffersForPlayback(int buffersQueued)
    {
        Guard.IsNotNull(OpenALApi);
        // Create a stack-allocated array of buffer handles to queue
        var buffersToQueue = stackalloc uint[buffersQueued];
        for (int i = 0; i < buffersQueued; i++)
        {
            buffersToQueue[i] = _alBuffers[i];
        }

        // Add the buffers to OpenAL's playback queue
        OpenALApi.SourceQueueBuffers(_source, buffersQueued, buffersToQueue);

        var queueError = OpenALApi.GetError();
        if (queueError is not AudioError.NoError)
        {
            Console.WriteLine($"SourceQueueBuffers error: {queueError}");
            throw new Exception($"SourceQueueBuffers error: {queueError}");
        }

        Console.WriteLine($"Successfully queued {buffersQueued} buffers");
    }

    /// <summary>
    /// Starts audio playback if conditions are met and playback hasn't already started.
    /// </summary>
    /// <param name="buffersQueued">The number of buffers that were queued for playback.</param>
    /// <remarks>
    /// Prevents multiple calls to SourcePlay() on the same source and verifies
    /// the source actually started playing for debugging purposes.
    /// </remarks>
    private void StartPlaybackIfReady(int buffersQueued)
    {
        Guard.IsNotNull(OpenALApi);
        if (!_playbackStarted && buffersQueued > 0)
        {
            Console.WriteLine("Starting OpenAL playback...");
            OpenALApi.SourcePlay(_source);
            _playbackStarted = true;

            var playError = OpenALApi.GetError();
            if (playError is not AudioError.NoError)
            {
                Console.WriteLine($"SourcePlay error: {playError}");
                throw new Exception($"SourcePlay error: {playError}");
            }

            // Verify the source actually started playing for debugging
            OpenALApi.GetSourceProperty(_source, GetSourceInteger.SourceState, out int sourceState);
            Console.WriteLine($"Source state after SourcePlay: {sourceState} (PLAYING={4114})");
        }
        else if (_playbackStarted)
        {
            Console.WriteLine("Playback already started");
        }
        else
        {
            Console.WriteLine($"Not starting playback: buffersQueued={buffersQueued}");
        }
    }
    
    /// <summary>
    /// Processes buffers that OpenAL has finished playing and refills them with new audio data.
    /// </summary>
    /// <remarks>
    /// This method is called on every Write() operation once streaming has started.
    /// It maintains the continuous audio stream by:
    /// 1. Checking how many buffers OpenAL has finished processing
    /// 2. Unqueuing the completed buffers from OpenAL
    /// 3. Refilling those buffers with new data from the accumulated buffer
    /// 4. Re-queuing the refilled buffers back to OpenAL for continued playback
    /// 5. Ensuring playback continues if it stopped unexpectedly
    /// 
    /// The key condition is that both completed buffers AND new data must be available.
    /// If there's no new data to stream, completed buffers are left unprocessed,
    /// which can cause audio cutoff if the input stream has ended.
    /// </remarks>
    private unsafe void ProcessCompletedBuffers()
    {
        Guard.IsNotNull(OpenALApi);

        var buffersProcessed = GetCompletedBufferCount();
        if (buffersProcessed < 0) return;

        LogStreamingStateIfActive(buffersProcessed);

        // CRITICAL STREAMING LOGIC: Only process if we have BOTH completed buffers AND new data
        // This condition can cause audio cutoff when the input stream ends but OpenAL is still playing
        if (buffersProcessed > 0 && _accumulatedData.Length > 0)
        {
            ProcessAndRefillCompletedBuffers(buffersProcessed);
        }

        EnsurePlaybackContinues();
    }
    
    /// <summary>
    /// Gets the count of buffers that OpenAL has finished processing.
    /// </summary>
    /// <returns>Number of completed buffers, or -1 if an error occurred.</returns>
    private int GetCompletedBufferCount()
    {
        Guard.IsNotNull(OpenALApi);

        // Query OpenAL to see how many buffers have finished playing
        // This tells us how many buffers are available for refilling
        OpenALApi.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int buffersProcessed);

        var processedError = OpenALApi.GetError();
        if (processedError is not AudioError.NoError)
            return -1; // Silently ignore errors during buffer processing to avoid spam

        return buffersProcessed;
    }

    /// <summary>
    /// Logs streaming state information for debugging purposes when there's activity.
    /// </summary>
    /// <param name="buffersProcessed">Number of buffers that have been processed.</param>
    private void LogStreamingStateIfActive(int buffersProcessed)
    {
        Guard.IsNotNull(OpenALApi);
        
        // Debug logging: Track the streaming state to understand what's happening
        // This helps diagnose issues like buffer starvation or playback problems
        if (buffersProcessed > 0 || _accumulatedData.Length > 0)
        {
            OpenALApi.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int buffersQueued);
            OpenALApi.GetSourceProperty(_source, GetSourceInteger.SourceState, out int sourceState);

            // CRITICAL BUG: Audio completely stops if this Console.WriteLine operation is removed!
            // Investigation results:
            // ✅ Console.WriteLine() - works (original)
            // ✅ Console.Error.WriteLine() - works  
            // ⚠️ Debug.WriteLine() - partial improvement, near immediate exit
            // ❌ Thread.MemoryBarrier() - immediate exit
            // ❌ Thread.Yield(), Thread.Sleep(1), Thread.Sleep(0), Task.Delay() - various failures, near immediate exit
            // It's probably the missing lock on the _accumulatedData Stream
            // When we pull from this stream to write to the buffer, we also shift the entire stream back by the number of bytes read.
            Console.WriteLine($"ProcessCompletedBuffers: processed={buffersProcessed}, queued={buffersQueued}, state={sourceState}, accumulatedData={_accumulatedData.Length}");
        }
    }

    /// <summary>
    /// Processes and refills completed buffers with new audio data.
    /// </summary>
    /// <param name="buffersProcessed">Number of buffers to process.</param>
    private unsafe void ProcessAndRefillCompletedBuffers(int buffersProcessed)
    {
        Console.WriteLine($"Processing {buffersProcessed} completed buffers, accumulated data: {_accumulatedData.Length} bytes");

        // Unqueue the completed buffers from OpenAL
        // These buffers have finished playing and are now available for reuse
        var processedBuffers = stackalloc uint[buffersProcessed];
        if (!UnqueueCompletedBuffers(buffersProcessed, processedBuffers))
            return;

        // Refill the processed buffers with new audio data
        // This maintains the continuous stream by recycling completed buffers
        var buffersRefilled = RefillProcessedBuffers(buffersProcessed, processedBuffers);

        // Re-queue the refilled buffers back to OpenAL for continued playback
        // This maintains the continuous audio stream
        if (buffersRefilled > 0)
        {
            RequeueRefilledBuffers(buffersRefilled, processedBuffers);
        }
    }

    /// <summary>
    /// Unqueues completed buffers from OpenAL.
    /// </summary>
    /// <param name="buffersProcessed">Number of buffers to unqueue.</param>
    /// <param name="processedBuffers">Pointer to store the unqueued buffer handles.</param>
    /// <returns>True if successful, false if an error occurred.</returns>
    private unsafe bool UnqueueCompletedBuffers(int buffersProcessed, uint* processedBuffers)
    {
        Guard.IsNotNull(OpenALApi);
        
        OpenALApi.SourceUnqueueBuffers(_source, buffersProcessed, processedBuffers);

        var unqueueError = OpenALApi.GetError();
        if (unqueueError is not AudioError.NoError)
        {
            Console.WriteLine($"SourceUnqueueBuffers error: {unqueueError}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Refills processed buffers with new audio data from the accumulated buffer.
    /// </summary>
    /// <param name="buffersProcessed">Number of buffers available for refilling.</param>
    /// <param name="processedBuffers">Pointer to the buffer handles to refill.</param>
    /// <returns>Number of buffers successfully refilled.</returns>
    private unsafe int RefillProcessedBuffers(int buffersProcessed, uint* processedBuffers)
    {
        Guard.IsNotNull(OpenALApi);
        
        int buffersRefilled = 0;
        for (int i = 0; i < buffersProcessed && _accumulatedData.Length > 0; i++)
        {
            // Determine how much data to read for this buffer
            var dataToRead = Math.Min(BUFFER_SIZE, (int)_accumulatedData.Length);
            var bufferData = new byte[dataToRead];

            // Read from the front of our accumulated data (FIFO order)
            _accumulatedData.Seek(0, SeekOrigin.Begin);
            var bytesRead = _accumulatedData.Read(bufferData, 0, dataToRead);

            if (bytesRead > 0)
            {
                // Load the new audio data into the recycled OpenAL buffer
                fixed (byte* pBuffer = bufferData)
                {
                    OpenALApi.BufferData(processedBuffers[i], BufferFormat, pBuffer, bytesRead, Frequency);
                }
                var bufferError = OpenALApi.GetError();
                if (bufferError is not AudioError.NoError)
                {
                    Console.WriteLine($"BufferData error: {bufferError}");
                    continue; // Skip this buffer and try the next one
                }

                // Remove the consumed data from our accumulation buffer
                RemoveDataFromFront(bytesRead);
                buffersRefilled++;

                Console.WriteLine($"Refilled buffer {i} with {bytesRead} bytes, remaining data: {_accumulatedData.Length} bytes");
            }
        }

        return buffersRefilled;
    }

    /// <summary>
    /// Re-queues refilled buffers back to OpenAL for continued playback.
    /// </summary>
    /// <param name="buffersRefilled">Number of buffers to re-queue.</param>
    /// <param name="processedBuffers">Pointer to the buffer handles to re-queue.</param>
    private unsafe void RequeueRefilledBuffers(int buffersRefilled, uint* processedBuffers)
    {
        Guard.IsNotNull(OpenALApi);
        
        OpenALApi.SourceQueueBuffers(_source, buffersRefilled, processedBuffers);

        var requeueError = OpenALApi.GetError();
        if (requeueError is not AudioError.NoError)
        {
            Console.WriteLine($"SourceQueueBuffers error: {requeueError}");
        }
        else
        {
            Console.WriteLine($"Successfully requeued {buffersRefilled} buffers");
        }
    }

    /// <summary>
    /// Ensures playback continues if it stopped unexpectedly.
    /// </summary>
    private void EnsurePlaybackContinues()
    {
        Guard.IsNotNull(OpenALApi);
        
        // Safety check: Restart playback if it stopped unexpectedly
        // Sometimes OpenAL stops playing if it runs out of queued buffers temporarily
        OpenALApi.GetSourceProperty(_source, GetSourceInteger.SourceState, out int currentSourceState);
        if (currentSourceState != (int)SourceState.Playing && _playbackStarted)
        {
            OpenALApi.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int buffersQueued);
            if (buffersQueued > 0)
            {
                Console.WriteLine("Restarting playback - source stopped but buffers are queued");
                OpenALApi.SourcePlay(_source);
            }
        }
    }

    /// <summary>
    /// Removes the specified number of bytes from the front of the accumulated data buffer.
    /// </summary>
    /// <param name="bytesToRemove">The number of bytes to remove from the beginning of the buffer.</param>
    /// <remarks>
    /// This method is essential for the streaming workflow. As audio data is consumed from
    /// the front of the accumulated buffer to fill OpenAL buffers, this method removes
    /// that consumed data to prevent it from being processed again.
    /// 
    /// The implementation:
    /// 1. Reads all remaining data after the bytes to be removed
    /// 2. Clears the entire accumulated buffer
    /// 3. Writes back only the remaining unconsumed data
    /// 
    /// This approach maintains the FIFO (first-in-first-out) behavior needed for audio streaming
    /// where data must be played in the order it was written.
    /// </remarks>
    private void RemoveDataFromFront(int bytesToRemove)
    {
        if (bytesToRemove <= 0 || _accumulatedData.Length == 0)
            return;

        // Read remaining data after the bytes we want to remove
        var remainingBytes = (int)(_accumulatedData.Length - bytesToRemove);
        if (remainingBytes > 0)
        {
            var remainingData = new byte[remainingBytes];
            _accumulatedData.Seek(bytesToRemove, SeekOrigin.Begin);
            _accumulatedData.Read(remainingData, 0, remainingBytes);

            // Replace buffer contents with remaining data
            _accumulatedData.SetLength(0);
            _accumulatedData.Position = 0;
            _accumulatedData.Write(remainingData, 0, remainingBytes);
        }
        else
        {
            // All data was consumed
            _accumulatedData.SetLength(0);
            _accumulatedData.Position = 0;
        }
    }

    #endregion
}