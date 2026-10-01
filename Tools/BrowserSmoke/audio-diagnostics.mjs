/** Executes the shipped PCM scheduler against a real browser audio renderer without output-device access. */
export async function runOfflineAudioProbe({ moduleUrl, budgetMs }) {
    const { WebAudioStream } = await import(moduleUrl);
    if (!globalThis.OfflineAudioContext) throw new Error('BrowserSmoke.OfflineAudioUnavailable.');
    const results = [];
    for (const rate of [1, 2]) {
        const context = new OfflineAudioContext(1, 9600, 48000);
        const first = context.createBuffer(1, 1920, 48000);
        const second = context.createBuffer(1, 2880, 48000);
        first.getChannelData(0).fill(0.25);
        second.getChannelData(0).fill(-0.375);
        const stream = new WebAudioStream(context, context.destination);
        let timer;
        try {
            stream.enqueue([1, 2], id => id === 1 ? first : second);
            stream.play(rate);
            const rendered = await Promise.race([context.startRendering(), new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('BrowserSmoke.OfflineAudioTimeout.')), budgetMs);
            })]);
            stream.refresh();
            if (stream.playing || stream.processed !== 2)
                throw new Error('BrowserSmoke.OfflineAudioLifecycle: completed buffers were not processed.');
            const samples = rendered.getChannelData(0);
            const boundary = 1920 / rate, end = 4800 / rate;
            let maximumError = 0;
            for (let index = 0; index < samples.length; index++) {
                const expected = index < boundary ? 0.25 : index < end ? -0.375 : 0;
                maximumError = Math.max(maximumError, Math.abs(samples[index] - expected));
            }
            if (maximumError > 0.000001)
                throw new Error(`BrowserSmoke.OfflineAudioSamples: rate ${rate} maximum error ${maximumError}.`);
            const unqueued = new Int32Array(2);
            if (stream.unqueue(unqueued) !== 2 || unqueued[0] !== 1 || unqueued[1] !== 2 || stream.count !== 0)
                throw new Error('BrowserSmoke.OfflineAudioOrder: completed buffers did not preserve queue order.');
            results.push({ rate, samples: samples.length, maximumError, buffersProcessed: 2 });
        } finally { clearTimeout(timer); stream.dispose(); }
    }
    return { scope: 'Real offline browser PCM scheduling and samples; no output-device, gesture, spatialization, or codec qualification.', results };
}
