from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def build_icon(source_path: Path, png_path: Path, ico_path: Path) -> None:
    source = Image.open(source_path).convert("RGBA")
    width, height = source.size
    if width != height:
        raise ValueError("Icon source must be square.")

    # Candidate A was generated as a rounded-square tile on a white canvas.
    # Rebuild only the outer alpha edge; preserve the selected artwork itself.
    scale = 4
    inset = 1
    radius = round(width * 0.135)
    mask_large = Image.new("L", (width * scale, height * scale), 0)
    draw = ImageDraw.Draw(mask_large)
    draw.rounded_rectangle(
        (
            inset * scale,
            inset * scale,
            (width - 1 - inset) * scale,
            (height - 1 - inset) * scale,
        ),
        radius=radius * scale,
        fill=255,
    )
    mask = mask_large.resize((width, height), Image.Resampling.LANCZOS)
    source.putalpha(mask)

    png_path.parent.mkdir(parents=True, exist_ok=True)
    source.save(png_path, "PNG", optimize=True)
    source.save(ico_path, "ICO", sizes=[(size, size) for size in ICON_SIZES])


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the selected Document Tailor Windows icon.")
    parser.add_argument("source", type=Path)
    parser.add_argument("png", type=Path)
    parser.add_argument("ico", type=Path)
    args = parser.parse_args()
    build_icon(args.source, args.png, args.ico)


if __name__ == "__main__":
    main()
