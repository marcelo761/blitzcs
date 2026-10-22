#!/usr/bin/env python3
"""Compare the captured FMOD oracle with actual compiled BASIC/SDL operations, normalizing opaque identities."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess


def blocks(text):
    result, key = {}, None
    for line in text.splitlines():
        if re.fullmatch(r'-?\d+(?:\.\d+)?', line):
            if key is not None: result[key].append(float(line) if '.' in line else int(line))
        else:
            key = line
            if key in result: raise ValueError('Duplicate result label: ' + key)
            result[key] = []
    for label in ('version', 'output'):
        result.pop(label, None)
    for label in ('open-length-minus-one', 'open-loop-valid'):
        result[label][0] = int(result[label][0] != 0)
    result['play-free'][0] = int(result['play-free'][0] not in (0, -1))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--report', type=Path, default=Path('artifacts/m15-fmod-comparison.json'))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    evidence = root / 'docs/evidence/m15-fmod'
    original = json.loads((evidence / 'probe.json').read_text())
    source = (evidence / 'probe.bb').read_text()
    source = '\n'.join(source.splitlines()[2:])
    aliases = {'Init':'Init','Error':'GetError','Open':'Stream_Open','Play':'Stream_Play','IsPlaying':'IsPlaying',
               'SetVolume':'SetVolume','GetVolume':'GetVolume','SetPaused':'SetPaused','GetPaused':'GetPaused',
               'SetFrequency':'SetFrequency','GetFrequency':'GetFrequency','SetPan':'SetPan',
               'StopChannel':'StopSound','StopStream':'Stream_Stop','CloseStream':'Stream_Close','Close':'Close'}
    source = re.sub(r'M15([A-Za-z]+)', lambda match: 'FSOUND_' + aliases[match[1]], source)
    source = re.sub(r'"Z:[^"]*tone.wav"', lambda _: '"' + (evidence / 'tone.wav').as_posix() + '"', source)
    source = re.sub(r'"Z:[^"]*missing.wav"', lambda _: '"' + (root / 'artifacts/m15-missing.wav').as_posix() + '"', source)
    source = source.replace('Delay 3300', 'CS::OracleWait.Wait(3300)')
    source = '<?cs module\npublic static class OracleWait { public static int Wait(int ms) { System.Threading.Thread.Sleep(ms); return 0; } }\n?>\n' + source
    work = root / 'artifacts/m15-fmod-local'
    work.mkdir(parents=True, exist_ok=True)
    (work / 'probe.bb').write_text(source)
    subprocess.run([args.dotnet, str(root / 'src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll'),
                    'build', str(work / 'probe.bb'), '-o', str(work / 'probe.dll'), '--externals',
                    str(root / 'samples/Audio/scp.json')], capture_output=True, text=True, check=True, timeout=30)
    run = subprocess.run([args.dotnet, str(work / 'probe.dll')], capture_output=True, text=True, check=True,
                         timeout=20, env={**os.environ, 'SDL_AUDIODRIVER':'dummy'})
    expected, actual = blocks(original['capture']['stdout']), blocks(run.stdout)
    if expected.keys() != actual.keys(): raise ValueError('Oracle/local case labels differ')
    known = {
        'channel-zero-is-playing': ([0,0],[0,14]),
        'channel-zero-volume': ([1,0],[0,14]), 'channel-zero-pause': ([1,0],[0,14]), 'channel-zero-stop': ([1,0],[0,14]),
        'open-loop-valid': ([1,14],[1,0]), 'open-error-read-again': ([14],[0]),
        'frequency-zero': ([1,0,0],[0,11025,0]),
        'frequency-minus-one': ([1,-1,0],[0,11025,0])
    }
    rows = [{'case':key,'original':value,'local':actual[key],
             'status':'match' if value == actual[key] else 'documented_divergence'
             if known.get(key) == (value,actual[key]) else 'mismatch'} for key,value in expected.items()]
    result = {'original_capture_sha256':hashlib.sha256((evidence/'probe.json').read_bytes()).hexdigest(),
              'local_source_sha256':hashlib.sha256(source.encode()).hexdigest(),'source':str(work/'probe.bb'),
              'local_profile':'SDL3 dummy; native mono WAV; no physical-audio claim','stdout':run.stdout,
              'normalization':'Only non-null asset handles and successful channel identities are normalized; return/error pairs remain intact',
              'comparisons':rows,'totals':{status:sum(row['status']==status for row in rows)
                                         for status in ('match','documented_divergence','mismatch')}}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['totals']))
    return int(any(row['status']=='mismatch' for row in rows))


if __name__ == '__main__':
    raise SystemExit(main())
