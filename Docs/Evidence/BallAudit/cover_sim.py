#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Cover sim for the ball-decoration problem (read-only, scratch).

Inputs: Docs/Design/Levels/story_01_bench_size/placement.json (127 props,
        aabb / absorbDiameterReq / valueUnits)
Growth: current  (c=0.0041 m3/u, baseline 3.4390244 u)
        repriced (c=0.0005 m3/u, baseline 28.2 u, value = max(1, round(V/0.0005)))
Absorb: ball drives to the NEAREST absorbable prop, absorbs it at the physical
        surface (box touches the sphere along the approach direction).
Metrics (every 5 absorbs):
  M1 surface-inside  : fraction of sample points on the VISUAL sphere that lie
                       inside some attached prop's OBB.
  M2 red-visible     : projected silhouette metric -- fraction of the visual
                       core's disc (averaged over 12 views) that is NOT hidden
                       by a prop that is in front of the core surface.
  burial             : per prop, centre depth = r_phys - d_centre (m, + = inside),
                       and fully-inside fraction (d + h_max < r_phys).
"""
import json, math, random, sys

ROOT = "/home/fulutang/Documents/Github/VRChatKatamariDanacy"
PLACEMENT = ROOT + "/Docs/Design/Levels/story_01_bench_size/placement.json"
SEED = 20261115
VIEWS = 12
GRID = 56
SURF_N = 3000

# ---------------- small vec / quat helpers (pure python) ----------------
def sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def dot(a, b): return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def norm(a):
    l = math.sqrt(dot(a, a)); return (a[0]/l, a[1]/l, a[2]/l)
def qrot(q, v):
    x, y, z, w = q
    t = (2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0]))
    return (v[0]+w*t[0]+y*t[2]-z*t[1],
            v[1]+w*t[1]+z*t[0]-x*t[2],
            v[2]+w*t[2]+x*t[1]-y*t[0])
def rand_quat(rng):
    u1, u2, u3 = rng.random(), rng.random(), rng.random()
    return (math.sqrt(1-u1)*math.sin(2*math.pi*u2),
            math.sqrt(1-u1)*math.cos(2*math.pi*u2),
            math.sqrt(u1)*math.sin(2*math.pi*u3),
            math.sqrt(u1)*math.cos(2*math.pi*u3))
def rand_dir(rng):
    z = rng.uniform(-1, 1); t = rng.uniform(0, 2*math.pi); r = math.sqrt(max(0.0, 1-z*z))
    return (r*math.cos(t), z, r*math.sin(t))
def fib_dirs(n):
    ga = math.pi*(3-math.sqrt(5)); out = []
    for i in range(n):
        y = 1-2*(i+0.5)/n; r = math.sqrt(max(0.0, 1-y*y)); t = ga*i
        out.append((math.cos(t)*r, y, math.sin(t)*r))
    return out

def balanced_dirs(n):
    """12 view directions that are roughly isotropic: Fibonacci + tilt."""
    return fib_dirs(n)

# ---------------- geometry ----------------
class Prop:
    __slots__ = ("item", "pos", "h", "req", "value", "vol", "R", "u", "d", "r_at", "hrad")
    def __init__(self, item, pos, half, req, value, vol):
        self.item = item; self.pos = pos; self.h = half; self.req = req; self.value = value; self.vol = vol
        self.R = (0, 0, 0, 1); self.u = (0, 1, 0); self.d = 0.0; self.r_at = 0.0; self.hrad = 0.0

def h_radial(prop):
    """Support function of the prop's box along its own radial direction u."""
    s = 0.0
    for i in range(3):
        e = [0.0, 0.0, 0.0]; e[i] = 1.0
        s += prop.h[i]*abs(dot(qrot(prop.R, tuple(e)), prop.u))
    return s

def corners(prop, centre):
    out = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                lv = (sx*prop.h[0], sy*prop.h[1], sz*prop.h[2])
                wv = qrot(prop.R, lv)
                out.append((centre[0]+wv[0], centre[1]+wv[1], centre[2]+wv[2]))
    return out

