"""Lists flickering faces (overlapping, same plane) in a view's scene.json.

python3 zfight.py <scene.json> [min m²]
"""
import json, sys, collections
import numpy as np

scene = json.load(open(sys.argv[1]))
min_area = float(sys.argv[2]) if len(sys.argv) > 2 else 0.5

def label(inst):
    i = inst["info"]
    p = i.get('position') or inst['m'][12:15]
    return f"{i['kind']}:{i.get('name', inst['mesh'])}@{','.join(str(round(c)) for c in p)}"

tris = []
for inst in scene["instances"]:
    pieces = scene["meshes"].get(inst["mesh"])
    if not pieces:
        continue
    m = np.array(inst["m"], dtype=float).reshape(4, 4)
    for pi, p in enumerate(pieces):
        mat = p["material"].split("/")[-1].split(".")[0]
        if "Decal" in mat or "Light" in mat or "Glass" in mat:
            continue
        pos = np.array(p["positions"], dtype=float).reshape(-1, 3)
        world = pos @ m[:3, :3] + m[3, :3]
        idx = np.array(p["indices"], dtype=int).reshape(-1, 3)
        tris.append((label(inst), pi, mat, world[idx]))

src, piece, mats, allt = [], [], [], []
for s, pi, mat, t in tris:
    src += [s] * len(t); piece += [pi] * len(t); mats += [mat] * len(t); allt.append(t)
T = np.concatenate(allt)
n = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0])
area = np.linalg.norm(n, axis=1) / 2
ok = area > 0.02
n[ok] /= (2 * area[ok])[:, None]
d = np.einsum("ij,ij->i", n, T[:, 0])

keys = collections.defaultdict(list)
for i in np.nonzero(ok)[0]:
    k = (tuple(np.round(n[i] * 60).astype(int)), int(np.floor(d[i] / 0.02)))
    keys[k].append(i)

def raster(i, axes, cell):
    a, b, c = T[i][:, axes[0]], T[i][:, axes[1]], None
    p = np.stack([a, b], 1)
    lo, hi = np.floor(p.min(0) / cell).astype(int), np.ceil(p.max(0) / cell).astype(int)
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1), np.arange(lo[1], hi[1] + 1))
    q = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1) * cell
    def side(p0, p1):
        return (p1[0] - p0[0]) * (q[:, 1] - p0[1]) - (p1[1] - p0[1]) * (q[:, 0] - p0[0])
    s1, s2, s3 = side(p[0], p[1]), side(p[1], p[2]), side(p[2], p[0])
    inside = ((s1 >= 0) & (s2 >= 0) & (s3 >= 0)) | ((s1 <= 0) & (s2 <= 0) & (s3 <= 0))
    return set(map(tuple, (q[inside] / cell).astype(int)))

cell = 0.25
pairs = collections.Counter()
where = {}
done = set()
for (nk, dk), idx in keys.items():
    if (nk, dk) in done:
        continue
    group = list(idx) + keys.get((nk, dk + 1), [])
    done.add((nk, dk))
    sources = {src[i] for i in group}
    if len(sources) < 2 or not any(s.startswith("ice item") for s in sources):
        continue
    axes = [ax for ax in range(3) if ax != int(np.argmax(np.abs(n[group[0]])))]
    cover = collections.defaultdict(set)
    for i in group:
        for c in raster(i, axes, cell):
            cover[c].add((src[i], mats[i]))
    for c, who in cover.items():
        ss = {w[0] for w in who}
        if len(ss) > 1 and any(s.startswith("ice item") for s in ss):
            key = tuple(sorted(who))
            pairs[key] += cell * cell
            where.setdefault(key, (nk, round(dk * 0.02, 2)))

def own_skin(key):
    # skins are lifted on purpose
    labels = {s for s, _ in key}
    blocks = [l for l in labels if l.startswith("block:")]
    items = [l for l in labels if l.startswith("ice item:")]
    return len(labels) == 2 and blocks and items and blocks[0].split("@")[1] == items[0].split("@")[1] \
        and blocks[0].split(":")[1].split("@")[0] in items[0]

for key, a in pairs.most_common():
    if a < min_area:
        break
    if own_skin(key):
        continue
    print(f"{a:7.1f} m²  normal~{np.round(np.array(where[key][0]) / 60, 2).tolist()}  " + "  |  ".join(f"{s} [{m}]" for s, m in key))
