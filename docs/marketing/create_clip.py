#!/usr/bin/env python3
"""
Single Clip Generator for tlbx Marketing Videos

Creates one video clip from start/end frame descriptions.

Usage:
    python create_clip.py config.json
    python create_clip.py --clip-id test001 --start "Dev at desk" --end "Dev smiling" --transition "picks up phone"

Output:
    output/{clip_id}/start.png
    output/{clip_id}/end.png
    output/{clip_id}/clip.mp4
"""

import os
import sys
import json
import time
import base64
import random
import argparse
from pathlib import Path
from functools import wraps

MAX_RETRIES = 8
BASE_DELAY = 5
MAX_DELAY = 120


def retry_on_429(func):
    """
    Decorator that retries function on 429 RESOURCE_EXHAUSTED errors.
    Uses exponential backoff with jitter: delay = min(base * 2^attempt + jitter, max)
    """
    @wraps(func)
    def wrapper(*args, **kwargs):
        last_exception = None
        for attempt in range(MAX_RETRIES):
            try:
                return func(*args, **kwargs)
            except Exception as e:
                error_str = str(e)
                if "429" in error_str or "RESOURCE_EXHAUSTED" in error_str:
                    last_exception = e
                    delay = min(BASE_DELAY * (2 ** attempt) + random.uniform(0, 2), MAX_DELAY)
                    print(f"    [RATE LIMITED] Waiting {delay:.1f}s before retry {attempt + 1}/{MAX_RETRIES}...")
                    time.sleep(delay)
                else:
                    raise
        print(f"    [FAILED] Max retries ({MAX_RETRIES}) exceeded. Last error: {last_exception}")
        raise last_exception
    return wrapper


def setup_image_client():
    """Initialize client for image generation (requires location="global")."""
    from google import genai
    from google.oauth2.service_account import Credentials

    project_id = os.environ.get("VERTEX_AI_PROJECT_ID")
    service_account_path = os.environ.get("VERTEX_AI_SERVICE_ACCOUNT_JSON")

    if not project_id or not service_account_path:
        print("ERROR: Set VERTEX_AI_PROJECT_ID and VERTEX_AI_SERVICE_ACCOUNT_JSON")
        sys.exit(1)

    scopes = ["https://www.googleapis.com/auth/cloud-platform"]
    credentials = Credentials.from_service_account_file(service_account_path, scopes=scopes)

    return genai.Client(
        vertexai=True,
        project=project_id,
        location="global",
        credentials=credentials,
    )


def setup_video_client():
    """Initialize client for video generation (requires location="us-central1")."""
    from google import genai
    from google.oauth2.service_account import Credentials

    project_id = os.environ.get("VERTEX_AI_PROJECT_ID")
    service_account_path = os.environ.get("VERTEX_AI_SERVICE_ACCOUNT_JSON")

    scopes = ["https://www.googleapis.com/auth/cloud-platform"]
    credentials = Credentials.from_service_account_file(service_account_path, scopes=scopes)

    return genai.Client(
        vertexai=True,
        project=project_id,
        location="us-central1",
        credentials=credentials,
    )


@retry_on_429
def generate_image(client, prompt: str, aspect_ratio: str, output_path: Path) -> Path:
    """Generate a single image from prompt."""
    from google.genai import types

    print(f"  Generating image: {output_path.name}")
    print(f"    Prompt: {prompt[:80]}...")

    response = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=prompt,
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio=aspect_ratio),
        ),
    )

    for part in response.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            image_bytes = part.inline_data.data
            with open(output_path, "wb") as f:
                f.write(image_bytes)
            print(f"    Saved: {output_path} ({len(image_bytes)} bytes)")
            return output_path

    print("ERROR: No image in response")
    sys.exit(1)


@retry_on_429
def generate_variation(client, reference_path: Path, prompt: str, aspect_ratio: str, output_path: Path) -> Path:
    """Generate image variation using reference image for consistency."""
    from google.genai import types

    print(f"  Generating variation: {output_path.name}")
    print(f"    Reference: {reference_path.name}")
    print(f"    Prompt: {prompt[:80]}...")

    with open(reference_path, "rb") as f:
        reference_bytes = f.read()

    response = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=[
            types.Part.from_bytes(data=reference_bytes, mime_type="image/png"),
            prompt,
        ],
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio=aspect_ratio),
        ),
    )

    for part in response.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            image_bytes = part.inline_data.data
            with open(output_path, "wb") as f:
                f.write(image_bytes)
            print(f"    Saved: {output_path} ({len(image_bytes)} bytes)")
            return output_path

    print("ERROR: No image in response")
    sys.exit(1)


