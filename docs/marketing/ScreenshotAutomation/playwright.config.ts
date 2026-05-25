import { defineConfig } from '@playwright/test';

const MIDTERM_BASE_URL = process.env.MIDTERM_BASE_URL ?? 'http://localhost:2000';

// 70% of Full HD (1920x1080)
const VIEWPORT_WIDTH = 1344;
const VIEWPORT_HEIGHT = 756;

export default defineConfig({
  testDir: './tests',
  testMatch: '**/*.spec.ts',

  // Single worker for video recording
  fullyParallel: false,
  workers: 1,

  // Generous timeouts for terminal interactions
  timeout: 120 * 1000,
  expect: {
    timeout: 10 * 1000,
  },

  use: {
    // Viewport matches video size exactly
    viewport: { width: VIEWPORT_WIDTH, height: VIEWPORT_HEIGHT },

    // Video recording - size matches viewport for 1:1 pixel mapping
    video: {
      mode: 'on',
      size: { width: VIEWPORT_WIDTH, height: VIEWPORT_HEIGHT },
    },

    // Base URL for MidTerm
    baseURL: MIDTERM_BASE_URL,
    ignoreHTTPSErrors: true,

    // Action timeouts
    actionTimeout: 10 * 1000,

    // Browser options
    launchOptions: {
      slowMo: 100, // Slow down for better video visibility
    },
  },

  projects: [
    {
      name: 'chromium',
      use: {
        browserName: 'chromium',
        // Ensure consistent window size
        viewport: { width: VIEWPORT_WIDTH, height: VIEWPORT_HEIGHT },
      },
    },
  ],

  // Output directory (will be moved by test to run-x folder)
  outputDir: './test-results',

  reporter: [['list']],
});
