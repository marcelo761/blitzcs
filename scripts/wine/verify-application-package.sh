#!/usr/bin/env bash
# Build and validate Windows application packages, then run a console, asset, file-I/O,
# and kernel32 ABI fixture under the configured Proton/Wine environment.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)

# Keep Wine configuration scoped to this script and its child processes.
. "$here/env.sh"
export BLITZCS_TEST_PUBLISH=1
export BLITZCS_TEST_WINE=1

dotnet test "$root/tests/BlitzCS.CLI.Tests/BlitzCS.CLI.Tests.csproj" \
  --filter FullyQualifiedName~ApplicationPackageTests \
  --logger 'console;verbosity=minimal'
