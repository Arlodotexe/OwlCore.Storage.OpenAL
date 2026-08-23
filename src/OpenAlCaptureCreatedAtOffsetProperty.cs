using System;

namespace OwlCore.Storage.OpenAL;

/// <summary>Created timestamp with offset property for OpenAL-backed storage items.</summary>
public class OpenAlCaptureCreatedAtOffsetProperty : SimpleStorageProperty<DateTimeOffset?>, ICreatedAtOffsetProperty
{
    /// <summary>
    /// Creates a new instance of <see cref="OpenAlCaptureCreatedAtOffsetProperty"/>.
    /// </summary>
    /// <param name="file">The original file.</param>
    public OpenAlCaptureCreatedAtOffsetProperty(OpenALCaptureDeviceFile file)
        : base(
            id: file.Id + "/" + nameof(ICreatedAtOffset.CreatedAtOffset),
            name: nameof(ICreatedAtOffset.CreatedAtOffset),
            getter: () => file.CaptureStream?.CaptureStartedAt)
    {
    }
}
