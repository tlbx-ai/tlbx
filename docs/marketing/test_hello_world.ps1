# Test Hello World scripts for Vertex AI image/video generation
# Run from the Marketing folder

$ErrorActionPreference = "Stop"

Write-Host "=== tlbx Marketing - AI Generation Test ===" -ForegroundColor Cyan
Write-Host ""

# Check environment variables
if (-not $env:VERTEX_AI_PROJECT_ID) {
    Write-Host "ERROR: VERTEX_AI_PROJECT_ID not set" -ForegroundColor Red
    exit 1
}
if (-not $env:VERTEX_AI_SERVICE_ACCOUNT_JSON) {
    Write-Host "ERROR: VERTEX_AI_SERVICE_ACCOUNT_JSON not set" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $env:VERTEX_AI_SERVICE_ACCOUNT_JSON)) {
    Write-Host "ERROR: Service account file not found: $env:VERTEX_AI_SERVICE_ACCOUNT_JSON" -ForegroundColor Red
    exit 1
}

Write-Host "Project ID: $env:VERTEX_AI_PROJECT_ID" -ForegroundColor Green
Write-Host "Service Account: $env:VERTEX_AI_SERVICE_ACCOUNT_JSON" -ForegroundColor Green
Write-Host ""

# Check Python and install dependencies
Write-Host "Checking Python dependencies..." -ForegroundColor Yellow
python -m pip install -q -r requirements.txt
Write-Host ""

# Create output folder
$outputDir = ".\output"
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Test 1: Image Generation
Write-Host "=== Test 1: Image Generation (Imagen 3) ===" -ForegroundColor Cyan
$imagePrompt = "A sleek terminal window with green glowing text on a dark background, futuristic hacker aesthetic, digital art"
$imageOutput = "$outputDir\hello_world_image.png"

Write-Host "Prompt: $imagePrompt"
Write-Host "Output: $imageOutput"
Write-Host ""

python generate_image.py $imagePrompt $imageOutput

if (Test-Path $imageOutput) {
    Write-Host "SUCCESS: Image generated!" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "FAILED: Image not generated" -ForegroundColor Red
}

# Test 2: Video Generation
Write-Host "=== Test 2: Video Generation (Veo 3) ===" -ForegroundColor Cyan
$videoPrompt = "A terminal window with scrolling green code, matrix-style, cinematic lighting"
$videoOutput = "$outputDir\hello_world_video.mp4"

Write-Host "Prompt: $videoPrompt"
Write-Host "Output: $videoOutput"
Write-Host "NOTE: Video generation takes 2-5 minutes..."
Write-Host ""

python generate_video.py $videoPrompt $videoOutput

if (Test-Path $videoOutput) {
    Write-Host "SUCCESS: Video generated!" -ForegroundColor Green
} else {
    Write-Host "Check output above for GCS URI or errors" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Tests Complete ===" -ForegroundColor Cyan
Write-Host "Output files in: $outputDir"
