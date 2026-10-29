import { skyCheck } from './sky-check.mjs';
import { clearSkyRadiance } from './sky-procedural-reference.mjs';

const timeOfDay = sampleCase => [0.75, 0.6, 0.9, 0.5, 0, 0.25][sampleCase] ?? 0.75;
const discSize = sampleCase => sampleCase === 6 ? 0.998 : sampleCase === 7 ? 0.9999 : 0.99965;
const normalize = values => values.map(value => value / Math.hypot(...values));

function reference(sampleCase, x, y, width, height) {
    const time = Math.fround(timeOfDay(sampleCase));
    const angle = time * Math.PI * 2 + Math.PI + 0.25 * Math.sin(time * Math.PI * 2 * 0.3);
    let forward = normalize([Math.cos(angle), Math.sin(angle), -0.22]);
    if (sampleCase === 10) forward = [forward[2], forward[1], -forward[0]];
    const right = normalize([-forward[2], 0, forward[0]]);
    const up = [-right[2] * forward[1], right[2] * forward[0] - right[0] * forward[2], right[0] * forward[1]];
    const scale = Math.tan(10 * Math.PI / 360);
    const dx = (2 * (x + 0.5) / width - 1) * width / height * scale;
    const dy = (1 - 2 * (y + 0.5) / height) * scale;
    let ray = forward.map((value, axis) => value + right[axis] * dx + up[axis] * dy);
    if (sampleCase === 10) ray = [-ray[2], ray[1], ray[0]];
    return clearSkyRadiance(ray, time, sampleCase === 8 ? 4 : 1, null, discSize(sampleCase));
}

/** Qualifies the authored moon surface, corona, size, and day/night weighting. */
export async function skyMoonCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-moon', reportKey: 'skyMoon', probe: 'sky-moon', capturePrefix: 'engine-sky-moon',
        scope: 'Analytically bounded moon surface and exact off-disc corona/daytime values. Maria/crater pattern parity remains open. These orbit/size cases do not exercise nonzero phase lighting or soft-edge falloff. Cross-adapter noise parity remains unqualified.',
        names: ['midnight', 'evening', 'predawn', 'sunset', 'sunrise', 'daytime', 'wide-disc', 'narrow-disc',
            'hdr-intensity', 'translated', 'sky-rotation', 'restored'],
        semantic: 'SkyboxDynamicProcedural', artifact: 'engine-skybox-dynamic-procedural',
        positions: [[0.125, 0.125], [0.875, 0.125], [0.125, 0.875], [0.875, 0.875], [0.5, 0.5],
            [0.55, 0.5], [0.6, 0.5], [0.65, 0.5], [0.7, 0.5], [0.8, 0.5], [0.9, 0.5]],
        reference, allowReferenceBounds: true, foregroundCases: [],
        expectedState: sampleCase => ({ mode: 'DynamicProcedural', intensity: sampleCase === 8 ? 4 : 1,
            rotation: sampleCase === 10 ? 90 : 0, timeOfDay: timeOfDay(sampleCase), cloudCoverage: 0, starIntensity: 0,
            moonDiscSize: discSize(sampleCase), verticalFieldOfView: 10 }),
        replacesMaterial: () => false,
    });
}
