import os
import struct
from PIL import Image, ImageDraw

BG = (10, 10, 10, 255)
FG = (255, 255, 255, 255)
BORDER = (58, 58, 58, 255)


def render(size: int) -> Image.Image:
    """Render the mark at `size` from a 4x supersample so every size stays crisp."""
    S = size * 4
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = S / 1024.0

    def sc(v):
        return int(round(v * k))

    d.rounded_rectangle((0, 0, S - 1, S - 1), radius=sc(230), fill=BG)
    d.rounded_rectangle((sc(6), sc(6), S - sc(7), S - sc(7)), radius=sc(227),
                        outline=BORDER, width=max(1, sc(4)))

    d.rounded_rectangle((sc(272), sc(240), sc(400), sc(784)), radius=sc(34), fill=FG)
    d.rounded_rectangle((sc(624), sc(240), sc(752), sc(784)), radius=sc(34), fill=FG)
    d.rounded_rectangle((sc(400), sc(432), sc(624), sc(592)), radius=sc(30), fill=FG)

    # hub hole only where it is still legible
    if size >= 96:
        d.ellipse((sc(512 - 50), sc(512 - 50), sc(512 + 50), sc(512 + 50)), fill=BG)

    return img.resize((size, size), Image.LANCZOS)


def bmp_frame(img: Image.Image) -> bytes:
    """32bpp DIB frame as used inside .ico files (bottom-up BGRA + empty AND mask)."""
    w, h = img.size
    px = img.convert("RGBA").load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    xor = b"".join(rows)
    mask_row = ((w + 31) // 32) * 4
    and_mask = b"\x00" * (mask_row * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + and_mask


def write_ico(path: str, frames: list[Image.Image]) -> None:
    entries, blobs = [], []
    offset = 6 + 16 * len(frames)
    for img in frames:
        w, h = img.size
        if w >= 256:  # PNG is the Windows convention for 256px only
            import io
            buf = io.BytesIO()
            img.save(buf, format="PNG")
            blob = buf.getvalue()
        else:
            blob = bmp_frame(img)
        entries.append(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


sizes = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]
frames = [render(n) for n in sizes]

for base in (r"src\HaseeCtl.App\Assets", r"src\HaseeCtl.Agent\Assets"):
    os.makedirs(base, exist_ok=True)
    write_ico(os.path.join(base, "app.ico"), frames)

app_assets = r"src\HaseeCtl.App\Assets"
frames[0].save(os.path.join(app_assets, "app-256.png"))

strip = Image.new("RGBA", (16 + 20 + 24 + 32 + 48 + 64 + 96 + 128 + 7 * 10 + 8, 140), (245, 245, 245, 255))
x = 6
for n in [16, 20, 24, 32, 48, 64, 96, 128]:
    strip.alpha_composite(render(n), (x, 6))
    x += n + 10
strip.save(os.path.join(app_assets, "sizes-preview.png"))

print("ok", os.path.getsize(os.path.join(app_assets, "app.ico")))
