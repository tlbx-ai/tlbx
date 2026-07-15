import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// Helper to get next run number
function getNextRunNumber(outputBase: string): number {
  if (!fs.existsSync(outputBase)) {
    return 1;
  }
  const entries = fs.readdirSync(outputBase);
  let maxRun = 0;
  for (const entry of entries) {
    const match = entry.match(/^run-(\d+)$/);
    if (match) {
      const num = parseInt(match[1], 10);
      if (num > maxRun) maxRun = num;
    }
  }
  return maxRun + 1;
}

// Helper to type text with realistic delays
async function typeSlowly(page: any, text: string, delay = 50) {
  for (const char of text) {
    await page.keyboard.type(char, { delay });
  }
}

// Helper to wait and show action
async function pause(ms: number) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

test('tlbx demo - create, rename, and close sessions', async ({ page }) => {
  const outputBase = path.join(__dirname, '../output');
  const runNumber = getNextRunNumber(outputBase);
  const runDir = path.join(outputBase, `run-${runNumber}`);
  fs.mkdirSync(runDir, { recursive: true });

  console.log(`Recording to: ${runDir}`);

  // Step 1: Navigate to tlbx
  await page.goto('/');
  await expect(page.locator('#app')).toBeVisible();
  await pause(1000);

  // ========================================
  // Session 1: Claude AI
  // ========================================
  console.log('Creating session 1: Claude AI');

  // Click new session button
  await page.click('#btn-new-session');
  await pause(500);

  // Wait for terminal to be ready (session appears in list)
  const session1 = page.locator('.session-item').first();
  await expect(session1).toBeVisible();
  await pause(500);

  // Focus the terminal and type command
  await page.locator('#terminal-container').click();
  await pause(300);
  await typeSlowly(page, 'claude');
  await page.keyboard.press('Enter');
  await pause(2000); // Wait for Claude to load

  // Type the prompt
  await typeSlowly(page, 'plan a todo app', 80);
  await page.keyboard.press('Enter');
  await pause(3000); // Let Claude start responding

  // Rename session 1
  await page.locator('.session-item').first().locator('.session-rename').click();
  await pause(300);
  const renameInput1 = page.locator('.session-rename-input');
  await expect(renameInput1).toBeVisible();
  await renameInput1.clear();
  await typeSlowly(renameInput1, 'Claude AI', 50);
  await page.keyboard.press('Enter');
  await pause(500);

  // ========================================
  // Session 2: Text Editor
  // ========================================
  console.log('Creating session 2: Text Editor');

  await page.click('#btn-new-session');
  await pause(500);

  // New session becomes active, wait for it
  await expect(page.locator('.session-item').nth(1)).toBeVisible();
  await pause(500);

  // Focus terminal and type command
  await page.locator('#terminal-container').click();
  await pause(300);
  await typeSlowly(page, 'edit');
  await page.keyboard.press('Enter');
  await pause(1500); // Wait for editor to load

  // Type sample code in the editor
  await typeSlowly(page, 'function hello() {', 40);
  await page.keyboard.press('Enter');
  await typeSlowly(page, '  console.log("Hello World");', 40);
  await page.keyboard.press('Enter');
  await typeSlowly(page, '}', 40);
  await pause(1000);

  // Rename session 2
  await page.locator('.session-item.active').locator('.session-rename').click();
  await pause(300);
  const renameInput2 = page.locator('.session-rename-input');
  await expect(renameInput2).toBeVisible();
  await renameInput2.clear();
  await typeSlowly(renameInput2, 'Text Editor', 50);
  await page.keyboard.press('Enter');
  await pause(500);

  // ========================================
  // Session 3: Network Monitor
  // ========================================
  console.log('Creating session 3: Network Monitor');

  await page.click('#btn-new-session');
  await pause(500);

  // Wait for third session
  await expect(page.locator('.session-item').nth(2)).toBeVisible();
  await pause(500);

  // Focus terminal and type ping command
  await page.locator('#terminal-container').click();
  await pause(300);
  await typeSlowly(page, 'ping 1.1.1.1 -t');
  await page.keyboard.press('Enter');
  await pause(2000); // Let ping start showing output

  // Rename session 3
  await page.locator('.session-item.active').locator('.session-rename').click();
  await pause(300);
  const renameInput3 = page.locator('.session-rename-input');
  await expect(renameInput3).toBeVisible();
  await renameInput3.clear();
  await typeSlowly(renameInput3, 'Network Monitor', 50);
  await page.keyboard.press('Enter');
  await pause(1000);

  // ========================================
  // Showcase: Tab Switching
  // ========================================
  console.log('Showcasing tab switching');

  // Switch to session 1 (Claude AI)
  await page.locator('.session-item').first().click();
  await pause(1500);

  // Switch to session 2 (Text Editor)
  await page.locator('.session-item').nth(1).click();
  await pause(1500);

  // Switch to session 3 (Network Monitor)
  await page.locator('.session-item').nth(2).click();
  await pause(1500);

  // ========================================
  // Close All Sessions
  // ========================================
  console.log('Closing all sessions');

  // Close session 3 (currently active)
  await page.locator('.session-item.active').locator('.session-close').click();
  await pause(800);

  // Close session 2 (now active)
  await page.locator('.session-item.active').locator('.session-close').click();
  await pause(800);

  // Close session 1 (last one) - need to send Ctrl+C first to stop claude
  await page.keyboard.press('Control+c');
  await pause(500);
  await page.locator('.session-item.active').locator('.session-close').click();
  await pause(800);

  // Final state - empty
  await expect(page.locator('#empty-state')).toBeVisible();
  await pause(1000);

  console.log('Demo recording complete!');

  // Video will be saved automatically when test ends
  // Move video to run directory after test
  test.info().annotations.push({ type: 'runDir', description: runDir });
});

// After test hook to move video
test.afterEach(async ({ page }, testInfo) => {
  const runDirAnnotation = testInfo.annotations.find(a => a.type === 'runDir');
  if (!runDirAnnotation) return;

  const runDir = runDirAnnotation.description;
  const video = page.video();

  if (video) {
    const videoPath = await video.path();
    if (videoPath && fs.existsSync(videoPath)) {
      const destPath = path.join(runDir!, 'video.mp4');
      // Wait a bit for video to finish encoding
      await new Promise(resolve => setTimeout(resolve, 2000));
      fs.copyFileSync(videoPath, destPath);
      console.log(`Video saved to: ${destPath}`);
    }
  }
});
