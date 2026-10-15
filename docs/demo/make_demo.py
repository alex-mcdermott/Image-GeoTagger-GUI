"""Builds the demo folder used for the README screenshots (no real photos or personal data).

    python docs/demo/make_demo.py <output-folder> <path-to-exiftool>

Creates a GPX track of a walk along London's South Bank (Westminster -> Tower Bridge, routed
along real footpaths via routing.openstreetmap.de) and flat illustrated "photos" of the
landmarks on the way, with capture times matching when the walk passes each one. Three photos
taken earlier already have GPS tags; one taken after the walk is left for manual placement.
Requires Pillow (pip install pillow).
"""
import json, math, os, random, subprocess, sys, urllib.request
from datetime import datetime, timedelta, timezone
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1]
EXIFTOOL = sys.argv[2]
os.makedirs(OUT, exist_ok=True)
random.seed(7)

# ------------------------------------------------------------------ route
WAYPOINTS = [(-0.1270, 51.5007), (-0.1196, 51.5028), (-0.1166, 51.5060), (-0.1075, 51.5087),
             (-0.0994, 51.5084), (-0.0905, 51.5073), (-0.0810, 51.5057), (-0.0754, 51.5045)]
url = ("https://routing.openstreetmap.de/routed-foot/route/v1/foot/" +
       ";".join(f"{lon},{lat}" for lon, lat in WAYPOINTS) + "?overview=full&geometries=geojson")
req = urllib.request.Request(url, headers={"User-Agent": "Geotagger-demo/1.0"})
coords = json.load(urllib.request.urlopen(req, timeout=60))["routes"][0]["geometry"]["coordinates"]

def dist(a, b):
    lat = math.radians((a[1] + b[1]) / 2)
    dx = (b[0] - a[0]) * 111320 * math.cos(lat)
    dy = (b[1] - a[1]) * 110540
    return math.hypot(dx, dy)