def pnt_in_obb(p, prop, centre):
    d = sub(p, centre); q = (prop.R[0], -prop.R[1], -prop.R[2], prop.R[3])  # inverse
    l = qrot(q, d)
    return abs(l[0]) <= prop.h[0] and abs(l[1]) <= prop.h[1] and abs(l[2]) <= prop.h[2]

# ------------- metric M1: surface points inside an OBB -------------
SURF = fib_dirs(SURF_N)

def m1_surface(visual_r, props, centres):
    if visual_r <= 0 or not props: return 0.0
    inside = 0
    for s in SURF:
        p = (s[0]*visual_r, s[1]*visual_r, s[2]*visual_r)
        for i, pr in enumerate(props):
            if pnt_in_obb(p, pr, centres[i]):
                inside += 1; break
    return inside/len(SURF)

# ------------- metric M2: projected silhouette visible fraction -------------
def hull(pts):
    pts = sorted(set((round(x, 9), round(y, 9)) for x, y in pts))
    if len(pts) <= 2: return pts
    def half(seq):
        out = []
        for p in seq:
            while len(out) >= 2 and ((out[-1][0]-out[-2][0])*(p[1]-out[-1][1]) -
                                      (out[-1][1]-out[-2][1])*(p[0]-out[-1][0])) <= 0:
                out.pop()
            out.append(p)
        return out
    lo = half(pts); up = half(reversed(pts))
    return lo[:-1]+up[:-1]

def inside_poly(pt, poly):
    if len(poly) < 3: return False
    sgn = 0
    for i in range(len(poly)):
        a = poly[i]; b = poly[(i+1) % len(poly)]
        c = (b[0]-a[0])*(pt[1]-a[1])-(b[1]-a[1])*(pt[0]-a[0])
        if abs(c) < 1e-15: continue
        s = 1 if c > 0 else -1
        if sgn == 0: sgn = s
        elif sgn != s: return False
    return True

def m2_visible(visual_r, props, centres, views):
    """Ray test: fraction of the visual core's disc whose first hit is the core.

    For every pixel inside the disc we cast the view ray and compare the core's
    near-surface parameter with the nearest prop hit; the pixel is RED only when
    the core is hit first.  Props buried inside the core are therefore not
    credited (they are behind the core surface)."""
    if visual_r <= 0:
        return 1.0
    r = visual_r
    step = 2*r/GRID
    vis_sum = 0.0
    for w in views:
        up = (0.0, 1.0, 0.0)
        if abs(dot(w, up)) > 0.95:
            up = (0.0, 0.0, 1.0)
        ux = norm(cross(up, w)); uy = cross(w, ux)
        prep = []
        for i, pr in enumerate(props):
            c = centres[i]
            cv = (dot(c, ux), dot(c, uy), dot(c, w))
            if cv[2] < -r - 2*max(pr.h):
                continue
            Rin = (pr.R[0], -pr.R[1], -pr.R[2], pr.R[3])
            cps = corners(pr, c)
            pxy = [(dot(q, ux), dot(q, uy)) for q in cps]
            xs = [q[0] for q in pxy]; ys = [q[1] for q in pxy]
            i0 = max(0, int((min(xs)+r)/step)-1); i1 = min(GRID-1, int((max(xs)+r)/step)+1)
            j0 = max(0, int((min(ys)+r)/step)-1); j1 = min(GRID-1, int((max(ys)+r)/step)+1)
            if i1 < i0 or j1 < j0:
                continue
            prep.append((Rin, cv, pr.h, i0, i1, j0, j1))
        hit = bytearray(GRID*GRID)
        for gy in range(GRID):
            y = -r+(gy+0.5)*step
            for gx in range(GRID):
                x = -r+(gx+0.5)*step
                rr = x*x+y*y
                if rr > r*r:
                    continue
                wc = math.sqrt(max(0.0, r*r-rr))   # core's near surface (w toward camera)
                hidden = False
                for Rin, cv, h, i0, i1, j0, j1 in prep:
                    if gx < i0 or gx > i1 or gy < j0 or gy > j1:
                        continue
                    o0 = qrot(Rin, (x-cv[0], y-cv[1], -cv[2]))
                    dv = qrot(Rin, (0.0, 0.0, 1.0))
                    slo = -1e9; shi = 1e9; ok = True
                    for k in range(3):
                        lo = -h[k]; hi = h[k]; oo = o0[k]; dd = dv[k]
                        if abs(dd) < 1e-12:
                            if oo < lo or oo > hi:
                                ok = False; break
                        else:
                            ta = (lo-oo)/dd; tb = (hi-oo)/dd
                            if ta > tb:
                                ta, tb = tb, ta
                            if ta > slo: slo = ta
                            if tb < shi: shi = tb
                            if slo > shi:
                                ok = False; break
                    # the prop hides this pixel only if some part of it is CLOSER
                    # to the camera than the core's near surface
                    if ok and shi > wc + 1e-6:
                        hidden = True; break
                if not hidden:
                    hit[gy*GRID+gx] = 1
        tot = sum(1 for gy in range(GRID) for gx in range(GRID)
                  if (gx+0.5-step and (-r+(gx+0.5)*step)**2+(-r+(gy+0.5)*step)**2 <= r*r))
        vis = sum(hit)
        vis_sum += vis/max(1, tot)
    return vis_sum/len(views)

