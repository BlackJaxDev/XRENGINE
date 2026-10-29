import { skyCheck } from './sky-check.mjs';

const top = [1.2, 0.15, 0.05], bottom = [0.05, 0.2, 0.9];

// Analytic camera rays and authored colors supply only expected values. The
// renderer receives no reference pixels or replacement shader from this module.
function reference(sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const dx = (2 * (x + 0.5) / width - 1) * width / height;
    const dy = 1 - 2 * (y + 0.5) / height;
    const length = Math.hypot(dx, dy, 1);
    const worldY = sampleCase === 2 ? (dy * Math.cos(Math.PI / 6) + Math.sin(Math.PI / 6)) / length : dy / length;
    const t = Math.max(0, Math.min(1, worldY * 0.5 + 0.5));
    const intensity = sampleCase === 4 ? 4 : 1;
    return [...top.map((value, channel) => (sampleCase === 5 ? value : bottom[channel] * (1 - t) + value * t) * intensity), 1];
}

/** Qualifies the engine gradient and solid sky through the shared pipeline. */
export async function skyGradientCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-gradient', reportKey: 'skyGradient', probe: 'sky', capturePrefix: 'engine-sky',
        scope: 'Gradient and solid engine sky; other projections and procedural controls remain unqualified.',
        names: ['gradient', 'translated', 'pitched', 'yawed', 'hdr-intensity', 'solid', 'restored'],
        semantic: 'SkyboxGradient', artifact: 'engine-skybox-gradient', reference,
        foregroundCases: [0, 4, 5, 6],
        expectedState: sampleCase => ({ mode: sampleCase === 5 ? 'SolidColor' : 'Gradient', intensity: sampleCase === 4 ? 4 : 1 }),
        replacesMaterial: (previousCase, sampleCase) => sampleCase === 5 || previousCase === 5,
    });
}
