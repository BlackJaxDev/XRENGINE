import math

def encode(dirx, diry, dirz):
    length = math.sqrt(dirx*dirx + diry*diry + dirz*dirz)
    dirx /= length
    diry /= length
    dirz /= length
    octX = dirx
    octY = dirz
    octZ = diry
    denom = max(abs(octX) + abs(octY) + abs(octZ), 1e-5)
    octX /= denom
    octY /= denom
    octZ /= denom
    uvx = octX
    uvy = octY
    if octZ < 0:
        signDirX = 1.0 if octX >= 0 else -1.0
        signDirY = 1.0 if octY >= 0 else -1.0
        uvx = (1.0 - abs(octY)) * signDirX
        uvy = (1.0 - abs(octX)) * signDirY
    return (uvx * 0.5 + 0.5, uvy * 0.5 + 0.5)

def decode(u,v):
    f_x = u * 2.0 - 1.0
    f_y = v * 2.0 - 1.0
    n_x = f_x
    n_y = f_y
    n_z = 1.0 - abs(f_x) - abs(f_y)
    if n_z < 0:
        signDirX = 1.0 if n_x >= 0 else -1.0
        signDirY = 1.0 if n_y >= 0 else -1.0
        tmp_x = (1.0 - abs(n_y)) * signDirX
        tmp_y = (1.0 - abs(n_x)) * signDirY
        n_x = tmp_x
        n_y = tmp_y
    dirx = n_x
    diry = n_z
    dirz = n_y
    length = math.sqrt(dirx*dirx + diry*diry + dirz*dirz)
    return (dirx/length, diry/length, dirz/length)

samples = [
    (1,0,0),
    (-1,0,0),
    (0,1,0),
    (0,-1,0),
    (0,0,1),
    (0,0,-1),
    (1,1,1),
    (1,-1,0.5)
]

for dir in samples:
    uv = encode(*dir)
    dec = decode(*uv)
    print(dir, '->', uv, '->', dec)
