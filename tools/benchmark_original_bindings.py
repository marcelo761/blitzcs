"""Measure real original-runtime bindings on Windows x86 or under Proton.

Build tests/benchmarks/OriginalRuntimeBindings first. This tool does not supply
an engine, substitute commands, change native DLLs or enable game tracing.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess

from probe_original_runtime import bitmap_metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--program', type=Path, default=Path(__file__).resolve().parents[1] /
                        'tests/benchmarks/OriginalRuntimeBindings/bin/Release/net10.0/OriginalRuntimeBindings.dll')
    parser.add_argument('--output', type=Path, required=True, help='New evidence directory')
    parser.add_argument('--proton', type=Path, help='Use Proton with STEAM_COMPAT_DATA_PATH and STEAM_COMPAT_CLIENT_INSTALL_PATH set')
    parser.add_argument('--require-zero-allocation', action='store_true')
    parser.add_argument('--correctness-only', action='store_true')
    parser.add_argument('--trace', default='', help='Selected commands, only with --correctness-only')
    args = parser.parse_args()
    if args.trace and not args.correctness_only:
        parser.error('Tracing must not contaminate performance measurements; use --correctness-only')
    for file in (args.host, args.runtime, args.program):
        if not file.is_file():
            parser.error('Missing input: ' + str(file))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)

    def windows(path):
        path = str(path.resolve())
        return 'Z:' + path.replace('/', '\\') if args.proton else path

    env = os.environ.copy()
    env.update(BLITZCS_ORIGINAL_NATIVE_ROOT=windows(args.runtime.parent),
               BLITZCS_BINDING_BENCHMARK_OUTPUT=windows(output/'samples.json'),
               BLITZCS_BINDING_REQUIRE_ZERO_ALLOC='1' if args.require_zero_allocation else '0',
               BLITZCS_BINDING_CORRECTNESS_ONLY='1' if args.correctness_only else '0',
               BLITZCS_ORIGINAL_TRACE=args.trace, WINEDEBUG='-all', PROTON_LOG='0')
    env.pop('BLITZCS_ORIGINAL_TRACE_STACK', None)
    command = ([str(args.proton.resolve()), 'run'] if args.proton else []) + [
        str(args.host.resolve()), '--log', windows(output/'host.log'), '--run',
        windows(args.program), windows(args.runtime), '--capture', windows(output/'frame.bmp')]
    with (output/'launcher.log').open('w') as log:
        process = subprocess.run(command, env=env, cwd=output, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    if process.returncode:
        raise SystemExit(f'Host failed ({process.returncode}); inspect {output / "host.log"}')
    samples = json.loads((output/'samples.json').read_text())
    summary = {name: dict(
        millisecondsMedian=statistics.median(s['milliseconds'] for s in samples if s['name'] == name),
        millisecondsRange=[min(s['milliseconds'] for s in samples if s['name'] == name),
                           max(s['milliseconds'] for s in samples if s['name'] == name)],
        allocatedBytes=sorted({s['allocatedBytes'] for s in samples if s['name'] == name}))
        for name in sorted({s['name'] for s in samples})}
    image = bitmap_metrics(output/'frame.bmp')
    if image['nonBlackPixels'] == 0:
        raise SystemExit('Native capture contains no rendered pixels')
    evidence = dict(exitCode=process.returncode, measurements=summary, rendered=image,
                    sha256={str(p.name): hashlib.sha256(p.read_bytes()).hexdigest()
                            for p in (args.runtime, args.host, args.host.parent/'BlitzCS.OriginalRuntime.Bridge.dll')})
    (output/'result.json').write_text(json.dumps(evidence, indent=2)+'\n')
    print(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    main()