# densify to <= 12 m spacing
pts = [coords[0]]
for a, b in zip(coords, coords[1:]):
    d = dist(a, b)
    n = max(1, int(d // 12))
    for i in range(1, n + 1):
        pts.append((a[0] + (b[0] - a[0]) * i / n, a[1] + (b[1] - a[1]) * i / n))

cum = [0.0]
for a, b in zip(pts, pts[1:]):
    cum.append(cum[-1] + dist(a, b))

BST = timezone(timedelta(hours=1))
START = datetime(2026, 9, 12, 14, 0, 0, tzinfo=BST)
SPEED = 1.15  # m/s

def nearest_index(lon, lat):
    return min(range(len(pts)), key=lambda i: dist(pts[i], (lon, lat)))

# Landmarks along the walk: (key, lon, lat, pause seconds)
STOPS = [
    ("bigben", -0.1245, 51.5007, 90),
    ("bridge", -0.1215, 51.5008, 60),
    ("eye", -0.1196, 51.5031, 240),
    ("eye2", -0.1189, 51.5040, 60),
    ("stpauls_view", -0.1150, 51.5082, 90),
    ("tate", -0.0995, 51.5083, 120),
    ("millennium", -0.0986, 51.5086, 60),
    ("globe", -0.0972, 51.5082, 120),
    ("shard", -0.0890, 51.5066, 90),
    ("belfast", -0.0812, 51.5058, 120),
    ("towerbridge", -0.0770, 51.5048, 90),
    ("towerbridge2", -0.0757, 51.5045, 60),
]
stop_at = {}
for key, lon, lat, pause in STOPS:
    stop_at[nearest_index(lon, lat)] = (key, pause)

# Timeline: walk at SPEED, pause at stops (logger keeps recording while standing).
times, photo_times = [], {}
t = START
for i in range(len(pts)):
    if i > 0:
        t += timedelta(seconds=(cum[i] - cum[i - 1]) / SPEED)
    times.append(t)
    if i in stop_at:
        key, pause = stop_at[i]
        photo_times[key] = t + timedelta(seconds=pause / 2)
        t += timedelta(seconds=pause)

# GPX with a little GPS jitter
with open(os.path.join(OUT, "South Bank walk.gpx"), "w", encoding="utf-8") as f:
    f.write('<?xml version="1.0" encoding="UTF-8"?>\n<gpx version="1.1" creator="Geotagger demo" '
            'xmlns="http://www.topografix.com/GPX/1/1">\n  <metadata><name>South Bank walk</name></metadata>\n'
            '  <trk><name>South Bank walk</name><trkseg>\n')
    for (lon, lat), tt in zip(pts, times):
        jl = random.gauss(0, 0.000012)
        jo = random.gauss(0, 0.000018)
        ele = 6 + 3 * math.sin(lon * 900)
        f.write(f'    <trkpt lat="{lat + jl:.7f}" lon="{lon + jo:.7f}"><ele>{ele:.1f}</ele>'
                f'<time>{tt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}</time></trkpt>\n')
    f.write('  </trkseg></trk>\n</gpx>\n')
print(f"track: {cum[-1]:.0f} m, {len(pts)} points, {times[0]:%H:%M}-{times[-1]:%H:%M}")

# ------------------------------------------------------------------ drawing helpers
S = 2  # supersampling

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def hexc(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))

PALETTES = {
    "noon":    dict(sky=("#5AA9E6", "#CFEAFB"), sun="#FFF6D5", far="#9DB4C8", mid="#6E8296", water=("#4F7FA6", "#2F5C80"), light=1.0),
    "after":   dict(sky=("#6CA6D9", "#F6E3C4"), sun="#FFE7A8", far="#A7A9B8", mid="#727789", water=("#5A82A3", "#34526E"), light=0.95),
    "golden":  dict(sky=("#F29E6D", "#FCE1A8"), sun="#FFF1C1", far="#C49482", mid="#8B6A67", water=("#C98C6E", "#6F5363"), light=0.9),
    "dusk":    dict(sky=("#2D3A7A", "#F08E6B"), sun="#FFD6A0", far="#4B4D78", mid="#33345A", water=("#3A3F72", "#1E2148"), light=0.65),
}

class Scene:
    def __init__(self, w, h, palette, horizon=0.68):
        self.w, self.h = w * S, h * S
        self.p = PALETTES[palette]
        self.img = Image.new("RGB", (self.w, self.h))
        self.d = ImageDraw.Draw(self.img, "RGBA")
        self.hy = int(self.h * horizon)
        top, bottom = hexc(self.p["sky"][0]), hexc(self.p["sky"][1])
        for y in range(self.h):
            self.d.line([(0, y), (self.w, y)], fill=lerp(top, bottom, min(1.0, y / max(1, self.hy))))

    def X(self, f): return int(f * self.w)
    def Y(self, f): return int(f * self.h)

    def sun(self, x, y, r):
        glow = Image.new("RGBA", (self.w, self.h), (0, 0, 0, 0))
        gd = ImageDraw.Draw(glow)
        c = hexc(self.p["sun"])
        R = int(r * self.w)
        gd.ellipse([self.X(x) - R * 3, self.Y(y) - R * 3, self.X(x) + R * 3, self.Y(y) + R * 3], fill=c + (70,))
        glow = glow.filter(ImageFilter.GaussianBlur(R))
        self.img.paste(glow, (0, 0), glow)
        self.d = ImageDraw.Draw(self.img, "RGBA")
        self.d.ellipse([self.X(x) - R, self.Y(y) - R, self.X(x) + R, self.Y(y) + R], fill=c)

    def clouds(self, n=4):
        for _ in range(n):
            cx, cy = random.uniform(0.05, 0.95), random.uniform(0.08, 0.35)
            s = random.uniform(0.05, 0.11)
            for k in range(5):
                ox, oy = random.uniform(-1.2, 1.2) * s, random.uniform(-0.25, 0.2) * s
                r = s * random.uniform(0.5, 0.9)
                self.d.ellipse([self.X(cx + ox - r), self.Y(cy + oy) - self.X(r) * 0.6,
                                self.X(cx + ox + r), self.Y(cy + oy) + self.X(r) * 0.6], fill=(255, 255, 255, 70))

    def skyline(self, base, color, hmin, hmax, wmin=0.02, wmax=0.06, alpha=255):
        x = -0.02
        c = hexc(color) + (alpha,)
        while x < 1.02:
            bw = random.uniform(wmin, wmax)
            bh = random.uniform(hmin, hmax)
            self.d.rectangle([self.X(x), self.Y(base - bh), self.X(x + bw), self.Y(base) + 2], fill=c)
            if random.random() < 0.15:
                self.d.polygon([(self.X(x), self.Y(base - bh)), (self.X(x + bw / 2), self.Y(base - bh - 0.04)),
                                (self.X(x + bw), self.Y(base - bh))], fill=c)
            x += bw * random.uniform(0.85, 1.05)

    def water(self, top=None):
        top = self.hy if top is None else self.Y(top)
        a, b = hexc(self.p["water"][0]), hexc(self.p["water"][1])
        for y in range(top, self.h):
            self.d.line([(0, y), (self.w, y)], fill=lerp(a, b, (y - top) / max(1, self.h - top)))
        for _ in range(140):
            y = random.randint(top + 4, self.h)
            x = random.randint(0, self.w)
            L = random.randint(20, 140) * S
            self.d.line([(x, y), (x + L, y)], fill=(255, 255, 255, random.randint(18, 50)), width=S * 2)

    def ground(self, top, color="#5E6B5A"):
        self.d.rectangle([0, self.Y(top), self.w, self.h], fill=hexc(color))

    def railing(self, y, color=(40, 44, 52, 230)):
        Y = self.Y(y)
        self.d.rectangle([0, Y, self.w, Y + 6 * S], fill=color)
        for x in range(0, self.w, 46 * S):
            self.d.rectangle([x, Y, x + 5 * S, self.h], fill=color)
        self.d.rectangle([0, Y + 70 * S, self.w, Y + 74 * S], fill=color)

    def lamp(self, x, base, h, color=(35, 38, 46, 255)):
        X = self.X(x)
        self.d.rectangle([X - 4 * S, self.Y(base - h), X + 4 * S, self.Y(base)], fill=color)
        self.d.ellipse([X - 16 * S, self.Y(base - h) - 22 * S, X + 16 * S, self.Y(base - h) + 6 * S], fill=color)
        self.d.ellipse([X - 8 * S, self.Y(base - h) - 6 * S, X + 8 * S, self.Y(base - h) + 6 * S], fill=hexc(self.p["sun"]) + (220,))

    def tree(self, x, base, r, color="#3F6B47"):
        X, B, R = self.X(x), self.Y(base), self.X(r)
        self.d.rectangle([X - R // 8, B - R, X + R // 8, B], fill=(70, 52, 40, 255))
        c = hexc(color)
        for ox, oy, k in [(-0.5, -1.2, 0.8), (0.45, -1.15, 0.75), (0, -1.6, 0.9)]:
            rr = int(R * k)
            self.d.ellipse([X + int(ox * R) - rr, B + int(oy * R) - rr, X + int(ox * R) + rr, B + int(oy * R) + rr], fill=c)

    def save(self, name, light=None):
        img = self.img.resize((self.w // S, self.h // S), Image.LANCZOS)
        path = os.path.join(OUT, name)
        img.save(path, "JPEG", quality=88)
        return path

def stone(sc, k=1.0):
    base = hexc("#C9B48F")
    return tuple(int(c * sc.p["light"] * k) for c in base) + (255,)

# ------------------------------------------------------------------ landmarks
def big_ben(sc, cx, base, h):
    s = stone(sc)
    w = h * 0.16
    X, B, W, H = sc.X(cx), sc.Y(base), sc.X(w), sc.Y(h)
    sc.d.rectangle([X - W // 2, B - int(H * 0.72), X + W // 2, B], fill=s)
    for i in range(1, 9):
        sc.d.line([(X - W // 2 + i * W // 9, B - int(H * 0.7)), (X - W // 2 + i * W // 9, B)], fill=stone(sc, 0.82), width=S * 2)
    cy = B - int(H * 0.78)
    sc.d.rectangle([X - int(W * 0.62), cy - int(W * 0.6), X + int(W * 0.62), cy + int(W * 0.62)], fill=stone(sc, 0.95))
    sc.d.ellipse([X - int(W * 0.48), cy - int(W * 0.48), X + int(W * 0.48), cy + int(W * 0.48)], fill=(246, 240, 222, 255))
    sc.d.line([(X, cy), (X, cy - int(W * 0.36))], fill=(30, 30, 30, 255), width=S * 4)
    sc.d.line([(X, cy), (X + int(W * 0.25), cy + int(W * 0.12))], fill=(30, 30, 30, 255), width=S * 4)
    top = cy - int(W * 0.6)
    sc.d.rectangle([X - int(W * 0.5), top - int(W * 0.7), X + int(W * 0.5), top], fill=stone(sc, 0.9))
    sc.d.polygon([(X - int(W * 0.55), top - int(W * 0.7)), (X, top - int(H * 0.28)), (X + int(W * 0.55), top - int(W * 0.7))],
                 fill=(70, 80, 72, 255))
    sc.d.line([(X, top - int(H * 0.28)), (X, top - int(H * 0.32))], fill=(212, 175, 55, 255), width=S * 3)

def parliament(sc, x0, x1, base, h):
    s = stone(sc, 0.92)
    sc.d.rectangle([sc.X(x0), sc.Y(base - h), sc.X(x1), sc.Y(base)], fill=s)
    x = x0
    while x < x1:
        sc.d.rectangle([sc.X(x), sc.Y(base - h * 1.35), sc.X(x + 0.008), sc.Y(base - h)], fill=s)
        sc.d.polygon([(sc.X(x), sc.Y(base - h * 1.35)), (sc.X(x + 0.004), sc.Y(base - h * 1.6)), (sc.X(x + 0.008), sc.Y(base - h * 1.35))], fill=s)
        x += 0.035
    for i in range(int((x1 - x0) / 0.012)):
        xx = x0 + 0.006 + i * 0.012
        sc.d.rectangle([sc.X(xx), sc.Y(base - h * 0.8), sc.X(xx + 0.004), sc.Y(base - h * 0.25)], fill=stone(sc, 0.75))

def london_eye(sc, cx, cy, r):
    X, Y, R = sc.X(cx), sc.Y(cy), sc.X(r)
    col = (235, 238, 242, 255)
    leg = (210, 214, 220, 255)
    sc.d.line([(X, Y), (X - int(R * 0.55), Y + int(R * 1.15))], fill=leg, width=S * 9)
    sc.d.line([(X, Y), (X + int(R * 0.15), Y + int(R * 1.15))], fill=leg, width=S * 9)
    for k in range(32):
        a = 2 * math.pi * k / 32
        sc.d.line([(X, Y), (X + int(R * math.cos(a)), Y + int(R * math.sin(a)))], fill=(235, 238, 242, 150), width=S)
    sc.d.ellipse([X - R, Y - R, X + R, Y + R], outline=col, width=S * 7)
    sc.d.ellipse([X - int(R * 0.93), Y - int(R * 0.93), X + int(R * 0.93), Y + int(R * 0.93)], outline=(235, 238, 242, 200), width=S * 3)
    for k in range(32):
        a = 2 * math.pi * k / 32
        px, py = X + int(R * 1.04 * math.cos(a)), Y + int(R * 1.04 * math.sin(a))
        cw = max(6, R // 22)
        sc.d.ellipse([px - cw, py - int(cw * 0.75), px + cw, py + int(cw * 0.75)], fill=(205, 225, 240, 255), outline=(150, 165, 180, 255), width=S)
    sc.d.ellipse([X - R // 14, Y - R // 14, X + R // 14, Y + R // 14], fill=(190, 196, 204, 255))

def st_pauls(sc, cx, base, scale):
    s = stone(sc, 1.02)
    W = scale
    sc.d.rectangle([sc.X(cx - W * 0.9), sc.Y(base - W * 0.55), sc.X(cx + W * 0.9), sc.Y(base)], fill=s)
    sc.d.rectangle([sc.X(cx - W * 0.42), sc.Y(base - W * 1.05), sc.X(cx + W * 0.42), sc.Y(base - W * 0.55)], fill=stone(sc, 0.95))
    for i in range(9):
        x = cx - W * 0.38 + i * W * 0.095
        sc.d.rectangle([sc.X(x), sc.Y(base - W * 1.0), sc.X(x + W * 0.03), sc.Y(base - W * 0.6)], fill=stone(sc, 0.8))
    sc.d.chord([sc.X(cx - W * 0.4), sc.Y(base - W * 1.55), sc.X(cx + W * 0.4), sc.Y(base - W * 0.6)], 180, 360, fill=(120, 132, 128, 255))
    sc.d.rectangle([sc.X(cx - W * 0.06), sc.Y(base - W * 1.33), sc.X(cx + W * 0.06), sc.Y(base - W * 1.07)], fill=s)
    sc.d.ellipse([sc.X(cx - W * 0.04), sc.Y(base - W * 1.42), sc.X(cx + W * 0.04), sc.Y(base - W * 1.33)], fill=(212, 175, 55, 255))
    for side in (-1, 1):
        x = cx + side * W * 0.72
        sc.d.rectangle([sc.X(x - W * 0.08), sc.Y(base - W * 0.95), sc.X(x + W * 0.08), sc.Y(base - W * 0.55)], fill=s)
        sc.d.polygon([(sc.X(x - W * 0.08), sc.Y(base - W * 0.95)), (sc.X(x), sc.Y(base - W * 1.1)), (sc.X(x + W * 0.08), sc.Y(base - W * 0.95))], fill=s)

def tate(sc, x0, x1, base, h):
    brick = tuple(int(c * sc.p["light"]) for c in (139, 92, 70)) + (255,)
    sc.d.rectangle([sc.X(x0), sc.Y(base - h), sc.X(x1), sc.Y(base)], fill=brick)
    sc.d.rectangle([sc.X(x0), sc.Y(base - h - 0.03), sc.X(x1), sc.Y(base - h)], fill=(190, 200, 205, 220))
    mid = (x0 + x1) / 2
    sc.d.rectangle([sc.X(mid - 0.025), sc.Y(base - h - 0.38), sc.X(mid + 0.025), sc.Y(base - h)], fill=brick)
    sc.d.rectangle([sc.X(mid - 0.027), sc.Y(base - h - 0.4), sc.X(mid + 0.027), sc.Y(base - h - 0.38)], fill=(190, 200, 205, 255))
    x = x0 + 0.02
    while x < x1 - 0.02:
        sc.d.rectangle([sc.X(x), sc.Y(base - h + 0.02), sc.X(x + 0.004), sc.Y(base - 0.03)], fill=(60, 40, 32, 255))
        x += 0.03

def millennium_bridge(sc):
    col = (205, 210, 216, 255)
    vx, vy = sc.X(0.5), sc.Y(0.5)
    for side in (-1, 1):
        sc.d.polygon([(vx, vy), (sc.X(0.5 + side * 0.9), sc.h), (sc.X(0.5 + side * 0.86), sc.h)], fill=col)
    sc.d.polygon([(vx, vy), (sc.X(-0.4), sc.h), (sc.X(1.4), sc.h)], fill=(150, 156, 164, 255))
    for i in range(1, 9):
        t = i / 9
        y = vy + (sc.h - vy) * t * t
        half = (sc.w * 0.9) * t * t
        sc.d.line([(vx - half, y), (vx + half, y)], fill=(120, 126, 134, 255), width=S * 3)
        sc.d.line([(vx - half * 0.98, y), (vx - half * 0.98, y - 80 * S * t)], fill=col, width=S * 3)
        sc.d.line([(vx + half * 0.98, y), (vx + half * 0.98, y - 80 * S * t)], fill=col, width=S * 3)

def globe(sc, cx, base, w, h):
    white = tuple(int(c * sc.p["light"]) for c in (240, 234, 220)) + (255,)
    wood = (88, 62, 44, 255)
    pts = []
    for k in range(7):
        a = math.pi + math.pi * k / 6
        pts.append((cx + w / 2 * math.cos(a), base - h * 0.05 * math.sin(a)))
    sc.d.rectangle([sc.X(cx - w / 2), sc.Y(base - h), sc.X(cx + w / 2), sc.Y(base)], fill=white)
    for i in range(1, 12):
        x = cx - w / 2 + i * w / 12
        sc.d.line([(sc.X(x), sc.Y(base - h)), (sc.X(x), sc.Y(base))], fill=wood, width=S * 4)
    for f in (0.33, 0.66):
        sc.d.line([(sc.X(cx - w / 2), sc.Y(base - h * f)), (sc.X(cx + w / 2), sc.Y(base - h * f))], fill=wood, width=S * 4)
    sc.d.polygon([(sc.X(cx - w / 2 - 0.02), sc.Y(base - h)), (sc.X(cx - w / 2 + 0.05), sc.Y(base - h * 1.45)),
                  (sc.X(cx + w / 2 - 0.05), sc.Y(base - h * 1.45)), (sc.X(cx + w / 2 + 0.02), sc.Y(base - h))], fill=(150, 120, 70, 255))
    for i in range(30):
        x = cx - w / 2 + i * w / 30
        sc.d.line([(sc.X(x), sc.Y(base - h * 1.43)), (sc.X(x - 0.01), sc.Y(base - h))], fill=(125, 98, 55, 160), width=S * 2)

def shard(sc, cx, base, h):
    pts = [(sc.X(cx - h * 0.13), sc.Y(base)), (sc.X(cx - h * 0.012), sc.Y(base - h)), (sc.X(cx + h * 0.004), sc.Y(base - h * 1.04)),
           (sc.X(cx + h * 0.02), sc.Y(base - h * 0.97)), (sc.X(cx + h * 0.13), sc.Y(base))]
    sc.d.polygon(pts, fill=(160, 186, 206, 255))
    sc.d.polygon([pts[0], pts[1], (sc.X(cx), sc.Y(base))], fill=(126, 156, 180, 255))
    for i in range(1, 14):
        y = base - h * i / 14
        half = h * 0.13 * (1 - i / 14)
        sc.d.line([(sc.X(cx - half), sc.Y(y)), (sc.X(cx + half), sc.Y(y))], fill=(230, 240, 248, 90), width=S)

def belfast(sc, x0, x1, wl):
    grey = tuple(int(c * sc.p["light"]) for c in (128, 136, 142)) + (255,)
    dark = tuple(int(c * sc.p["light"]) for c in (92, 98, 104)) + (255,)
    L = x1 - x0
    sc.d.polygon([(sc.X(x0), sc.Y(wl - 0.06)), (sc.X(x1), sc.Y(wl - 0.08)), (sc.X(x1 - L * 0.04), sc.Y(wl)), (sc.X(x0 + L * 0.06), sc.Y(wl))], fill=grey)
    sc.d.rectangle([sc.X(x0 + L * 0.3), sc.Y(wl - 0.16), sc.X(x0 + L * 0.62), sc.Y(wl - 0.06)], fill=dark)
    sc.d.rectangle([sc.X(x0 + L * 0.4), sc.Y(wl - 0.24), sc.X(x0 + L * 0.52), sc.Y(wl - 0.16)], fill=grey)
    for mx, mh in ((0.36, 0.42), (0.58, 0.36)):
        sc.d.line([(sc.X(x0 + L * mx), sc.Y(wl - 0.06)), (sc.X(x0 + L * mx), sc.Y(wl - mh))], fill=dark, width=S * 4)
    for tx in (0.14, 0.22, 0.72, 0.8):
        sc.d.rectangle([sc.X(x0 + L * tx), sc.Y(wl - 0.1), sc.X(x0 + L * (tx + 0.05)), sc.Y(wl - 0.065)], fill=dark)
        sc.d.line([(sc.X(x0 + L * (tx + 0.05)), sc.Y(wl - 0.085)), (sc.X(x0 + L * (tx + 0.1)), sc.Y(wl - 0.09))], fill=dark, width=S * 3)
    sc.d.rectangle([sc.X(x0 + L * 0.2), sc.Y(wl - 0.075), sc.X(x0 + L * 0.85), sc.Y(wl - 0.06)], fill=(60, 66, 72, 255))

def tower_bridge(sc, cx, wl, scale):
    s = stone(sc, 1.0)
    blue = (86, 140, 182, 255)
    W = scale
    for side in (-1, 1):
        tx = cx + side * W * 0.42
        sc.d.rectangle([sc.X(tx - W * 0.09), sc.Y(wl - W * 1.05), sc.X(tx + W * 0.09), sc.Y(wl)], fill=s)
        for k in (-1, 1):
            px = tx + k * W * 0.075
            sc.d.rectangle([sc.X(px - W * 0.02), sc.Y(wl - W * 1.17), sc.X(px + W * 0.02), sc.Y(wl - W * 1.05)], fill=s)
            sc.d.polygon([(sc.X(px - W * 0.025), sc.Y(wl - W * 1.17)), (sc.X(px), sc.Y(wl - W * 1.3)), (sc.X(px + W * 0.025), sc.Y(wl - W * 1.17))], fill=(70, 80, 90, 255))
        sc.d.polygon([(sc.X(tx - W * 0.1), sc.Y(wl - W * 1.05)), (sc.X(tx), sc.Y(wl - W * 1.25)), (sc.X(tx + W * 0.1), sc.Y(wl - W * 1.05))], fill=(70, 80, 90, 255))
        sc.d.rectangle([sc.X(tx - W * 0.035), sc.Y(wl - W * 0.7), sc.X(tx + W * 0.035), sc.Y(wl - W * 0.45)], fill=(80, 70, 60, 200))
        # suspension chains to the shore
        far = cx + side * W * 1.3
        for i in range(40):
            t0, t1 = i / 40, (i + 1) / 40
            def chain(t):
                x = tx + (far - tx) * t
                y = wl - W * 0.8 + (W * 0.75) * (t ** 1.6)
                return (sc.X(x), sc.Y(y))
            sc.d.line([chain(t0), chain(t1)], fill=blue, width=S * 7)
    for y in (0.95, 0.88):
        sc.d.rectangle([sc.X(cx - W * 0.33), sc.Y(wl - W * y), sc.X(cx + W * 0.33), sc.Y(wl - W * y + W * 0.05)], fill=blue)
    sc.d.rectangle([sc.X(cx - W * 1.4), sc.Y(wl - W * 0.2), sc.X(cx + W * 1.4), sc.Y(wl - W * 0.15)], fill=blue)

def tower_of_london(sc, cx, base, w):
    s = tuple(int(c * sc.p["light"]) for c in (214, 200, 170)) + (255,)
    sc.d.rectangle([sc.X(cx - w / 2), sc.Y(base - w * 0.7), sc.X(cx + w / 2), sc.Y(base)], fill=s)
    for k in range(4):
        x = cx - w / 2 + k * w / 3
        sc.d.rectangle([sc.X(x - w * 0.05), sc.Y(base - w * 0.95), sc.X(x + w * 0.05), sc.Y(base - w * 0.7)], fill=s)
        sc.d.chord([sc.X(x - w * 0.055), sc.Y(base - w * 1.05), sc.X(x + w * 0.055), sc.Y(base - w * 0.88)], 180, 360, fill=(60, 64, 80, 255))
    i = 0
    x = cx - w / 2
    while x < cx + w / 2:
        sc.d.rectangle([sc.X(x), sc.Y(base - w * 0.74), sc.X(x + w * 0.025), sc.Y(base - w * 0.7)], fill=s)
        x += w * 0.05
    for r in range(2):
        for c in range(6):
            x = cx - w * 0.38 + c * w * 0.15
            y = base - w * 0.55 + r * w * 0.22
            sc.d.rounded_rectangle([sc.X(x), sc.Y(y), sc.X(x + w * 0.03), sc.Y(y + w * 0.08)], radius=6, fill=(255, 205, 120, 230))

def nelson(sc, cx, base, h):
    s = stone(sc, 0.98)
    sc.d.rectangle([sc.X(cx - h * 0.09), sc.Y(base - h * 0.12), sc.X(cx + h * 0.09), sc.Y(base)], fill=s)
    sc.d.rectangle([sc.X(cx - h * 0.025), sc.Y(base - h * 0.9), sc.X(cx + h * 0.025), sc.Y(base - h * 0.12)], fill=s)
    for i in range(5):
        x = cx - h * 0.02 + i * h * 0.01
        sc.d.line([(sc.X(x), sc.Y(base - h * 0.88)), (sc.X(x), sc.Y(base - h * 0.14))], fill=stone(sc, 0.85), width=S)
    sc.d.rectangle([sc.X(cx - h * 0.04), sc.Y(base - h * 0.95), sc.X(cx + h * 0.04), sc.Y(base - h * 0.9)], fill=(120, 110, 80, 255))
    sc.d.ellipse([sc.X(cx - h * 0.014), sc.Y(base - h * 1.02), sc.X(cx + h * 0.014), sc.Y(base - h * 0.95)], fill=(90, 90, 80, 255))
    for side in (-1, 1):
        lx = cx + side * h * 0.13
        sc.d.ellipse([sc.X(lx - h * 0.05), sc.Y(base - h * 0.06), sc.X(lx + h * 0.05), sc.Y(base)], fill=(70, 70, 64, 255))

def fountain(sc, cx, base, w):
    sc.d.ellipse([sc.X(cx - w / 2), sc.Y(base - w * 0.12), sc.X(cx + w / 2), sc.Y(base + w * 0.04)], fill=stone(sc, 0.9))
    sc.d.ellipse([sc.X(cx - w * 0.45), sc.Y(base - w * 0.1), sc.X(cx + w * 0.45), sc.Y(base)], fill=(120, 170, 200, 255))
    for i in range(18):
        a = math.pi * (0.15 + 0.7 * i / 17)
        sc.d.line([(sc.X(cx), sc.Y(base - w * 0.05)), (sc.X(cx + math.cos(a) * w * 0.3), sc.Y(base - w * 0.05 - math.sin(a) * w * 0.4))],
                  fill=(235, 245, 255, 120), width=S * 2)

def gallery(sc, x0, x1, base, h):
    s = stone(sc, 1.0)
    sc.d.rectangle([sc.X(x0), sc.Y(base - h), sc.X(x1), sc.Y(base)], fill=s)
    mid = (x0 + x1) / 2
    pw = (x1 - x0) * 0.22
    sc.d.polygon([(sc.X(mid - pw), sc.Y(base - h * 1.05)), (sc.X(mid), sc.Y(base - h * 1.35)), (sc.X(mid + pw), sc.Y(base - h * 1.05))], fill=stone(sc, 1.05))
    sc.d.rectangle([sc.X(mid - pw), sc.Y(base - h * 1.07), sc.X(mid + pw), sc.Y(base - h * 1.0)], fill=stone(sc, 1.05))
    for i in range(8):
        x = mid - pw * 0.9 + i * pw * 1.8 / 7
        sc.d.rectangle([sc.X(x - 0.004), sc.Y(base - h), sc.X(x + 0.004), sc.Y(base - h * 0.25)], fill=stone(sc, 0.82))
    sc.d.chord([sc.X(mid - pw * 0.4), sc.Y(base - h * 1.75), sc.X(mid + pw * 0.4), sc.Y(base - h * 1.2)], 180, 360, fill=(110, 125, 120, 255))
    for i in range(10):
        x = x0 + 0.01 + i * (x1 - x0) / 10
        if abs(x - mid) > pw:
            sc.d.rectangle([sc.X(x), sc.Y(base - h * 0.7), sc.X(x + 0.01), sc.Y(base - h * 0.35)], fill=stone(sc, 0.7))

def market(sc, base, h):
    s = stone(sc, 1.0)
    sc.d.rectangle([0, sc.Y(base - h), sc.w, sc.Y(base)], fill=s)
    n = 7
    for i in range(n):
        x0 = i / n
        sc.d.chord([sc.X(x0 + 0.01), sc.Y(base - h * 1.5), sc.X(x0 + 1 / n - 0.01), sc.Y(base - h * 0.5)], 180, 360, fill=(180, 205, 220, 255))
        sc.d.arc([sc.X(x0 + 0.01), sc.Y(base - h * 1.5), sc.X(x0 + 1 / n - 0.01), sc.Y(base - h * 0.5)], 180, 360, fill=(90, 96, 104, 255), width=S * 4)
        sc.d.rectangle([sc.X(x0 + 0.03), sc.Y(base - h * 0.7), sc.X(x0 + 1 / n - 0.03), sc.Y(base)], fill=(60, 50, 44, 255))
        sc.d.rectangle([sc.X(x0 + 0.035), sc.Y(base - h * 0.65), sc.X(x0 + 1 / n - 0.035), sc.Y(base - h * 0.15)], fill=(255, 214, 140, 200))
    for i in range(n + 1):
        sc.d.rectangle([sc.X(i / n - 0.008), sc.Y(base - h), sc.X(i / n + 0.008), sc.Y(base)], fill=stone(sc, 0.85))

def people(sc, base, n, color=(40, 42, 50, 230)):
    for _ in range(n):
        x = random.uniform(0.02, 0.98)
        h = random.uniform(0.07, 0.11)
        X, B, H = sc.X(x), sc.Y(base + random.uniform(0, 0.05)), sc.Y(h)
        sc.d.rounded_rectangle([X - H // 7, B - int(H * 0.82), X + H // 7, B], radius=H // 8, fill=color)
        sc.d.ellipse([X - H // 9, B - H, X + H // 9, B - int(H * 0.8)], fill=color)

# ------------------------------------------------------------------ the photos
def photo(name, w, h, palette, draw):
    sc = Scene(w, h, palette)
    draw(sc)
    return sc.save(name)

def p_trafalgar(sc):
    sc.clouds(3); sc.sun(0.82, 0.16, 0.035)
    gallery(sc, 0.05, 0.95, 0.66, 0.18)
    sc.ground(0.66, "#B9AE9A")
    nelson(sc, 0.36, 0.78, 0.72)
    fountain(sc, 0.72, 0.86, 0.36)
    people(sc, 0.86, 9)

def p_gallery(sc):
    sc.clouds(4)
    gallery(sc, -0.1, 1.1, 0.72, 0.3)
    sc.ground(0.72, "#BDB29E")
    people(sc, 0.82, 12)

def p_covent(sc):
    sc.clouds(2)
    market(sc, 0.82, 0.35)
    sc.ground(0.82, "#8C8378")
    people(sc, 0.88, 14)

def p_bigben(sc):
    sc.clouds(3); sc.sun(0.2, 0.2, 0.04)
    sc.skyline(0.74, sc.p["far"], 0.03, 0.08)
    parliament(sc, -0.1, 0.42, 0.78, 0.11)
    big_ben(sc, 0.62, 0.78, 0.66)
    sc.ground(0.78, "#7D7A72")
    people(sc, 0.86, 6)
    sc.lamp(0.88, 0.95, 0.4)

def p_bridge(sc):
    sc.clouds(3); sc.sun(0.15, 0.18, 0.03)
    sc.skyline(0.64, sc.p["far"], 0.02, 0.07)
    parliament(sc, 0.0, 0.62, 0.66, 0.09)
    big_ben(sc, 0.7, 0.66, 0.42)
    sc.water(0.66)
    for i in range(6):
        x = -0.05 + i * 0.22
        sc.d.chord([sc.X(x), sc.Y(0.72), sc.X(x + 0.2), sc.Y(0.88)], 180, 360, fill=(60, 110, 80, 255))
    sc.d.rectangle([0, sc.Y(0.72), sc.w, sc.Y(0.76)], fill=(70, 125, 92, 255))
    sc.railing(0.86)

def p_eye(sc):
    sc.clouds(4); sc.sun(0.85, 0.12, 0.03)
    sc.skyline(0.74, sc.p["far"], 0.02, 0.06)
    london_eye(sc, 0.52, 0.42, 0.3)
    sc.water(0.74)
    sc.tree(0.06, 0.95, 0.09); sc.tree(0.95, 0.97, 0.1)

def p_eye2(sc):
    sc.clouds(2)
    london_eye(sc, 0.3, 0.5, 0.5)
    sc.ground(0.86, "#5E7B55")
    for x in (0.7, 0.82, 0.94):
        sc.tree(x, 0.92, 0.07)
    people(sc, 0.9, 7)

def p_stpauls(sc):
    sc.clouds(3); sc.sun(0.8, 0.2, 0.035)
    sc.skyline(0.66, sc.p["far"], 0.03, 0.12)
    st_pauls(sc, 0.62, 0.66, 0.17)
    sc.skyline(0.68, sc.p["mid"], 0.0, 0.05, 0.05, 0.12)
    sc.water(0.68)
    sc.d.rectangle([0, sc.Y(0.62), sc.X(0.34), sc.Y(0.65)], fill=(150, 150, 150, 255))
    for i in range(4):
        sc.d.rectangle([sc.X(0.02 + i * 0.09), sc.Y(0.65), sc.X(0.05 + i * 0.09), sc.Y(0.72)], fill=(130, 130, 130, 255))
    sc.railing(0.88)

def p_tate(sc):
    sc.clouds(3); sc.sun(0.12, 0.15, 0.03)
    tate(sc, 0.1, 0.9, 0.82, 0.26)
    sc.ground(0.82, "#77806D")
    sc.tree(0.05, 0.92, 0.07); sc.tree(0.2, 0.93, 0.06); sc.tree(0.84, 0.93, 0.07)
    people(sc, 0.86, 8)

def p_millennium(sc):
    sc.clouds(2)
    sc.skyline(0.5, sc.p["far"], 0.03, 0.1)
    st_pauls(sc, 0.5, 0.5, 0.22)
    millennium_bridge(sc)
    people(sc, 0.78, 5)

def p_globe(sc):
    sc.clouds(3); sc.sun(0.85, 0.15, 0.03)
    sc.skyline(0.7, sc.p["far"], 0.05, 0.12)
    globe(sc, 0.5, 0.8, 0.6, 0.3)
    sc.ground(0.8, "#8A8070")
    people(sc, 0.86, 10)

def p_shard(sc):
    sc.clouds(3)
    shard(sc, 0.5, 0.86, 0.78)
    sc.skyline(0.86, sc.p["mid"], 0.05, 0.22, 0.06, 0.14)
    sc.ground(0.86, "#6B6460")
    sc.lamp(0.12, 0.98, 0.3)

def p_belfast(sc):
    sc.clouds(3); sc.sun(0.2, 0.22, 0.035)
    sc.skyline(0.6, sc.p["far"], 0.04, 0.16)
    sc.water(0.6)
    belfast(sc, 0.05, 0.95, 0.74)
    sc.railing(0.9)

def p_towerbridge(sc):
    sc.clouds(2); sc.sun(0.15, 0.3, 0.04)
    sc.skyline(0.64, sc.p["far"], 0.02, 0.08)
    sc.water(0.64)
    tower_bridge(sc, 0.5, 0.66, 0.42)
    sc.railing(0.88)

def p_towerbridge2(sc):
    sc.clouds(2); sc.sun(0.85, 0.32, 0.04)
    sc.water(0.7)
    tower_bridge(sc, 0.48, 0.72, 0.62)
    sc.railing(0.9)

def p_tower_london(sc):
    sc.sun(0.82, 0.6, 0.03)
    sc.skyline(0.7, sc.p["far"], 0.04, 0.16)
    tower_of_london(sc, 0.5, 0.82, 0.55)
    sc.ground(0.82, "#3C4A3A")
    sc.lamp(0.1, 0.95, 0.35); sc.lamp(0.9, 0.95, 0.35)

L, P = (1800, 1200), (1200, 1600)
SHOTS = [
    # name, size, palette, draw, local time or route key, pre-tagged location
    ("DSCF4301.JPG", L, "noon", p_trafalgar, datetime(2026, 9, 12, 12, 14, 8, tzinfo=BST), (51.50797, -0.12804)),
    ("DSCF4304.JPG", L, "noon", p_gallery, datetime(2026, 9, 12, 12, 21, 40, tzinfo=BST), (51.50886, -0.12835)),
    ("DSCF4310.JPG", L, "noon", p_covent, datetime(2026, 9, 12, 12, 58, 3, tzinfo=BST), (51.51195, -0.12271)),
    ("DSCF4322.JPG", P, "after", p_bigben, "bigben", None),
    ("DSCF4325.JPG", L, "after", p_bridge, "bridge", None),
    ("DSCF4331.JPG", L, "after", p_eye, "eye", None),
    ("DSCF4333.JPG", P, "after", p_eye2, "eye2", None),
    ("DSCF4340.JPG", L, "after", p_stpauls, "stpauls_view", None),
    ("DSCF4347.JPG", L, "after", p_tate, "tate", None),
    ("DSCF4349.JPG", P, "after", p_millennium, "millennium", None),
    ("DSCF4352.JPG", L, "golden", p_globe, "globe", None),
    ("DSCF4358.JPG", P, "golden", p_shard, "shard", None),
    ("DSCF4363.JPG", L, "golden", p_belfast, "belfast", None),
    ("DSCF4368.JPG", L, "golden", p_towerbridge, "towerbridge", None),
    ("DSCF4371.JPG", P, "golden", p_towerbridge2, "towerbridge2", None),
    ("DSCF4380.JPG", L, "dusk", p_tower_london, datetime(2026, 9, 12, 16, 52, 30, tzinfo=BST), None),
]

for name, size, palette, draw, when, loc in SHOTS:
    path = photo(name, size[0], size[1], palette, draw)
    if isinstance(when, str):
        when = photo_times[when]
    args = [EXIFTOOL, "-q", "-overwrite_original", "-Make=FUJIFILM", "-Model=X-T5", "-LensModel=XF23mmF1.4 R LM WR",
            f"-DateTimeOriginal={when.strftime('%Y:%m:%d %H:%M:%S')}", f"-CreateDate={when.strftime('%Y:%m:%d %H:%M:%S')}",
            "-FNumber=5.6", "-ExposureTime=1/500", "-ISO=160", "-FocalLength=23"]
    if loc:
        lat, lon = loc
        args += [f"-GPSLatitude={abs(lat)}", f"-GPSLatitudeRef={'N' if lat >= 0 else 'S'}",
                 f"-GPSLongitude={abs(lon)}", f"-GPSLongitudeRef={'E' if lon >= 0 else 'W'}"]
    args.append(path)
    subprocess.run(args, check=True)
    print(name, when.strftime("%H:%M:%S"), "tagged" if loc else "")
