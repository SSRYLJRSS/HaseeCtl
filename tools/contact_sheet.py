from PIL import Image
import glob, os

files = sorted(glob.glob(os.path.join("_shots", "pages", "*.png")))
files = [f for f in files if not f.endswith(("0-dashboard.png", "1-fan.png", "_sheet.png"))]
ims = [Image.open(f).resize((600, 432), Image.LANCZOS) for f in files]
cols, rows = 2, (len(ims) + 1) // 2
sheet = Image.new("RGB", (cols * 610 + 10, rows * 442 + 10), (30, 30, 30))
for i, im in enumerate(ims):
    sheet.paste(im, (10 + (i % cols) * 610, 10 + (i // cols) * 442))
out = os.path.join("_shots", "pages", "_sheet.png")
sheet.save(out)
print([os.path.basename(f) for f in files])
