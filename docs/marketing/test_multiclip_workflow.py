#!/usr/bin/env python3
"""
Multi-Clip Video Workflow with Audio

Generates a seamless multi-clip video where:
- Each clip's last frame becomes the next clip's first frame (continuity)
- Audio is enabled for ambient sound and effects
- Reference assets (screenshots, logos, memes) can be passed to influence generation

Example scene: Developer discovers tlbx
- Clip 1: Frustrated at desk (stuck) -> Has an idea (lightbulb moment)
- Clip 2: Has idea -> Typing excitedly on laptop
- Clip 3: Typing -> Celebrating with arms raised

Models:
- Image: gemini-3-pro-image-preview (location: global)
- Video: veo-3.1-generate-001 (location: us-central1)

Usage:
    python test_multiclip_workflow.py [--assets file1.png file2.png ...]
"""

import os
import sys
import time
import base64
import argparse
import subprocess
from pathlib import Path
from dataclasses import dataclass, field


@dataclass
class KeyFrame:
    """A keyframe in the video sequence."""
    name: str
    prompt: str
    path: Path = None


@dataclass
class ClipDefinition:
    """A video clip connecting two keyframes."""
    name: str
    prompt: str  # Video motion/action prompt with audio cues
    duration: int = 4


@dataclass
class Assets:
    """Reference assets to influence image generation."""
    files: list[Path] = field(default_factory=list)
    _loaded: dict = field(default_factory=dict, repr=False)

    def load(self):
        """Load all asset files into memory."""
        from google.genai import types
        for f in self.files:
            if f.exists():
                with open(f, "rb") as fp:
                    mime = "image/png" if f.suffix.lower() == ".png" else "image/jpeg"
                    self._loaded[f.name] = types.Part.from_bytes(data=fp.read(), mime_type=mime)
                print(f"  Loaded asset: {f.name}")
            else:
                print(f"  WARNING: Asset not found: {f}")

    def get_parts(self) -> list:
        """Get all loaded assets as Parts for Gemini."""
        return list(self._loaded.values())


def setup_image_client():
    """Initialize client for image generation (requires global location)."""
    from google import genai
    from google.oauth2.service_account import Credentials

    project_id = os.environ.get("VERTEX_AI_PROJECT_ID")
    service_account_path = os.environ.get("VERTEX_AI_SERVICE_ACCOUNT_JSON")

    if not project_id or not service_account_path:
        print("ERROR: Set VERTEX_AI_PROJECT_ID and VERTEX_AI_SERVICE_ACCOUNT_JSON")
        sys.exit(1)

    credentials = Credentials.from_service_account_file(
        service_account_path,
        scopes=["https://www.googleapis.com/auth/cloud-platform"]
    )

    return genai.Client(
        vertexai=True,
        project=project_id,
        location="global",
        credentials=credentials,
    )


def setup_video_client():
    """Initialize client for video generation (requires us-central1)."""
    from google import genai
    from google.oauth2.service_account import Credentials

    project_id = os.environ.get("VERTEX_AI_PROJECT_ID")
    service_account_path = os.environ.get("VERTEX_AI_SERVICE_ACCOUNT_JSON")

    credentials = Credentials.from_service_account_file(
        service_account_path,
        scopes=["https://www.googleapis.com/auth/cloud-platform"]
    )

    return genai.Client(
        vertexai=True,
        project=project_id,
        location="us-central1",
        credentials=credentials,
    )


def generate_keyframe(client, keyframe: KeyFrame, reference_image: Path, output_dir: Path, assets: Assets = None) -> Path:
    """Generate a keyframe image, optionally using a reference for consistency and assets for branding."""
    from google.genai import types

    print(f"\n{'='*60}")
    print(f"KEYFRAME: {keyframe.name}")
    print(f"{'='*60}")
    print(f"Prompt: {keyframe.prompt}")

    contents = []

    # Add reference assets (screenshots, logos, etc.) first
    if assets and assets.get_parts():
        contents.extend(assets.get_parts())
        print(f"Using {len(assets.get_parts())} reference asset(s)")

    # Add previous keyframe for character/scene consistency
    if reference_image and reference_image.exists():
        print(f"Using previous keyframe: {reference_image.name}")
        with open(reference_image, "rb") as f:
            contents.append(types.Part.from_bytes(data=f.read(), mime_type="image/png"))

    contents.append(keyframe.prompt)

    response = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=contents if len(contents) > 1 else keyframe.prompt,
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio="16:9"),
        ),
    )

    output_path = output_dir / f"{keyframe.name}.png"

    for part in response.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            with open(output_path, "wb") as f:
                f.write(part.inline_data.data)
            print(f"Saved: {output_path.name} ({len(part.inline_data.data)} bytes)")
            keyframe.path = output_path
            return output_path

    print("ERROR: No image in response")
    sys.exit(1)


