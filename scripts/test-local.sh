#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dotnet restore OneLag.slnx
dotnet format OneLag.slnx --verify-no-changes --no-restore
dotnet build OneLag.slnx --configuration Release --no-restore
dotnet test OneLag.slnx --configuration Release --no-build

publish_dir="tmp/local-validation/publish/win-x64"
dotnet publish src/OneLag.Cli/OneLag.Cli.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version=0.1.0-local \
  --output "$publish_dir"

test -f "$publish_dir/onelag.exe"
echo "Local validation passed. Windows publish artifact: $publish_dir/onelag.exe"
