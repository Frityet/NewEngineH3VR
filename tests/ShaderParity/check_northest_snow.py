#!/usr/bin/env python3
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow"]
# ///
"""Verify Northest snow shadow contrast in pose-matched live stereo captures."""
import argparse
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image

SCENE = 'TakeAndHold_WinterWasteland'
LUMA = np.array([.2126, .7152, .0722])


# Pixel-space boxes (left, top, right, bottom), extracted from the
# deterministic ShaderParity Northest Dakota route at 1440x1600 per eye.
REGIONS = {
    0: {
        'take': ('cargo container',
                 ((730, 954, 768, 976), (688, 954, 717, 976)),
                 ((726, 954, 765, 976), (685, 954, 714, 976))),
    },
    1: {
        'take': ('cargo container',
                 ((691, 941, 736, 963), (790, 941, 835, 963)),
                 ((685, 941, 730, 963), (784, 941, 829, 963))),
        'hold': ('tree',
                 ((723, 1069, 755, 1088), (720, 1117, 752, 1136)),
                 ((720, 1069, 749, 1088), (717, 1117, 749, 1136))),
    },
}


def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def pose(path):
    xyz_quaternion = np.array([float(x) for x in path.read_text().split()])
    if xyz_quaternion.shape != (7,) or not np.isfinite(xyz_quaternion).all():
        raise ValueError(f'Invalid camera pose in {path}')
    return xyz_quaternion[:3], xyz_quaternion[3:] / np.linalg.norm(xyz_quaternion[3:])


def luma(image, box):
    if not (0 <= box[0] < box[2] <= image.width and 0 <= box[1] < box[3] <= image.height):
        raise ValueError(f'ROI {box} lies outside the {image.size} frame')
    return float(np.asarray(image.crop(box).convert('RGB')).mean(axis=(0, 1)) @ LUMA)


def verify(args):
    regions = REGIONS[args.seed]
    phases = args.phase or list(regions)
    unsupported = set(phases) - regions.keys()
    if unsupported:
        raise ValueError(f'No validated reference ROI for seed {args.seed}, phases {sorted(unsupported)}')
    cases = [(phase, eye, regions[phase][0], regions[phase][index + 1])
             for phase in phases for index, eye in enumerate(('left', 'right'))]
    results = []
    for phase, eye, caster, (shadow, lit) in cases:
        stems = [args.reference / f'{SCENE}-{phase}-{args.reference_mode}',
                 args.capture / f'{SCENE}-{phase}-{args.mode}']
        poses = [Path(str(s) + '-pose.txt') for s in stems]
        images = [Path(str(s) + f'-{eye}.png') for s in stems]
        (p0, q0), (p1, q1) = [pose(p) for p in poses]
        distance = float(np.linalg.norm(p0 - p1))
        angle = math.degrees(2 * math.acos(min(1., abs(float(q0 @ q1)))))
        metrics, dimensions = [], []
        for path in images:
            with Image.open(path) as img:
                dimensions.append(img.size)
                s, l = luma(img, shadow), luma(img, lit)
                metrics.append({'shadow': s, 'lit': l, 'deficit': l - s, 'imageSha256': sha(path)})
        ref, candidate = metrics
        # r01 Hold has no dark footprint in the alignment reference. Its ROI
        # measures different lit surfaces, so use the r02 Hold tree instead.
        eligible = ref['deficit'] >= 30
        same_geometry = distance <= .015 and angle <= .12 and all(x == (1440, 1600) for x in dimensions)
        retained = candidate['deficit'] / ref['deficit'] if eligible else None
        passed = eligible and same_geometry and retained >= .5 and abs(candidate['lit'] - ref['lit']) <= 25
        results.append({'phase': phase, 'eye': eye, 'caster': caster,
                        'footprint': shadow, 'litControl': lit, 'dimensions': dimensions,
                        'poseDeltaMeters': distance, 'poseDeltaDegrees': angle,
                        'reference': ref, 'candidate': candidate, 'contrastFraction': retained,
                        'status': 'pass' if passed else 'fail' if eligible else 'invalid-reference'})
    passed = all(c['status'] == 'pass' for c in results)
    return {'passed': passed, 'seed': args.seed, 'mode': args.mode, 'cases': results,
            'limits': {'minReferenceDeficit': 30, 'minContrastFraction': .5,
                       'maxLitControlChange': 25, 'maxPoseMeters': .015, 'maxPoseDegrees': .12},
            'referenceMode': args.reference_mode}


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('reference', type=Path, help='Directory of matched Built-in stereo eye frames and pose files')
    p.add_argument('capture', type=Path, help='Directory of candidate stereo eye frames and pose files')
    p.add_argument('--seed', choices=REGIONS, type=int, default=0)
    p.add_argument('--reference-mode', default='stock', help='Mode suffix in the reference filenames')
    p.add_argument('--mode', choices=['stock', 'balanced', 'quality'], required=True)
    p.add_argument('--phase', choices=['take', 'hold'], action='append', default=[])
    p.add_argument('--out', type=Path, required=True)
    args = p.parse_args()
    result = verify(args)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2) + '\n')
    raise SystemExit(0 if result['passed'] else 1)
