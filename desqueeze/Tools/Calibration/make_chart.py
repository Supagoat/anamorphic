"""Makes the printable calibration chart and a simulated anamorphic photo of it.

    python3 make_chart.py [out_dir] [photo_background.png]

calibration-chart.png      A4 landscape at 300 dpi: one large black circle. Print at 100% (no "fit to page").
sample-squeezed-1.6x.jpg   The chart photographed through a 1.6x anamorphic lens (simulated: area-integrated
                           squeeze, lens blur, sensor noise, JPEG), for trying Calibrate without a lens.
"""
import io, os, sys, numpy as np
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
bg_path = sys.argv[2] if len(sys.argv) > 2 else None
os.makedirs(out, exist_ok=True)

def font(size):
    for f in ["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/Library/Fonts/Arial.ttf", "C:/Windows/Fonts/arial.ttf"]:
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()

# ---------------------------------------------------------------- printable chart
W, H, DPI = 3508, 2480, 300                  # A4 landscape
mm = DPI / 25.4
ss = 4
big = Image.new("L", (W * ss, H * ss), 255)
d = ImageDraw.Draw(big)
r = 70 * mm * ss                             # 140 mm circle
cx, cy = W * ss / 2, (H * ss) / 2 - 8 * mm * ss
d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=0)
chart = big.resize((W, H), Image.LANCZOS)
d = ImageDraw.Draw(chart)
f1, f2 = font(int(5 * mm)), font(int(3.2 * mm))
d.text((14 * mm, H - 30 * mm), "Desqueeze calibration chart", fill=0, font=f1)
d.text((14 * mm, H - 21 * mm),
       "Print at 100% on A4 or larger and lay it flat. Photograph it straight on through the anamorphic lens, with the circle",
       fill=90, font=f2)
d.text((14 * mm, H - 15.5 * mm),
       "near the middle of the frame and about half the frame height. Open the photo, press Calibrate and click inside the circle.",
       fill=90, font=f2)
d.text((W - 72 * mm, 14 * mm), "circle diameter 140 mm", fill=120, font=f2)
chart.save(os.path.join(out, "calibration-chart.png"), dpi=(DPI, DPI), optimize=True)

# ---------------------------------------------------------------- simulated photo of the chart, squeezed 1.6x
def to_lin(s):
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)

def from_lin(l):
    l = np.clip(l, 0, 1)
    return np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)

f = 1.6
cw, ch = 2400, 1600                          # captured (squeezed) size
sw = int(cw * f)                             # scene width before the squeeze
ss = 2
rng = np.random.default_rng(3)
if bg_path:
    scene = np.asarray(Image.open(bg_path).convert("RGB").resize((sw * ss, ch * ss), Image.BICUBIC)).astype(np.float32) / 255
    scene = to_lin(scene) * 0.8
else:
    yy, xx = np.mgrid[0:ch * ss, 0:sw * ss].astype(np.float32)
    scene = np.stack([0.18 + 0.05 * xx / xx.max(), 0.16 + 0.04 * yy / yy.max(), np.full_like(xx, 0.13)], -1)
# the chart on the table: paper rectangle, 0.83 x 0.83 of the frame height, centred slightly off-centre
paper_h = int(0.83 * ch * ss); paper_w = int(paper_h * W / H)
px0, py0 = int(sw * ss * 0.5 - paper_w / 2 + 40 * ss), int(ch * ss * 0.5 - paper_h / 2)
paper = np.asarray(chart.resize((paper_w, paper_h), Image.BICUBIC)).astype(np.float32) / 255
shade = 0.92 - 0.06 * np.linspace(0, 1, paper_w)[None, :]
paper_lin = to_lin(paper) * shade
scene[py0:py0 + paper_h, px0:px0 + paper_w] = paper_lin[..., None] * np.array([1.0, 0.98, 0.95], np.float32)
# sensor: average ss rows, integrate columns over f*ss scene pixels
img = scene.reshape(ch, ss, sw * ss, 3).mean(1)
cs = np.concatenate([np.zeros((ch, 1, 3)), np.cumsum(img, 1, dtype=np.float64)], 1)
edges = np.arange(cw + 1) * f * ss
i0 = np.minimum(np.floor(edges).astype(int), sw * ss - 1); fr = edges - i0
v = cs[:, i0] + fr[None, :, None] * (cs[:, i0 + 1] - cs[:, i0])
cap = (v[:, 1:] - v[:, :-1]) / (f * ss)
from scipy import ndimage
cap = ndimage.gaussian_filter(cap, (0.55, 0.55, 0))
s = from_lin(cap) + rng.normal(0, 0.006, cap.shape)
Image.fromarray((np.clip(s, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, "sample-squeezed-1.6x.jpg"), quality=93)
print("wrote", os.path.join(out, "calibration-chart.png"), "and", os.path.join(out, "sample-squeezed-1.6x.jpg"))
