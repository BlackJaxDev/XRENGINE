import path from 'node:path';

export async function canvasGeometry(page, selector = 'canvas') {
    const element = page.locator(selector);
    await element.scrollIntoViewIfNeeded();
    return element.evaluate(canvas => {
        const bounds = canvas.getBoundingClientRect();
        return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height,
            bitmapWidth: canvas.width, bitmapHeight: canvas.height,
            devicePixelRatio, scrollX, scrollY };
    });
}

export async function inspectCanvas(page, before, after) {
    return page.evaluate(async ({ oldPng, newPng }) => {
        async function pixels(encoded) {
            const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
            const context = canvas.getContext('2d', { willReadFrequently: true });
            context.drawImage(bitmap, 0, 0);
            bitmap.close();
            return { width: canvas.width, height: canvas.height,
                data: context.getImageData(0, 0, canvas.width, canvas.height).data };
        }
        const current = await pixels(newPng);
        const prior = oldPng ? await pixels(oldPng) : null;
        // Element screenshots may include fractional CSS outline pixels. Those
        // belong to the page, not the authored canvas, and cannot prove a frame.
        const bounds = document.querySelector('#input-surface').getBoundingClientRect();
        const inset = Math.max(2, Math.ceil(2 * current.width / Math.max(1, bounds.width)));
        let colorful = 0, yellow = 0, green = 0, changed = 0;
        let colorfulLeft = 0, colorfulRight = 0, changedRight = 0;
        for (let index = 0; index < current.data.length; index += 4) {
            const pixel = index / 4, x = pixel % current.width, y = Math.floor(pixel / current.width);
            if (x < inset || y < inset || x >= current.width - inset || y >= current.height - inset) continue;
            const red = current.data[index], greenChannel = current.data[index + 1];
            const blue = current.data[index + 2];
            if (Math.max(red, greenChannel, blue) > 70 &&
                Math.max(red, greenChannel, blue) - Math.min(red, greenChannel, blue) > 35) {
                colorful++;
                if (x < current.width * 0.48) colorfulLeft++;
                if (x > current.width * 0.52) colorfulRight++;
            }
            if (red > 90 && greenChannel > 90 && blue * 1.5 < Math.min(red, greenChannel)) yellow++;
            if (greenChannel > 90 && greenChannel > red * 1.3 && greenChannel > blue * 1.3) green++;
            if (prior && prior.width === current.width && prior.height === current.height &&
                Math.abs(red - prior.data[index]) +
                Math.abs(greenChannel - prior.data[index + 1]) +
                Math.abs(blue - prior.data[index + 2]) > 45) {
                changed++;
                if (x > current.width * 0.52) changedRight++;
            }
        }
        return { width: current.width, height: current.height, colorful, colorfulLeft, colorfulRight, yellow, green, changedRight,
            changed: prior?.width === current.width && prior?.height === current.height ? changed : null };
    }, { oldPng: before?.toString('base64') ?? null, newPng: after.toString('base64') });
}

export async function capture(page, output, name, previous) {
    const image = await page.locator('#input-surface').screenshot({ path: path.join(output, `${name}.png`) });
    return { image, pixels: await inspectCanvas(page, previous, image) };
}

export async function captureUntil(page, config, name, previous, accepts, failure) {
    const deadline = Date.now() + Math.min(config.timeout, 10000);
    let latest;
    do {
        latest = await capture(page, config.output, name, previous);
        if (accepts(latest.pixels)) return latest;
        if (await page.locator('#status').getAttribute('data-state') === 'failed')
            throw new Error(`BrowserSmoke.EngineFrameFailed: ${await page.locator('#status').textContent()}`);
        await page.waitForTimeout(250);
    } while (Date.now() < deadline);
    throw new Error(`${failure} Last canvas summary: ${JSON.stringify(latest?.pixels)}`);
}