@retry_on_429
def generate_video(client, first_frame: Path, last_frame: Path, prompt: str,
                   aspect_ratio: str, duration: int, output_path: Path) -> Path:
    """Generate transition video from first to last frame."""
    from google.genai import types
    from google.genai.types import GenerateVideosConfig

    print(f"  Generating video: {output_path.name}")
    print(f"    First frame: {first_frame.name}")
    print(f"    Last frame: {last_frame.name}")
    print(f"    Duration: {duration}s")
    print(f"    Prompt: {prompt[:80]}...")

    first_image = types.Image.from_file(location=str(first_frame))
    last_image = types.Image.from_file(location=str(last_frame))

    operation = client.models.generate_videos(
        model="veo-3.1-generate-001",
        prompt=prompt,
        image=first_image,
        config=GenerateVideosConfig(
            aspect_ratio=aspect_ratio,
            duration_seconds=duration,
            generate_audio=False,
            resolution="720p",
            last_frame=last_image,
        ),
    )

    poll_count = 0
    while not operation.done:
        poll_count += 1
        print(f"    Waiting... (poll #{poll_count})")
        time.sleep(15)
        operation = client.operations.get(operation)

    if operation.response:
        result = operation.result
        if result.generated_videos:
            video = result.generated_videos[0].video

            if hasattr(video, 'video_bytes') and video.video_bytes:
                video_bytes = video.video_bytes
                if isinstance(video_bytes, str):
                    video_bytes = base64.b64decode(video_bytes)
                with open(output_path, 'wb') as f:
                    f.write(video_bytes)
                print(f"    Saved: {output_path} ({len(video_bytes)} bytes)")
                return output_path
            elif hasattr(video, 'uri') and video.uri:
                print(f"    Video at GCS: {video.uri}")
                print("    NOTE: Download from GCS manually")
                return None

    print("ERROR: No video generated")
    return None


def create_clip(config: dict) -> dict:
    """
    Create a single video clip from config.

    Config keys:
        clip_id: str - unique identifier for output folder
        start_prompt: str - description of starting frame
        end_prompt: str - description of ending frame
        transition_prompt: str - description of the motion/transition
        aspect_ratio: str - "9:16", "16:9", or "1:1" (default: "9:16")
        duration: int - 4, 6, or 8 seconds (default: 4)
        output_dir: str - base output directory (default: "output")
    """
    clip_id = config["clip_id"]
    start_prompt = config["start_prompt"]
    end_prompt = config["end_prompt"]
    transition_prompt = config["transition_prompt"]
    aspect_ratio = config.get("aspect_ratio", "9:16")
    duration = config.get("duration", 4)
    output_dir = Path(config.get("output_dir", "output"))

    clip_dir = output_dir / clip_id
    clip_dir.mkdir(parents=True, exist_ok=True)

    print(f"\n{'='*60}")
    print(f"CREATING CLIP: {clip_id}")
    print(f"Aspect ratio: {aspect_ratio}, Duration: {duration}s")
    print(f"Output: {clip_dir}")
    print(f"{'='*60}\n")

    # Initialize clients
    image_client = setup_image_client()
    video_client = setup_video_client()

    # Step 1: Generate start frame
    print("STEP 1: Start frame")
    start_path = clip_dir / "start.png"
    generate_image(image_client, start_prompt, aspect_ratio, start_path)

    # Step 2: Generate end frame (using start as reference for consistency)
    print("\nSTEP 2: End frame (with reference)")
    end_path = clip_dir / "end.png"
    consistency_prompt = f"Generate the exact same scene/person from the reference image, but now: {end_prompt}. Maintain visual consistency."
    generate_variation(image_client, start_path, consistency_prompt, aspect_ratio, end_path)

    # Step 3: Generate transition video
    print("\nSTEP 3: Transition video")
    video_path = clip_dir / "clip.mp4"
    generate_video(video_client, start_path, end_path, transition_prompt, aspect_ratio, duration, video_path)

    print(f"\n{'='*60}")
    print(f"CLIP COMPLETE: {clip_id}")
    print(f"{'='*60}")

    return {
        "clip_id": clip_id,
        "start": str(start_path),
        "end": str(end_path),
        "video": str(video_path) if video_path else None,
    }


def main():
    parser = argparse.ArgumentParser(description="Generate a single marketing video clip")
    parser.add_argument("config", nargs="?", help="JSON config file path")
    parser.add_argument("--clip-id", help="Clip identifier")
    parser.add_argument("--start", dest="start_prompt", help="Start frame prompt")
    parser.add_argument("--end", dest="end_prompt", help="End frame prompt")
    parser.add_argument("--transition", dest="transition_prompt", help="Transition prompt")
    parser.add_argument("--aspect", default="9:16", help="Aspect ratio (9:16, 16:9, 1:1)")
    parser.add_argument("--duration", type=int, default=4, choices=[4, 6, 8], help="Duration in seconds")
    parser.add_argument("--output-dir", default="output", help="Output directory")
    parser.add_argument("--max-retries", type=int, default=8, help="Max retries on rate limit (default: 8)")

    args = parser.parse_args()

    global MAX_RETRIES
    MAX_RETRIES = args.max_retries

    if args.config:
        with open(args.config, "r") as f:
            config = json.load(f)
    elif args.clip_id and args.start_prompt and args.end_prompt and args.transition_prompt:
        config = {
            "clip_id": args.clip_id,
            "start_prompt": args.start_prompt,
            "end_prompt": args.end_prompt,
            "transition_prompt": args.transition_prompt,
            "aspect_ratio": args.aspect,
            "duration": args.duration,
            "output_dir": args.output_dir,
        }
    else:
        parser.print_help()
        print("\nExample config.json:")
        print(json.dumps({
            "clip_id": "test001",
            "start_prompt": "Developer at desk looking frustrated at laptop screen",
            "end_prompt": "Same developer now smiling, holding phone showing terminal",
            "transition_prompt": "Developer picks up phone, expression changes from frustration to relief",
            "aspect_ratio": "9:16",
            "duration": 4
        }, indent=2))
        sys.exit(1)

    result = create_clip(config)
    print(f"\nResult: {json.dumps(result, indent=2)}")


if __name__ == "__main__":
    main()