def generate_clip(client, clip: ClipDefinition, first_frame: Path, last_frame: Path, output_dir: Path) -> Path:
    """Generate a video clip with audio, transitioning between two keyframes."""
    from google.genai import types
    from google.genai.types import GenerateVideosConfig

    print(f"\n{'='*60}")
    print(f"CLIP: {clip.name}")
    print(f"{'='*60}")
    print(f"First frame: {first_frame.name}")
    print(f"Last frame: {last_frame.name}")
    print(f"Prompt: {clip.prompt}")
    print(f"Duration: {clip.duration}s | Audio: ENABLED")
    print("Generating (this takes 2-4 minutes)...")

    first_image = types.Image.from_file(location=str(first_frame))
    last_image = types.Image.from_file(location=str(last_frame))

    operation = client.models.generate_videos(
        model="veo-3.1-generate-001",
        prompt=clip.prompt,
        image=first_image,
        config=GenerateVideosConfig(
            aspect_ratio="16:9",
            duration_seconds=clip.duration,
            generate_audio=True,  # Audio enabled!
            resolution="720p",
            last_frame=last_image,
        ),
    )

    poll_count = 0
    while not operation.done:
        poll_count += 1
        print(f"  Waiting... (poll #{poll_count})")
        time.sleep(15)
        operation = client.operations.get(operation)

    if operation.response and operation.result.generated_videos:
        video = operation.result.generated_videos[0].video

        if hasattr(video, 'video_bytes') and video.video_bytes:
            video_bytes = video.video_bytes
            if isinstance(video_bytes, str):
                video_bytes = base64.b64decode(video_bytes)

            output_path = output_dir / f"{clip.name}.mp4"
            with open(output_path, 'wb') as f:
                f.write(video_bytes)
            print(f"Saved: {output_path.name} ({len(video_bytes)} bytes)")
            return output_path

        elif hasattr(video, 'uri') and video.uri:
            print(f"Video at GCS: {video.uri}")
            return None

    print("ERROR: No video generated")
    return None


def concatenate_clips(clips: list[Path], output_path: Path):
    """Concatenate video clips using ffmpeg."""
    print(f"\n{'='*60}")
    print("CONCATENATING CLIPS")
    print(f"{'='*60}")

    # Create file list for ffmpeg (use absolute paths)
    list_file = output_path.parent / "clips.txt"
    with open(list_file, "w") as f:
        for clip in clips:
            f.write(f"file '{clip.absolute()}'\n")

    cmd = [
        "ffmpeg", "-y",
        "-f", "concat",
        "-safe", "0",
        "-i", str(list_file.absolute()),
        "-c", "copy",
        str(output_path.absolute())
    ]

    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode == 0:
        print(f"Saved: {output_path}")
        list_file.unlink()  # Clean up
        return output_path
    else:
        print(f"ffmpeg error: {result.stderr}")
        return None


def parse_args():
    """Parse command line arguments."""
    parser = argparse.ArgumentParser(description="Multi-clip video workflow with audio and asset references")
    parser.add_argument(
        "--assets", "-a",
        nargs="*",
        default=[],
        help="Reference asset files (screenshots, logos, memes) to influence image generation"
    )
    parser.add_argument(
        "--output", "-o",
        default="output/multiclip_demo",
        help="Output directory (default: output/multiclip_demo)"
    )
    return parser.parse_args()


