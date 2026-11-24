#!/usr/bin/env bash
set -euo pipefail

# Only the checked-in logger inputs enter the build. Each SDK container owns
# its intermediates, adapter and completion event, even on a shared checkout.
work=$(mktemp -d)
scripts=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
mkdir "$work/runner"
cp "$scripts/../../Directory.Build.props" "$scripts/../../Directory.Packages.props" "$work/"
cp "$scripts"/runner/{ContractCompletionLogger.cs,WebApi.Contracts.TestLogger.csproj,packages.lock.json} "$work/runner/"
dotnet build "$work/runner/WebApi.Contracts.TestLogger.csproj" --configuration Release \
  --output "$work/adapter" -p:RestoreLockedMode=true -p:RestoreSources="$work/runner"

export WEBAPI_CONTRACT_RESULT="$work/completion.json"
dotnet test --test-adapter-path "$work/adapter" --logger WebApiContractCompletion "$@"
if [ -f "$WEBAPI_CONTRACT_RESULT" ]; then
  cat "$WEBAPI_CONTRACT_RESULT" > /contract-completion.json
fi
