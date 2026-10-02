const inverse = 0xD001;
const inverseClamped = 0xD002;
const linear = 0xD003;
const linearClamped = 0xD004;
const exponent = 0xD005;
const exponentClamped = 0xD006;

export function validateDistanceModel(model) {
    if (model !== 0 && model !== inverse && model !== inverseClamped && model !== linear
        && model !== linearClamped && model !== exponent && model !== exponentClamped)
        throw new Error('WebAudio.DistanceModelUnsupported: unknown authored distance model.');
}

/** Builds the listener basis without allocating in pose updates. */
export function setListenerBasis(owner, fx, fy, fz, ux, uy, uz) {
    const forwardLength = Math.hypot(fx, fy, fz);
    if (!Number.isFinite(forwardLength) || forwardLength < 1e-8)
        throw new Error('WebAudio.ListenerOrientationInvalid: forward must be nonzero and finite.');
    fx /= forwardLength; fy /= forwardLength; fz /= forwardLength;
    let rx = fy * uz - fz * uy, ry = fz * ux - fx * uz, rz = fx * uy - fy * ux;
    const rightLength = Math.hypot(rx, ry, rz);
    if (!Number.isFinite(rightLength) || rightLength < 1e-8)
        throw new Error('WebAudio.ListenerOrientationInvalid: forward and up must be independent finite vectors.');
    rx /= rightLength; ry /= rightLength; rz /= rightLength;
    const right = owner.listenerRight, forward = owner.listenerForward, up = owner.listenerUp;
    right[0] = rx; right[1] = ry; right[2] = rz;
    forward[0] = fx; forward[1] = fy; forward[2] = fz;
    up[0] = ry * fz - rz * fy; up[1] = rz * fx - rx * fz; up[2] = rx * fy - ry * fx;
}

function distanceGain(model, distance, source) {
    if (model === 0 || source.rolloff === 0) return 1;
    const reference = source.referenceDistance, maximum = source.maxDistance;
    if (model === inverseClamped || model === linearClamped || model === exponentClamped)
        distance = Math.min(maximum, Math.max(reference, distance));
    let gain;
    if (model === inverse || model === inverseClamped)
        gain = reference / (reference + source.rolloff * (distance - reference));
    else if (model === linear || model === linearClamped)
        gain = 1 - source.rolloff * (Math.min(distance, maximum) - reference) / (maximum - reference);
    else
        gain = Math.pow(distance / reference, -source.rolloff);
    // The shared native model leaves a source unattenuated when its formula is undefined.
    return Number.isFinite(gain) ? gain : 1;
}

function relativeVector(owner, input, output) {
    const right = owner.listenerRight, up = owner.listenerUp, forward = owner.listenerForward;
    for (let axis = 0; axis < 3; axis++)
        output[axis] = right[axis] * input[0] + up[axis] * input[1] - forward[axis] * input[2];
}

/** Preserves authored attenuation/cone/gain order; the Panner supplies direction only. */
export function updateSpatialSource(owner, source) {
    const position = source.worldPosition, direction = source.worldDirection;
    if (source.relative) {
        relativeVector(owner, source.position, position);
        relativeVector(owner, source.direction, direction);
        for (let axis = 0; axis < 3; axis++) position[axis] += owner.listenerPosition[axis];
    } else {
        for (let axis = 0; axis < 3; axis++) {
            position[axis] = source.position[axis];
            direction[axis] = source.direction[axis];
        }
    }
    const dx = owner.listenerPosition[0] - position[0];
    const dy = owner.listenerPosition[1] - position[1];
    const dz = owner.listenerPosition[2] - position[2];
    const distance = Math.hypot(dx, dy, dz);
    let attenuation = 1;
    if (source.channels === 1) {
        attenuation = distanceGain(owner.distanceModel, distance, source);
        const directionLength = Math.hypot(direction[0], direction[1], direction[2]);
        if (distance > 1e-8 && directionLength > 1e-8) {
            const cosine = (direction[0] * dx + direction[1] * dy + direction[2] * dz) / (distance * directionLength);
            const angle = 2 * Math.acos(Math.max(-1, Math.min(1, cosine))) * 180 / Math.PI;
            if (angle > source.coneInner && angle >= source.coneOuter) attenuation *= source.coneOuterGain;
            else if (angle > source.coneInner && source.coneOuter > source.coneInner)
                attenuation *= 1 + (source.coneOuterGain - 1) * (angle - source.coneInner)
                    / (source.coneOuter - source.coneInner);
        }
    }
    source.panner.positionX.value = position[0];
    source.panner.positionY.value = position[1];
    source.panner.positionZ.value = position[2];
    source.gain.gain.value = Math.min(source.maxGain, Math.max(source.minGain, source.gainValue * attenuation));
}

/** Computes velocity-dependent pitch explicitly because Web Audio has no Doppler control. */
export function spatialPlaybackRate(owner, source) {
    if (source.channels !== 1 || owner.dopplerFactor === 0) return source.pitch;
    const position = source.relative ? source.position : source.worldPosition;
    const dx = position[0] - (source.relative ? 0 : owner.listenerPosition[0]);
    const dy = position[1] - (source.relative ? 0 : owner.listenerPosition[1]);
    const dz = position[2] - (source.relative ? 0 : owner.listenerPosition[2]);
    const distance = Math.hypot(dx, dy, dz);
    if (distance < 1e-8) return source.pitch;
    const factor = owner.dopplerFactor, speed = owner.speedOfSound;
    const sourceVelocity = (source.velocity[0] * dx + source.velocity[1] * dy + source.velocity[2] * dz) / distance;
    const listenerVelocity = source.relative ? 0 :
        (owner.listenerVelocity[0] * dx + owner.listenerVelocity[1] * dy + owner.listenerVelocity[2] * dz) / distance;
    const rate = source.pitch * (speed + factor * Math.max(-speed / factor, listenerVelocity))
        / (speed + factor * Math.max(-speed / factor, sourceVelocity));
    if (!Number.isFinite(rate) || rate <= 0)
        throw new Error('WebAudio.DopplerRateUnsupported: authored velocities produce a singular or nonpositive playback rate.');
    return rate;
}
