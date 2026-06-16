#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const rubricPath = path.join(root, 'docs', 'video-quality-rubric.md');

function parseArgs(argv) {
  const args = {
    model: process.env.GEMINI_VIDEO_AUDIT_MODEL || 'gemini-3.5-flash',
    fps: '2',
    out: '',
    video: '',
    dryRun: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--video') args.video = argv[++i] ?? '';
    else if (arg === '--out') args.out = argv[++i] ?? '';
    else if (arg === '--model') args.model = argv[++i] ?? args.model;
    else if (arg === '--fps') args.fps = argv[++i] ?? args.fps;
    else if (arg === '--dry-run') args.dryRun = true;
    else if (arg === '--help' || arg === '-h') {
      printHelp();
      process.exit(0);
    } else if (!arg.startsWith('-') && !args.video) {
      args.video = arg;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  if (!args.video) {
    throw new Error('Missing --video <path>');
  }

  args.video = path.resolve(args.video);
  args.out = args.out
    ? path.resolve(args.out)
    : path.join(path.dirname(args.video), `${path.basename(args.video, path.extname(args.video))}-ai-audit.json`);

  return args;
}

function printHelp() {
  console.log(`Usage:
  node scripts/audit-video-with-gemini.mjs --video <clip.mp4> [--out report.json] [--model gemini-3.5-flash] [--fps 2]

Environment:
  GEMINI_API_KEY              Required unless --dry-run is used.
  GEMINI_VIDEO_AUDIT_MODEL    Optional model override.

The script uploads the video with the Gemini File API, asks Gemini to judge it against docs/video-quality-rubric.md,
and writes a structured JSON report beside the video by default.`);
}

function detectMimeType(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  if (ext === '.mp4') return 'video/mp4';
  if (ext === '.webm') return 'video/webm';
  if (ext === '.mov') return 'video/quicktime';
  if (ext === '.avi') return 'video/avi';
  if (ext === '.mpeg' || ext === '.mpg') return 'video/mpeg';
  if (ext === '.wmv') return 'video/wmv';
  return 'application/octet-stream';
}

function buildPrompt() {
  const rubric = fs.readFileSync(rubricPath, 'utf8');
  return `${rubric}

Review the attached MidTerm marketing video.

Be skeptical and taste-aware. Do not reward the clip for merely proving that a feature exists.
Judge whether it is publishable for a developer-facing social post.
If the clip is bad, say so directly and explain what to change in the next iteration.
Return valid JSON only, matching the output contract above.`;
}

function buildSchema() {
  return {
    type: 'object',
    properties: {
      approved: { type: 'boolean' },
      overallScore: { type: 'number' },
      oneLineVerdict: { type: 'string' },
      targetAudienceFit: { type: 'string' },
      fatalFlaws: { type: 'array', items: { type: 'string' } },
      criteria: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            name: { type: 'string' },
            score: { type: 'number' },
            evidence: { type: 'string' },
            fix: { type: 'string' },
          },
          required: ['name', 'score', 'evidence', 'fix'],
        },
      },
      timestampFindings: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            timestamp: { type: 'string' },
            severity: { type: 'string', enum: ['info', 'warn', 'fail'] },
            finding: { type: 'string' },
            fix: { type: 'string' },
          },
          required: ['timestamp', 'severity', 'finding', 'fix'],
        },
      },
      nextIterationBrief: { type: 'string' },
      keep: { type: 'array', items: { type: 'string' } },
      change: { type: 'array', items: { type: 'string' } },
      discardReason: { type: 'string' },
    },
    required: [
      'approved',
      'overallScore',
      'oneLineVerdict',
      'targetAudienceFit',
      'fatalFlaws',
      'criteria',
      'timestampFindings',
      'nextIterationBrief',
      'keep',
      'change',
      'discardReason',
    ],
  };
}

