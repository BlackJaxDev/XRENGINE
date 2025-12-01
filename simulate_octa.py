import math

face_colors = {
    '+X': (1,0,0),
    '-X': (0,1,0),
    '+Y': (0,0,1),
    '-Y': (1,1,0),
    '+Z': (1,0,1),
    '-Z': (0,1,1),
}


def sample_cubemap(dirx, diry, dirz):
    ax = abs(dirx); ay = abs(diry); az = abs(dirz)
    if ax >= ay and ax >= az:
        if dirx > 0:
            return face_colors['+X']
        else:
            return face_colors['-X']
    elif ay >= ax and ay >= az:
        if diry > 0:
            return face_colors['+Y']
        else:
            return face_colors['-Y']
    else:
        if dirz > 0:
            return face_colors['+Z']
        else:
            return face_colors['-Z']


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

samples = [(0.5,0.5),(0.1,0.5),(0.9,0.5),(0.5,0.1),(0.5,0.9),(0.1,0.1),(0.9,0.9),(0.1,0.9),(0.9,0.1)]

for uv in samples:
    d = decode(*uv)
    col = sample_cubemap(*d)
    print(uv, '-> dir', d, '->', col)
