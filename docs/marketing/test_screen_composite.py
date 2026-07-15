#!/usr/bin/env python3
"""
Test: Can Gemini incorporate a screenshot as monitor content?

Approach: Pass a tlbx screenshot + prompt asking to generate a person
looking at a monitor displaying that exact screenshot.
"""

import os
import sys
from pathlib import Path


def setup_client():
    """Initialize the Google GenAI client."""
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


def test_screen_composite(client, screenshot_path: Path, output_dir: Path):
    """Test if Gemini can place the screenshot on a monitor in the scene."""
    from google.genai import types

    print("=" * 60)
    print("TEST: Screen Content Incorporation")
    print("=" * 60)

    with open(screenshot_path, "rb") as f:
        screenshot_bytes = f.read()

    print(f"Screenshot: {screenshot_path.name} ({len(screenshot_bytes)} bytes)")

    # Test 1: Ask to show the screenshot on a monitor
    prompt1 = """Generate an image of a young developer with short dark hair sitting at a desk
in a modern home office. They are looking at a large monitor that displays EXACTLY the
terminal application shown in the reference image. The monitor should show the reference
image content clearly and legibly. Warm ambient lighting, photorealistic, high quality."""

    print(f"\nTest 1: Direct incorporation")
    print(f"Prompt: {prompt1[:100]}...")

    response1 = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=[
            types.Part.from_bytes(data=screenshot_bytes, mime_type="image/png"),
            prompt1,
        ],
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio="16:9"),
        ),
    )

    output1 = output_dir / "test1_direct_composite.png"
    for part in response1.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            with open(output1, "wb") as f:
                f.write(part.inline_data.data)
            print(f"Saved: {output1.name}")
            break

    # Test 2: Different framing - over the shoulder view
    prompt2 = """Over-the-shoulder shot of a person looking at their computer monitor.
The monitor screen shows the exact terminal application from the reference image -
a dark themed terminal with a sidebar on the left showing multiple sessions.
The reference image content must be visible on the monitor screen.
Modern office setting, shallow depth of field focusing on the screen, photorealistic."""

    print(f"\nTest 2: Over-shoulder framing")
    print(f"Prompt: {prompt2[:100]}...")

    response2 = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=[
            types.Part.from_bytes(data=screenshot_bytes, mime_type="image/png"),
            prompt2,
        ],
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio="16:9"),
        ),
    )

    output2 = output_dir / "test2_over_shoulder.png"
    for part in response2.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            with open(output2, "wb") as f:
                f.write(part.inline_data.data)
            print(f"Saved: {output2.name}")
            break

    # Test 3: Just the screen (for potential compositing)
    prompt3 = """Generate an image of just a computer monitor screen displaying the exact
terminal application from the reference image. The monitor should fill most of the frame,
slight angle, showing the bezel. The screen content must match the reference image exactly -
a dark terminal with colorful text and a sidebar. High quality, sharp details."""

    print(f"\nTest 3: Monitor only (for composite)")
    print(f"Prompt: {prompt3[:100]}...")

    response3 = client.models.generate_content(
        model="gemini-3-pro-image-preview",
        contents=[
            types.Part.from_bytes(data=screenshot_bytes, mime_type="image/png"),
            prompt3,
        ],
        config=types.GenerateContentConfig(
            response_modalities=["IMAGE"],
            image_config=types.ImageConfig(aspect_ratio="16:9"),
        ),
    )

    output3 = output_dir / "test3_monitor_only.png"
    for part in response3.candidates[0].content.parts:
        if part.inline_data and part.inline_data.mime_type.startswith("image/"):
            with open(output3, "wb") as f:
                f.write(part.inline_data.data)
            print(f"Saved: {output3.name}")
            break

    print("\n" + "=" * 60)
    print("DONE - Check outputs to see how well Gemini preserved the screenshot")
    print("=" * 60)


def main():
    client = setup_client()

    screenshot = Path("Screenshots/sc1.png")
    if not screenshot.exists():
        print(f"ERROR: Screenshot not found: {screenshot}")
        sys.exit(1)

    output_dir = Path("output/screen_composite_test")
    output_dir.mkdir(parents=True, exist_ok=True)

    test_screen_composite(client, screenshot, output_dir)


if __name__ == "__main__":
    main()
