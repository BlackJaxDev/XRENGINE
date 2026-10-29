/** Checks the saved neutral HDR panorama in a shipping-player canvas capture. */
export async function renderingParitySky(page, image) {
    const regions = await page.evaluate(async encoded => {
        const bytes = Uint8Array.from(atob(encoded), value => value.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext('2d');
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return [[0.1, 0.1], [0.9, 0.1], [0.1, 0.9], [0.9, 0.9]].map(([u, v]) => {
            const x = Math.floor(canvas.width * u), y = Math.floor(canvas.height * v);
            const pixels = context.getImageData(x - 2, y - 2, 5, 5).data;
            const channels = [0, 1, 2, 3].map(channel =>
                Array.from({ length: 25 }, (_, index) => pixels[index * 4 + channel]));
            return { x, y, upper: v < 0.5,
                min: channels.map(values => Math.min(...values)),
                max: channels.map(values => Math.max(...values)) };
        });
    }, image.toString('base64'));
    for (const region of regions) {
        // The fixed -Z camera sees the left column of the saved 2x2 panorama.
        // Its upper/lower HDR texels are 1/8 and 3/8. The camera saves exposure 1,
        // gamma 1 and Mobius transition 0.6. Neutral RGB cannot satisfy the
        // separate colorful foreground checks.
        const hdr = region.upper ? 1 / 8 : 3 / 8;
        const value = Math.round(255 * hdr * 1.6 / (hdr + 0.6));
        region.expected = [value, value, value, 255];
        for (let channel = 0; channel < 4; channel++)
            if (region.min[channel] < region.expected[channel] - 2 ||
                region.max[channel] > region.expected[channel] + 2)
                throw new Error(`BrowserSmoke.RenderingParitySky: saved HDR sky mismatch: ${JSON.stringify(region)}`);
    }
    return regions;
}