async function uploadVideo(apiKey, videoPath, mimeType) {
  const bytes = fs.statSync(videoPath).size;
  const start = await fetch('https://generativelanguage.googleapis.com/upload/v1beta/files', {
    method: 'POST',
    headers: {
      'x-goog-api-key': apiKey,
      'X-Goog-Upload-Protocol': 'resumable',
      'X-Goog-Upload-Command': 'start',
      'X-Goog-Upload-Header-Content-Length': String(bytes),
      'X-Goog-Upload-Header-Content-Type': mimeType,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ file: { display_name: path.basename(videoPath) } }),
  });

  if (!start.ok) {
    throw new Error(`Gemini upload start failed: ${start.status} ${await start.text()}`);
  }

  const uploadUrl = start.headers.get('x-goog-upload-url');
  if (!uploadUrl) {
    throw new Error('Gemini upload start did not return x-goog-upload-url.');
  }

  const upload = await fetch(uploadUrl, {
    method: 'POST',
    headers: {
      'Content-Length': String(bytes),
      'X-Goog-Upload-Offset': '0',
      'X-Goog-Upload-Command': 'upload, finalize',
    },
    body: fs.readFileSync(videoPath),
  });

  if (!upload.ok) {
    throw new Error(`Gemini upload finalize failed: ${upload.status} ${await upload.text()}`);
  }

  const body = await upload.json();
  if (!body.file?.uri) {
    throw new Error(`Gemini upload response missing file URI: ${JSON.stringify(body)}`);
  }
  return body.file;
}

async function waitForFileActive(apiKey, file) {
  if (!file.name) {
    return file;
  }

  for (let attempt = 0; attempt < 30; attempt += 1) {
    const response = await fetch(`https://generativelanguage.googleapis.com/v1beta/${file.name}`, {
      headers: { 'x-goog-api-key': apiKey },
    });
    if (!response.ok) {
      throw new Error(`Gemini file status failed: ${response.status} ${await response.text()}`);
    }

    const current = await response.json();
    if (current.state === 'ACTIVE') {
      return current;
    }
    if (current.state === 'FAILED') {
      throw new Error(`Gemini file processing failed: ${JSON.stringify(current)}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 2000));
  }

  throw new Error(`Gemini file did not become ACTIVE in time: ${file.name}`);
}

async function generateAudit(apiKey, args, file, mimeType) {
  const endpoint = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(args.model)}:generateContent`;
  const basePayload = {
      contents: [
        {
          role: 'user',
          parts: [
            {
              file_data: {
                mime_type: file.mimeType || mimeType,
                file_uri: file.uri,
              },
              video_metadata: {
                fps: Number(args.fps),
              },
            },
            { text: buildPrompt() },
          ],
        },
      ],
    };
  const generationConfigs = [
    {
      responseFormat: {
        text: {
          mimeType: 'application/json',
          schema: buildSchema(),
        },
      },
    },
    {
      responseMimeType: 'application/json',
      responseSchema: buildSchema(),
    },
    {
      responseMimeType: 'application/json',
    },
  ];

  let lastFailure = '';
  for (const generationConfig of generationConfigs) {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'x-goog-api-key': apiKey,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ ...basePayload, generationConfig }),
    });

    const text = await response.text();
    if (!response.ok) {
      lastFailure = `${response.status} ${text}`;
      continue;
    }

    const json = JSON.parse(text);
    const responseText = json.candidates?.[0]?.content?.parts?.map((part) => part.text ?? '').join('') ?? '';
    if (!responseText.trim()) {
      lastFailure = `Gemini response did not contain text: ${text}`;
      continue;
    }
    return JSON.parse(responseText);
  }

  throw new Error(`Gemini audit failed: ${lastFailure}`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!fs.existsSync(args.video)) {
    throw new Error(`Video not found: ${args.video}`);
  }
  if (!fs.existsSync(rubricPath)) {
    throw new Error(`Rubric not found: ${rubricPath}`);
  }

  const mimeType = detectMimeType(args.video);
  const requestSummary = {
    provider: 'gemini',
    model: args.model,
    fps: Number(args.fps),
    video: args.video,
    mimeType,
    rubric: rubricPath,
    output: args.out,
  };

  if (args.dryRun) {
    console.log(JSON.stringify({ dryRun: true, requestSummary, prompt: buildPrompt() }, null, 2));
    return;
  }

  const apiKey = process.env.GEMINI_API_KEY;
  if (!apiKey) {
    throw new Error('GEMINI_API_KEY is not set. Use --dry-run to inspect the request without calling Gemini.');
  }

  const uploadedFile = await uploadVideo(apiKey, args.video, mimeType);
  const file = await waitForFileActive(apiKey, uploadedFile);
  const audit = await generateAudit(apiKey, args, file, mimeType);
  const report = {
    generatedAt: new Date().toISOString(),
    request: requestSummary,
    geminiFile: { uri: file.uri, name: file.name, mimeType: file.mimeType },
    audit,
  };

  fs.mkdirSync(path.dirname(args.out), { recursive: true });
  fs.writeFileSync(args.out, `${JSON.stringify(report, null, 2)}\n`);
  console.log(`AI video audit written to ${args.out}`);
  if (!audit.approved) {
    process.exit(2);
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
