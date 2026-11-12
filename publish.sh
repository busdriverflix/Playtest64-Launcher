#!/usr/bin/env bash
# Builds the launcher for Windows and Linux, each as one self-contained file: the .NET runtime and
# Avalonia go inside it, so a tester installs nothing first. Output: build/publish/<system>/
# Not compressed (EnableCompressionInSingleFile): a compressed file is high-entropy bytes that antivirus
# heuristics read as "packed", which is the commonest reason a plain .NET program is flagged. It is larger.
#   ./publish.sh            both
#   ./publish.sh linux-x64  one
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

for rid in ${@:-win-x64 linux-x64}; do
    echo "--- $rid"
    dotnet publish launcher/Launcher.csproj -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false \
        -p:PublishTrimmed=true -p:TrimMode=partial \
        -o "build/publish/$rid" -v q --nologo
    du -h build/publish/"$rid"/Playtest64Launcher* | cut -f1,2
done
