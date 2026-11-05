# Third-party notices

This file currently covers the packet-capture dependencies added with the packet trace feature (PT0.2). Notices for the other
package references in `Directory.Packages.props` (for example Serilog, CommunityToolkit.Mvvm and the Microsoft.Extensions packages)
will be added in a follow-up. This file is copied next to `AcsTool.exe` in the published app.

## Packet capture (`src/AcsTool.PacketCapture`)

| Component | Version | License | Source |
|---|---|---|---|
| SharpPcap | 6.3.1 | MIT | https://github.com/dotpcap/sharppcap (NuGet: https://www.nuget.org/packages/SharpPcap/6.3.1) |
| PacketDotNet | 1.4.8 | MPL-2.0 | https://github.com/dotpcap/packetnet (NuGet: https://www.nuget.org/packages/PacketDotNet/1.4.8) |

### SharpPcap — MIT License

Copyright (c) Tamir Gal, Chris Morgan, Ayoub Kaanich, Noah Potash, Phillip Lemon, Evan Plaice

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files
(the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### PacketDotNet — Mozilla Public License 2.0

PacketDotNet is distributed under the Mozilla Public License, version 2.0 (https://mozilla.org/MPL/2.0/). AcsTool ships the
unmodified `PacketDotNet.dll` from the official NuGet package. The source code of this version is available at
https://github.com/dotpcap/packetnet (commit `690707ce56d6e9c266daf6236c4f76ac5035334c`, as recorded in the package).

## Runtime dependency, not distributed

Live capture on Windows needs **Npcap** (https://npcap.com), installed by the user. AcsTool does not bundle or redistribute Npcap;
the free Npcap edition does not permit redistribution. On Linux, live capture uses the distribution's libpcap (BSD license).
Without either, live capture is disabled and the rest of the app works normally.
