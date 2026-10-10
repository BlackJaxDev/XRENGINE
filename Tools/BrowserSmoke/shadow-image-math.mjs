/** Projection constants come from the two SHA-pinned worlds, not from sampled successful pixels. */
export function shadowReceiverRegions(width, height, canvasWidth, canvasHeight) {
    const scaleY = height / (2 * Math.tan(48 * Math.PI / 360));
    const scaleX = width / (2 * Math.tan(48 * Math.PI / 360) * (canvasWidth / canvasHeight));
    const project = (x, y, z) => [width / 2 + x * scaleX / (6.5 - z), height / 2 - y * scaleY / (6.5 - z)];
    const box = (scale, translation, margin) => {
        const topLeft = project(-1.95 * scale + translation[0], 1 * scale + translation[1], translation[2]);
        const bottomRight = project(-0.75 * scale + translation[0], -1 * scale + translation[1], translation[2]);
        return { left: topLeft[0] - margin, top: topLeft[1] - margin,
            right: bottomRight[0] + margin, bottom: bottomRight[1] + margin };
    };
    // Four screenshot pixels exclude raster edges, AO fringes, and CSS outline rounding.
    return { receiver: box(1, [0, 0, 0], -4),
        occluders: [box(0.35, [-0.8775, 0, 1], 4), box(0.35, [0.23916667, 0.5, 1], 4)],
        surfaces: [box(1, [0, 0, 0], 0), box(0.35, [-0.8775, 0, 1], 0), box(0.35, [0.23916667, 0.5, 1], 0)] };
}

/** Checks the full projected scene and its uniform exterior. No successful sample pixels define this mask. */
export function inspectShadowCaptureImage({ data, width, height }, regions) {
    const inside = (x, y, box, margin = 0) => x >= box.left - margin && x < box.right + margin &&
        y >= box.top - margin && y < box.bottom + margin;
    const surfaces = regions.surfaces;
    const expected = { left: Math.min(...surfaces.map(box => box.left)), top: Math.min(...surfaces.map(box => box.top)),
        right: Math.max(...surfaces.map(box => box.right)), bottom: Math.max(...surfaces.map(box => box.bottom)) };
    const histogram = Array.from({ length: 3 }, () => new Uint32Array(256));
    let exteriorPixels = 0;
    // Exclude four edge pixels for fractional CSS outlines. The remaining exterior is authored empty space.
    for (let y = 4; y < height - 4; y++) for (let x = 4; x < width - 4; x++) {
        if (surfaces.some(box => inside(x + 0.5, y + 0.5, box, 4))) continue;
        exteriorPixels++;
        const offset = 4 * (y * width + x);
        for (let channel = 0; channel < 3; channel++) histogram[channel][data[offset + channel]]++;
    }
    // Use the exterior median so this check does not assume an exact tonemapped clear-color value.
    const background = histogram.map(values => {
        let sum = 0;
        for (let value = 0; value < values.length; value++) {
            sum += values[value];
            if (sum >= exteriorPixels / 2) return value;
        }
        return 0;
    });
    const observed = { left: width, top: height, right: 0, bottom: 0 };
    let foregroundPixels = 0, exteriorMismatchPixels = 0;
    for (let y = 4; y < height - 4; y++) for (let x = 4; x < width - 4; x++) {
        const offset = 4 * (y * width + x);
        if (Math.max(Math.abs(background[0] - data[offset]), Math.abs(background[1] - data[offset + 1]),
            Math.abs(background[2] - data[offset + 2])) <= 8) continue;
        foregroundPixels++;
        observed.left = Math.min(observed.left, x);
        observed.top = Math.min(observed.top, y);
        observed.right = Math.max(observed.right, x + 1);
        observed.bottom = Math.max(observed.bottom, y + 1);
        if (!surfaces.some(box => inside(x + 0.5, y + 0.5, box, 4))) exteriorMismatchPixels++;
    }
    const maximumEdgeError = Math.max(...Object.keys(expected).map(edge => Math.abs(expected[edge] - observed[edge])));
    const exteriorMismatchFraction = exteriorPixels ? exteriorMismatchPixels / exteriorPixels : 1;
    return { accepted: exteriorPixels > 1000 && foregroundPixels > 100 &&
            exteriorMismatchFraction <= 0.001 && maximumEdgeError <= 3,
        expected, observed, background, foregroundPixels, exteriorPixels, exteriorMismatchPixels,
        exteriorMismatchFraction, maximumEdgeError, edgeTolerancePixels: 3, backgroundToleranceRgb: 8 };
}

export function inspectShadowPixels(current, canvasCssWidth) {
    const prior = null;
    // Element screenshots may include fractional CSS outline pixels. Those
    // belong to the page, not the authored canvas, and cannot prove a frame.
    const inset = Math.max(2, Math.ceil(2 * current.width / Math.max(1, canvasCssWidth)));
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
}

export function compareShadowReceiverPixels(baseline, current, regions) {
    const inside = (x, y, box) => x >= box.left && x < box.right && y >= box.top && y < box.bottom;
    let receiverPixels = 0, darkenedPixels = 0, brightenedPixels = 0, difference = 0;
    let offColorful = 0, onColorful = 0;
    const colorful = (data, offset) => Math.max(...data.slice(offset, offset + 3)) > 70 &&
        Math.max(...data.slice(offset, offset + 3)) - Math.min(...data.slice(offset, offset + 3)) > 35;
    for (let y = 0; y < current.height; y++) for (let x = 0; x < current.width; x++) {
        if (!inside(x + 0.5, y + 0.5, regions.receiver) ||
            regions.occluders.some(box => inside(x + 0.5, y + 0.5, box))) continue;
        const offset = 4 * (y * current.width + x);
        const delta = (baseline.data[offset] + baseline.data[offset + 1] + baseline.data[offset + 2] -
            current.data[offset] - current.data[offset + 1] - current.data[offset + 2]) / 3;
        receiverPixels++;
        difference += delta;
        if (delta > 5) darkenedPixels++;
        if (delta < -5) brightenedPixels++;
        if (colorful(baseline.data, offset)) offColorful++;
        if (colorful(current.data, offset)) onColorful++;
    }
    return { regions, receiverPixels, darkenedPixels, brightenedPixels, offColorful, onColorful,
        meanRgbDarkening: receiverPixels ? difference / receiverPixels : null,
        darkenedFraction: receiverPixels ? darkenedPixels / receiverPixels : 0, thresholdRgb: 5 };
}
