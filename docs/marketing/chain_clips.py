#!/usr/bin/env python3
"""
FFmpeg Clip Chainer for tlbx Marketing Videos

Chains 2-3 video clips into a single video with optional crossfade.

Usage:
    python chain_clips.py clip1_dir clip2_dir clip3_dir -o final.mp4
    python chain_clips.py clip1_dir clip2_dir --crossfade 0.5 -o final.mp4

Prerequisites:
    ffmpeg must be installed and in PATH
"""

import os
import sys
import subprocess
import argparse
from pathlib import Path
from tempfile import NamedTemporaryFile


def check_ffmpeg():
    """Verify ffmpeg is available."""
    try:
        result = subprocess.run(
            ["ffmpeg", "-version"],
            capture_output=True,
            text=True
        )
        if result.returncode != 0:
            print("ERROR: ffmpeg not working correctly")
            sys.exit(1)
    except FileNotFoundError:
        print("ERROR: ffmpeg not found. Install it and add to PATH.")
        sys.exit(1)


def find_clip_video(clip_dir: Path) -> Path:
    """Find the video file in a clip directory."""
    for name in ["clip.mp4", "video.mp4"]:
        video_path = clip_dir / name
        if video_path.exists():
            return video_path

    mp4_files = list(clip_dir.glob("*.mp4"))
    if mp4_files:
        return mp4_files[0]

    print(f"ERROR: No video file found in {clip_dir}")
    sys.exit(1)


def concat_simple(clip_paths: list[Path], output_path: Path):
    """
    Simple concatenation using concat demuxer.
    Fast, no re-encoding if formats match.
    """
    print("Using simple concatenation (concat demuxer)")

    with NamedTemporaryFile(mode='w', suffix='.txt', delete=False) as f:
        for clip in clip_paths:
            f.write(f"file '{clip.absolute()}'\n")
        list_file = f.name

    try:
        cmd = [
            "ffmpeg", "-y",
            "-f", "concat",
            "-safe", "0",
            "-i", list_file,
            "-c", "copy",
            str(output_path)
        ]
        print(f"  Running: {' '.join(cmd)}")
        result = subprocess.run(cmd, capture_output=True, text=True)

        if result.returncode != 0:
            print(f"ERROR: ffmpeg failed\n{result.stderr}")
            sys.exit(1)

        print(f"  Output: {output_path}")
    finally:
        os.unlink(list_file)


