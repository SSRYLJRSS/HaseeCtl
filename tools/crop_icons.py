from PIL import Image
import subprocess, os

shot = os.path.join("_shots", "desk.png")
subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", "tools/shot.ps1", "-Out", os.path.abspath(shot)],
               check=True, stdout=subprocess.DEVNULL)
im = Image.open(shot)
w, h = im.size
icons = im.crop((880, 0, 1500, 200)).resize((930, 300), Image.LANCZOS)
tray = im.crop((w - 1000, h - 90, w, h)).resize((1000, 90), Image.LANCZOS)
sheet = Image.new("RGB", (1000, 300 + 100), (25, 25, 25))
sheet.paste(icons.convert("RGB"), (0, 0))
sheet.paste(tray.convert("RGB"), (0, 305))
sheet.save(os.path.join("_shots", "icons.png"))
print("ok")
