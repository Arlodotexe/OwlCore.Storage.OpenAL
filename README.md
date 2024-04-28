# OwlCore.Storage.OpenAL [![Version](https://img.shields.io/nuget/v/OwlCore.Storage.OpenAL.svg)](https://www.nuget.org/packages/OwlCore.Storage.OpenAL)

Handle your audio devices like simple files and folders.

## Featuring:
- Capture devices via OpenALCaptureDeviceFolder. Contains files corresponding to available capture devices whose Stream continuously reads samples from the capture device.

## Install

Published releases are available on [NuGet](https://www.nuget.org/packages/OwlCore.Storage.OpenAL). To install, run the following command in the [Package Manager Console](https://docs.nuget.org/docs/start-here/using-the-package-manager-console).

    PM> Install-Package OwlCore.Storage.OpenAL
    
Or using [dotnet](https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet)

    > dotnet add package OwlCore.Storage.OpenAL

## Usage

```cs
var captureDevicesFolder = new OpenALCaptureDevicesFolder();

// Iterate through all capture devices
await foreach (var captureDevice in captureDevicesFolder.GetFilesAsync(cancellationToken))
    Console.WriteLine($"Capture device: {captureDevice.Name}");

// Get default capture device
var defaultDevice = (OpenALCaptureDeviceFile)await captureDevicesFolder.GetItemAsync($"{captureDevicesFolder.Id}/default");

// Change buffer format or sample rate as needed
defaultDevice.Format = BufferFormat.Mono8;
```

## Financing

We accept donations [here](https://github.com/sponsors/Arlodotexe) and [here](https://www.patreon.com/arlodotexe), and we do not have any active bug bounties.

## Versioning

Version numbering follows the Semantic versioning approach. However, if the major version is `0`, the code is considered alpha and breaking changes may occur as a minor update.

## License

All OwlCore code is licensed under the MIT License. OwlCore is licensed under the MIT License. See the [LICENSE](./src/LICENSE.txt) file for more details.
