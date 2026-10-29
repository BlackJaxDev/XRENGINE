import { skyCheck } from './sky-check.mjs';
import { clearSkyRadiance } from './sky-procedural-reference.mjs';

const timeOfDay = sampleCase => [1, 6, 8].includes(sampleCase) ? 0 : sampleCase === 2 ? 0.5 : sampleCase === 3 ? 0.98 : 0.25;
const discSize = sampleCase => sampleCase === 4 ? 0.998 : sampleCase === 5 ? 0.9999 : 0.9994;
const normalize = vector => vector.map(value => value / Math.hypot(...vector));

function reference(sampleCase, x, y, width, height) {
    const time = Math.fround(timeOfDay(sampleCase));
    const halfExtent = Math.tan((sampleCase === 8 ? 90 : 10) * Math.PI / 360);
    const dx = (2 * (x + 0.5) / width - 1) * width / height * halfExtent;
    const dy = (1 - 2 * (y + 0.5) / height) * halfExtent;
    let ray;
    if (sampleCase === 8) {
        // Intersect the camera ray with the authored foreground plane at z=-2.
        // Sky rotation applies only after the depth-occluding geometry.
        if (Math.abs(dx * 2) < 0.75 && Math.abs(dy * 2) < 0.75)
            return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
        ray = [1, dy, dx];
    } else {
        const angle = time * Math.PI * 2;
        const sun = normalize([Math.cos(angle), Math.sin(angle), 0.18]);
        const right = normalize([-sun[2], 0, sun[0]]);
        const up = [-right[2] * sun[1], right[2] * sun[0] - right[0] * sun[2], right[0] * sun[1]];
        ray = sun.map((value, index) => value + right[index] * dx + up[index] * dy);
    }
    return clearSkyRadiance(ray, time, sampleCase === 6 ? 0.01 : 1, discSize(sampleCase));
}

/** Qualifies sun-disc radiance, size, limb falloff, and foreground depth. */
export async function skySunCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-sun', reportKey: 'skySun', probe: 'sky-sun', capturePrefix: 'engine-sky-sun',
        scope: 'Clear sun disc, limb darkening, aureole, day-cycle color, size and HDR intensity. Moon surface, clouds, stars, and lighting synchronization remain unqualified.',
        names: ['noon', 'sunrise', 'sunset', 'below-horizon', 'wide-disc', 'narrow-disc', 'dim-dawn', 'translated', 'foreground', 'restored'],
        semantic: 'SkyboxDynamicProcedural', artifact: 'engine-skybox-dynamic-procedural',
        positions: [[0.125, 0.125], [0.875, 0.125], [0.125, 0.875], [0.875, 0.875], [0.5, 0.5],
            [0.55, 0.5], [0.6, 0.5], [0.7, 0.5], [0.85, 0.5], [0.9, 0.5]],
        warmupCase: 8, resizeCases: [8, 9], hdrAbsoluteTolerance: 0.00002, reference, foregroundCases: [],
        expectedState: sampleCase => ({ mode: 'DynamicProcedural', intensity: sampleCase === 6 ? 0.01 : 1,
            rotation: sampleCase === 8 ? 90 : 0, timeOfDay: timeOfDay(sampleCase), cloudCoverage: 0, starIntensity: 0,
            sunDiscSize: discSize(sampleCase), verticalFieldOfView: sampleCase === 8 ? 90 : 10 }),
        replacesMaterial: () => false,
    });
}