def concat_with_crossfade(clip_paths: list[Path], output_path: Path, fade_duration: float):
    """
    Concatenation with crossfade transitions.
    Requires re-encoding but produces smooth transitions.
    """
    print(f"Using crossfade concatenation (fade: {fade_duration}s)")

    if len(clip_paths) == 2:
        v1, v2 = clip_paths
        cmd = [
            "ffmpeg", "-y",
            "-i", str(v1),
            "-i", str(v2),
            "-filter_complex",
            f"[0:v][1:v]xfade=transition=fade:duration={fade_duration}:offset=3.5[v]",
            "-map", "[v]",
            "-c:v", "libx264",
            "-preset", "fast",
            "-crf", "23",
            str(output_path)
        ]
    elif len(clip_paths) == 3:
        v1, v2, v3 = clip_paths
        cmd = [
            "ffmpeg", "-y",
            "-i", str(v1),
            "-i", str(v2),
            "-i", str(v3),
            "-filter_complex",
            f"[0:v][1:v]xfade=transition=fade:duration={fade_duration}:offset=3.5[v01];"
            f"[v01][2:v]xfade=transition=fade:duration={fade_duration}:offset=7[v]",
            "-map", "[v]",
            "-c:v", "libx264",
            "-preset", "fast",
            "-crf", "23",
            str(output_path)
        ]
    else:
        print("ERROR: Crossfade only supports 2-3 clips")
        sys.exit(1)

    print(f"  Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print(f"ERROR: ffmpeg failed\n{result.stderr}")
        sys.exit(1)

    print(f"  Output: {output_path}")


def concat_with_reencode(clip_paths: list[Path], output_path: Path):
    """
    Concatenation with re-encoding for format consistency.
    Use when clips have different codecs/formats.
    """
    print("Using re-encode concatenation (filter_complex concat)")

    inputs = []
    filter_inputs = ""
    for i, clip in enumerate(clip_paths):
        inputs.extend(["-i", str(clip)])
        filter_inputs += f"[{i}:v]"

    filter_complex = f"{filter_inputs}concat=n={len(clip_paths)}:v=1[v]"

    cmd = [
        "ffmpeg", "-y",
        *inputs,
        "-filter_complex", filter_complex,
        "-map", "[v]",
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "23",
        str(output_path)
    ]

    print(f"  Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print(f"ERROR: ffmpeg failed\n{result.stderr}")
        sys.exit(1)

    print(f"  Output: {output_path}")


def get_video_duration(video_path: Path) -> float:
    """Get video duration in seconds using ffprobe."""
    cmd = [
        "ffprobe",
        "-v", "error",
        "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1",
        str(video_path)
    ]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode == 0:
        return float(result.stdout.strip())
    return 0.0


def chain_clips(clip_dirs: list[Path], output_path: Path, crossfade: float = 0, reencode: bool = False):
    """
    Chain multiple clips into a single video.

    Args:
        clip_dirs: List of directories containing clip.mp4 files
        output_path: Output video file path
        crossfade: Crossfade duration in seconds (0 = no crossfade)
        reencode: Force re-encoding even without crossfade
    """
    print(f"\n{'='*60}")
    print(f"CHAINING {len(clip_dirs)} CLIPS")
    print(f"{'='*60}\n")

    clip_paths = []
    total_duration = 0.0

    for clip_dir in clip_dirs:
        clip_dir = Path(clip_dir)
        if not clip_dir.exists():
            print(f"ERROR: Directory not found: {clip_dir}")
            sys.exit(1)

        video_path = find_clip_video(clip_dir)
        clip_paths.append(video_path)

        duration = get_video_duration(video_path)
        total_duration += duration
        print(f"  Found: {video_path} ({duration:.1f}s)")

    print(f"\n  Total input duration: {total_duration:.1f}s")
    print(f"  Output: {output_path}\n")

    output_path.parent.mkdir(parents=True, exist_ok=True)

    if crossfade > 0:
        concat_with_crossfade(clip_paths, output_path, crossfade)
    elif reencode:
        concat_with_reencode(clip_paths, output_path)
    else:
        concat_simple(clip_paths, output_path)

    final_duration = get_video_duration(output_path)
    print(f"\n{'='*60}")
    print(f"CHAIN COMPLETE")
    print(f"  Output: {output_path}")
    print(f"  Duration: {final_duration:.1f}s")
    print(f"  Size: {output_path.stat().st_size / 1024:.0f} KB")
    print(f"{'='*60}\n")

    return output_path


def main():
    parser = argparse.ArgumentParser(description="Chain video clips into a single video")
    parser.add_argument("clips", nargs="+", help="Clip directories (2-3)")
    parser.add_argument("-o", "--output", required=True, help="Output video path")
    parser.add_argument("--crossfade", type=float, default=0, help="Crossfade duration in seconds")
    parser.add_argument("--reencode", action="store_true", help="Force re-encoding")

    args = parser.parse_args()

    if len(args.clips) < 2:
        print("ERROR: Need at least 2 clips to chain")
        sys.exit(1)

    if len(args.clips) > 3:
        print("WARNING: Only first 3 clips will be used")
        args.clips = args.clips[:3]

    check_ffmpeg()

    clip_dirs = [Path(c) for c in args.clips]
    output_path = Path(args.output)

    chain_clips(clip_dirs, output_path, args.crossfade, args.reencode)


if __name__ == "__main__":
    main()
