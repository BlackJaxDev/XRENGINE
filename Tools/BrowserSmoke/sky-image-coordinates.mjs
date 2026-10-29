/** Maps pixel-center camera rays into the authored sky image coordinates. */
export function skyImageCoordinates(projection, sampleCase, x, y, width, height) {
    const dx = (2 * (x + 0.5) / width - 1) * width / height;
    const dy = 1 - 2 * (y + 0.5) / height;
    let direction = [dx, dy, -1];
    switch (sampleCase) {
        case 1: case 7: direction = [1, dy, dx]; break;
        case 2: direction = [-1, dy, -dx]; break;
        case 3: direction = [dx, 1, dy]; break;
        case 4: direction = [dx, -1, -dy]; break;
        case 5: direction = [-dx, dy, 1]; break;
    }
    const [a, b, c] = direction;
    let u, v;
    if (projection === 'Equirectangular') {
        u = Math.atan2(c, a) / (2 * Math.PI) + 0.5;
        u -= Math.floor(u);
        v = 0.5 - Math.asin(b / Math.hypot(a, b, c)) / Math.PI;
    } else {
        const length = Math.abs(a) + Math.abs(b) + Math.abs(c);
        const horizontal = a / length, vertical = c / length;
        u = horizontal; v = vertical;
        if (b < 0) {
            u = (1 - Math.abs(vertical)) * (horizontal >= 0 ? 1 : -1);
            v = (1 - Math.abs(horizontal)) * (vertical >= 0 ? 1 : -1);
        }
        u = (u + 1) / 2; v = (v + 1) / 2;
    }
    return [u, v];
}
