#!/usr/bin/env python3
"""Summarize grey text tones in terminal screenshots.

Usage:
  python scripts/extract-terminal-tones.py IMAGE [IMAGE ...]
"""
import json
import statistics
import sys
from collections import Counter
from pathlib import Path

from PIL import Image


def luma(rgb):
    r, g, b = rgb
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def quantize(value, step=8):
    return max(0, min(255, round(value / step) * step))


def analyze(path):
    image = Image.open(path).convert("RGB")
    pixels = list(image.getdata())
    grey_pixels = []
    all_non_background = []

    for rgb in pixels:
        y = luma(rgb)
        if y < 35:
            continue

        all_non_background.append(rgb)
        if max(rgb) - min(rgb) <= 35:
            grey_pixels.append(rgb)

    grey_luma = [luma(rgb) for rgb in grey_pixels]
    quantized = Counter(
        (quantize(rgb[0]), quantize(rgb[1]), quantize(rgb[2])) for rgb in grey_pixels
    )

    def percentile(values, fraction):
        if not values:
            return None
        ordered = sorted(values)
        index = min(len(ordered) - 1, int(len(ordered) * fraction))
        return round(ordered[index], 2)

    return {
        "file": str(path),
        "size": {"width": image.width, "height": image.height},
        "nonBackgroundPixels": len(all_non_background),
        "greyishPixels": len(grey_pixels),
        "greyishLuma": {
            "mean": round(statistics.mean(grey_luma), 2) if grey_luma else None,
            "p50": percentile(grey_luma, 0.50),
            "p95": percentile(grey_luma, 0.95),
            "max": round(max(grey_luma), 2) if grey_luma else None,
        },
        "topGreyishTones": [
            {"rgb": list(rgb), "count": count}
            for rgb, count in quantized.most_common(12)
        ],
    }


def main():
    if len(sys.argv) < 2:
        raise SystemExit("Usage: extract-terminal-tones.py IMAGE [IMAGE ...]")

    results = [analyze(Path(arg)) for arg in sys.argv[1:]]
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
