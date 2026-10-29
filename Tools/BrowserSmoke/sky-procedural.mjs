import { skyCheck } from './sky-check.mjs';
import { clearSkyRadiance } from './sky-procedural-reference.mjs';

const timeOfDay = sampleCase => [0.25, 0, 0.5, 0.75][sampleCase] ?? 0.25;

// Evaluate the clear atmosphere in double precision from authored parameters
// and camera rays. This reference does not read shader text or rendered colors.
function reference(sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const dx = (2 * (x + 0.5) / width - 1) * width / height;
    const dy = 1 - 2 * (y + 0.5) / height;
    let ray = [dx, dy, -1];
    if (sampleCase === 4) ray = [dx, dy * Math.cos(Math.PI / 6) + 0.5, dy * 0.5 - Math.cos(Math.PI / 6)];
    if (sampleCase === 6) ray = [-1, dy, -dx];
    if (sampleCase === 7) ray = [1, dy, dx];
    return clearSkyRadiance(ray, timeOfDay(sampleCase), sampleCase === 8 ? 4 : 1);
}

/** Qualifies clear day/night sky values through the real component and shared pipeline. */
export async function skyProceduralCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-procedural', reportKey: 'skyProcedural', probe: 'sky-procedural', capturePrefix: 'engine-sky-procedural',
        scope: 'Clear atmosphere, dawn/dusk, night base, and off-disc celestial glow. Clouds, stars, discs, and light synchronization remain unqualified.',
        names: ['noon', 'sunrise', 'sunset', 'midnight', 'pitched', 'translated', 'yaw', 'sky-rotation', 'hdr-intensity', 'restored'],
        semantic: 'SkyboxDynamicProcedural', artifact: 'engine-skybox-dynamic-procedural',
        hdrAbsoluteTolerance: 0.00002,
        reference, foregroundCases: [0, 1, 2, 3, 7, 8, 9],
        expectedState: sampleCase => ({ mode: 'DynamicProcedural', intensity: sampleCase === 8 ? 4 : 1,
            rotation: sampleCase === 7 ? 90 : 0, timeOfDay: timeOfDay(sampleCase), cloudCoverage: 0, starIntensity: 0 }),
        replacesMaterial: () => false,
    });
}