# ---------------- growth / absorb ----------------
def radius_units(units, c, baseline, rmax=1.1):
    v = (baseline+units)*c
    r = (3.0*v/(4.0*math.pi))**(1/3.0)
    return min(r, rmax)

def load_props():
    j = json.load(open(PLACEMENT, encoding="utf-8"))
    out = []
    for p in j["props"]:
        if not p.get("absorbable", True): continue
        a = p["aabb"]
        out.append(Prop(p["itemId"], (p["position"][0], p["position"][1], p["position"][2]),
                        (a[0]/2, a[1]/2, a[2]/2), p["absorbDiameterReq"],
                        float(p["valueUnits"]), a[0]*a[1]*a[2]))
    return out, j["level"]

def run(scheme, order="nearest", k=1.0, c=0.0041, baseline=3.4390244,
        repriced=False, use_visual_surface=False, push_out=False, n_absorb=30, rng=None):
    rng = rng or random.Random(SEED)
    props, level = load_props()
    for p in props:
        p.R = rand_quat(rng); p.u = rand_dir(rng)
        p.hrad = h_radial(p)
    start = level["ballStart"]
    ball = (start[0], 0.0, start[2])
    r = radius_units(0.0, c, baseline)
    attached = []
    units = 0.0
    rows = []
    pool = list(props)
    for t in range(1, n_absorb+1):
        D = 2*r
        cand = [p for p in pool if p.req <= D*1.0001]
        if not cand: break
        if order == "nearest":
            cand.sort(key=lambda p: (p.pos[0]-ball[0])**2+(p.pos[2]-ball[2])**2)
        elif order == "biggest":
            cand.sort(key=lambda p: -p.vol)
        elif order == "gate_limit":
            # the biggest item the ball can legally eat = closest to the 10%-volume gate
            cand.sort(key=lambda p: -p.req)   # the item that just became legal = the 10% gate
        elif order == "biggest_near":
            cand.sort(key=lambda p: (-p.vol, (p.pos[0]-ball[0])**2+(p.pos[2]-ball[2])**2))
        else:
            rng.shuffle(cand)
        pick = cand[0]
        # ball drives to the prop (ground contact point), then absorbs at the surface
        tgt = (pick.pos[0], 0.0, pick.pos[2])
        ball = (tgt[0], 0.0, tgt[2])
        pool.remove(pick)
        if use_visual_surface:
            pick.d = k*r + pick.hrad
        else:
            pick.d = r + pick.hrad
        pick.r_at = r
        attached.append(pick)
        # growth
        if repriced:
            units += max(1, round(pick.vol/0.0005))
        else:
            units += pick.value
        r = radius_units(units, c, baseline)
        if t % 5 == 0:
            centres = []
            for p in attached:
                if push_out:
                    d = r + (p.d - p.r_at)          # keep absolute depth from the surface
                else:
                    d = p.d
                centres.append((p.u[0]*d, p.u[1]*d, p.u[2]*d))
            r_vis = k*r
            m1 = m1_surface(r_vis, attached, centres)
            m2 = m2_visible(r_vis, attached, centres, VIEWS_DIRS)
            depths = [r - d for d in (math.sqrt(p[0]**2+p[1]**2+p[2]**2) for p in centres)]
            full = 0
            for i, p in enumerate(attached):
                c0 = centres[i]; d = math.sqrt(dot(c0, c0))
                if d + max(p.h) < r: full += 1
            rows.append(dict(n=t, D=2*r, r=r, r_vis=r_vis, m1=m1, m2=m2,
                             depth_mean=sum(depths)/len(depths),
                             depth_max=max(depths),
                             full=full/len(attached),
                             items=[(p.item[0], round(max(p.h)*2, 3)) for p in attached]))
    return rows

