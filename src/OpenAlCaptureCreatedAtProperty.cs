using System;

namespace OwlCore.Storage.OpenAL;

/// <summary>Created timestamp property for OpenAL-backed storage items.</summary>
public class OpenAlCaptureCreatedAtProperty : SimpleStorageProperty<DateTime?>, ICreatedAtProperty
{
    /// <summary>
    /// Creates a new instance of <see cref="OpenAlCaptureCreatedAtProperty"/>.
    /// </summary>
    /// <param name="file">The original file.</param>
    public OpenAlCaptureCreatedAtProperty(OpenALCaptureDeviceFile file)
        : base(
            id: file.Id + "/" + nameof(ICreatedAt.CreatedAt),
            name: nameof(ICreatedAt.CreatedAt),
            getter: () => file.CaptureStream?.CaptureStartedAt?.LocalDateTime)
    {
    }
}