def main():
    args = parse_args()

    print("="*60)
    print("MULTI-CLIP VIDEO WORKFLOW WITH AUDIO")
    print("Scene: Developer discovers tlbx")
    print("="*60)

    # Load reference assets
    assets = Assets(files=[Path(f) for f in args.assets])
    if assets.files:
        print(f"\nLoading {len(assets.files)} reference asset(s)...")
        assets.load()
    else:
        print("\nNo reference assets provided (use --assets to add screenshots, logos, etc.)")

    # Define the keyframes (A -> B -> C -> D)
    # Each keyframe prompt maintains the same person/setting
    # When assets are provided, prompts reference them for screen content
    base_scene = "young developer with short dark hair, modern home office with dual monitors, warm ambient lighting, photorealistic"

    # Adjust prompts based on whether we have assets
    screen_ref = "displaying the terminal application from the reference images" if assets.files else "with code on screen"

    keyframes = [
        KeyFrame(
            name="kf1_frustrated",
            prompt=f"{base_scene}, sitting at desk looking frustrated, hand on forehead, monitor {screen_ref}, staring at error messages"
        ),
        KeyFrame(
            name="kf2_idea",
            prompt=f"Same person from reference image, {base_scene}, sitting up straight with excited expression, finger pointing up in 'eureka' moment, eyes wide, monitor {screen_ref}"
        ),
        KeyFrame(
            name="kf3_typing",
            prompt=f"Same person from reference image, {base_scene}, leaning forward typing enthusiastically on keyboard, focused happy expression, monitor {screen_ref}"
        ),
        KeyFrame(
            name="kf4_celebrating",
            prompt=f"Same person from reference image, {base_scene}, standing next to desk with both arms raised in victory pose, huge smile, celebrating success, monitor {screen_ref}"
        ),
    ]

    # Define the clips (transitions between keyframes)
    # Prompts include audio cues
    clips = [
        ClipDefinition(
            name="clip1_frustration_to_idea",
            prompt='Person sighs in frustration then suddenly has a realization, sits up with excitement. SFX: frustrated sigh, then "aha!" moment. Ambient keyboard sounds.',
            duration=4
        ),
        ClipDefinition(
            name="clip2_idea_to_typing",
            prompt='Person quickly turns to keyboard and starts typing excitedly, leaning in with focus. SFX: rapid keyboard clicking, mouse clicks. Ambient office sounds.',
            duration=4
        ),
        ClipDefinition(
            name="clip3_typing_to_celebration",
            prompt='Person finishes typing, sees success on screen, pushes back chair and stands up with arms raised in celebration. SFX: final keyboard tap, chair rolling, "Yes!" exclamation.',
            duration=4
        ),
    ]

    # Setup
    image_client = setup_image_client()
    video_client = setup_video_client()

    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"\nOutput: {output_dir.absolute()}")

    # Generate keyframes (each uses the previous as reference for consistency)
    print("\n" + "="*60)
    print("PHASE 1: GENERATING KEYFRAMES")
    print("="*60)

    reference = None
    for kf in keyframes:
        generate_keyframe(image_client, kf, reference, output_dir, assets)
        reference = kf.path  # Use this frame as reference for next

    # Generate video clips
    # Key insight: clip N uses keyframe N as first_frame and keyframe N+1 as last_frame
    print("\n" + "="*60)
    print("PHASE 2: GENERATING VIDEO CLIPS WITH AUDIO")
    print("="*60)

    clip_paths = []
    for i, clip in enumerate(clips):
        first_frame = keyframes[i].path
        last_frame = keyframes[i + 1].path  # Continuity: end of clip N = start of clip N+1

        clip_path = generate_clip(video_client, clip, first_frame, last_frame, output_dir)
        if clip_path:
            clip_paths.append(clip_path)

    # Concatenate clips
    if len(clip_paths) == len(clips):
        print("\n" + "="*60)
        print("PHASE 3: CONCATENATING FINAL VIDEO")
        print("="*60)
        final_video = concatenate_clips(clip_paths, output_dir / "final_midterm_discovery.mp4")
    else:
        print(f"\nWARNING: Only {len(clip_paths)}/{len(clips)} clips generated, skipping concatenation")
        final_video = None

    # Summary
    print("\n" + "="*60)
    print("COMPLETE")
    print("="*60)
    print("\nKeyframes:")
    for kf in keyframes:
        print(f"  {kf.path}")
    print("\nClips:")
    for cp in clip_paths:
        print(f"  {cp}")
    if final_video:
        print(f"\nFinal video: {final_video}")


if __name__ == "__main__":
    main()