VIEWS_DIRS = balanced_dirs(VIEWS)

SCHEMES = [
    ("a_current",        dict()),
    ("b_k0.9",           dict(k=0.9)),
    ("b_k0.8",           dict(k=0.8)),
    ("b_k0.7",           dict(k=0.7)),
    ("b_k0.6",           dict(k=0.6)),
    ("c_pushout",        dict(push_out=True)),
    ("d_k0.8_push",      dict(k=0.8, push_out=True)),
    ("d_k0.7_push",      dict(k=0.7, push_out=True)),
    ("d_k0.6_push",      dict(k=0.6, push_out=True)),
    ("f_k0.8_vis",       dict(k=0.8, push_out=True, use_visual_surface=True)),
    ("f_k0.7_vis",       dict(k=0.7, push_out=True, use_visual_surface=True)),
    ("f_k0.6_vis",       dict(k=0.6, push_out=True, use_visual_surface=True)),
    ("e_repriced",       dict(repriced=True, c=0.0005, baseline=28.2)),
    ("e_repriced_k0.8",  dict(repriced=True, c=0.0005, baseline=28.2, k=0.8)),
    ("f_k0.5_vis",       dict(k=0.5, push_out=True, use_visual_surface=True)),
    ("f_k0.4_vis",       dict(k=0.4, push_out=True, use_visual_surface=True)),
    ("g_repriced_f0.7",  dict(repriced=True, c=0.0005, baseline=28.2, k=0.7,
                              push_out=True, use_visual_surface=True)),
    ("g_repriced_f0.6",  dict(repriced=True, c=0.0005, baseline=28.2, k=0.6,
                              push_out=True, use_visual_surface=True)),
]

def fmt(rows):
    out = ["n | D(m) | r_vis | M1 surface-inside | M2 red-visible | depth_mean(m) | depth_max | fully-inside"]
    for r in rows:
        out.append("%2d | %.3f | %.3f | %5.1f%% | %5.1f%% | %+.3f | %+.3f | %3.0f%%" % (
            r["n"], r["D"], r["r_vis"], 100*r["m1"], 100*r["m2"],
            r["depth_mean"], r["depth_max"], 100*r["full"]))
    return "\n".join(out)

if __name__ == "__main__":
    order = sys.argv[1] if len(sys.argv) > 1 else "nearest"
    only = sys.argv[2] if len(sys.argv) > 2 else None
    print("ORDER=", order)
    for name, kw in SCHEMES:
        if only and only not in name: continue
        rows = run(name, order=order, rng=random.Random(SEED), **kw)
        print("="*100); print(name, kw); print(fmt(rows))
