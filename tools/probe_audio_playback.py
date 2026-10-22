#!/usr/bin/env python3
"""Run real SDL playback and optionally inspect PCM written by SDL's native disk driver."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def run(command, **kwargs):
    return subprocess.run(command, text=True, capture_output=True, timeout=30, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--asset', type=Path, required=True)
    parser.add_argument('--report', type=Path, default=Path('artifacts/m15-playback.json'))
    parser.add_argument('--capture-disk', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    asset = args.asset.resolve()
    output = root / 'artifacts/m15-playback/playback.dll'
    run([args.dotnet, str(root / 'src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll'),
         'build', str(root / 'samples/Audio/playback.bb'), '-o', str(output),
         '--externals', str(root / 'samples/Audio/scp.json')])
    profiles = []

    def playback(driver, extra=None):
        env = dict(os.environ, BLITZCS_AUDIO_ASSET=str(asset))
        if driver:
            env['SDL_AUDIODRIVER'] = driver
            env['SDL_AUDIO_DRIVER'] = driver
        else:
            env.pop('SDL_AUDIODRIVER', None)
            env.pop('SDL_AUDIO_DRIVER', None)
        env.update(extra or {})
        result = run([args.dotnet, str(output)], env=env)
        snapshots = [json.loads(line) for line in result.stdout.splitlines()]
        if len(snapshots) != 2 or snapshots[0]['NonSilentFrames'] <= 0 or snapshots[0]['PcmEnergy'] <= 0:
            raise ValueError('No non-silent PCM submitted to SDL')
        if snapshots[0]['Assets'] != 2 or snapshots[0]['Channels'] != 2:
            raise ValueError('Concurrent sample/stream playback did not remain active')
        if snapshots[-1]['Assets'] or snapshots[-1]['Channels']:
            raise ValueError('Audio resources remained after close')
        profiles.append({'driver': driver or 'host default', 'exit_code': result.returncode,
                         'snapshots': snapshots, 'stderr': result.stderr})

    playback('dummy')
    playback(None)
    captured = None
    if args.capture_disk:
        import array, math
        pcm = root / 'artifacts/m15-playback/post-sdl.f32le'
        if pcm.exists(): pcm.unlink()
        playback('disk', {'SDL_AUDIO_DRIVER':'disk', 'SDL_AUDIO_DISK_OUTPUT_FILE':str(pcm)})
        samples = array.array('f')
        samples.frombytes(pcm.read_bytes())
        if os.sys.byteorder != 'little': samples.byteswap()
        if not samples or any(not math.isfinite(value) for value in samples):
            raise ValueError('SDL disk PCM is empty or non-finite')
        rms = math.sqrt(sum(value * value for value in samples) / len(samples))
        peak = max(abs(value) for value in samples)
        if rms <= .0001: raise ValueError('SDL disk PCM is silent')
        captured = {'path':str(pcm.relative_to(root)), 'sha256':hashlib.sha256(pcm.read_bytes()).hexdigest(),
                    'format':'F32LE', 'sample_rate':44100, 'channels':2, 'frames':len(samples)//2,
                    'nonzero_samples':sum(value != 0 for value in samples), 'rms':rms, 'peak':peak,
                    'source':'actual SDL3 disk audio driver after the bound native stream; no hardware/loopback claim'}
    result = {'asset': str(asset), 'asset_sha256': hashlib.sha256(asset.read_bytes()).hexdigest(),
              'profiles': profiles, 'post_sdl_pcm': captured,
              'physical_audibility': 'not confirmed by a listener; default device submission and SDL disk PCM capture are separate evidence'}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'profiles': len(profiles), 'post_sdl_pcm': bool(captured)}))


if __name__ == '__main__':
    main()
