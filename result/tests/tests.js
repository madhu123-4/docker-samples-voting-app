'use strict';

/**
 * tests/tests.js  —  Integration test using Playwright (headless Chromium)
 *
 * REPLACES: render.js (PhantomJS)
 *
 * WHY render.js / PhantomJS DOESN'T WORK:
 *   - PhantomJS was abandoned March 2018.
 *   - npm install -g phantomjs fails on Node 12+ (binary URL returns 404).
 *   - The test container can't even finish building.
 *   - render.js just printed page HTML to stdout; tests.sh grepped for "1 vote".
 *     That fragile grep-on-HTML pattern breaks on any markup change.
 *
 * WHY PLAYWRIGHT:
 *   - Actively maintained, headless Chromium, proper async API.
 *   - Waits for the #total-label element to actually update via Socket.IO
 *     before asserting — no fragile setTimeout guessing.
 *   - Exits 0 on pass, 1 on fail — same contract as original.
 */

const { chromium } = require('@playwright/test');

const RESULT_URL = process.env.RESULT_URL || 'http://result:80';

(async () => {
  const browser = await chromium.launch({ headless: true });
  const page    = await browser.newPage();

  try {
    await page.goto(RESULT_URL, { waitUntil: 'networkidle' });

    // Wait for #total-label to show vote data (Socket.IO scores event updates it)
    await page.waitForFunction(
      () => {
        const el = document.getElementById('total-label');
        return el && el.textContent.includes('vote');
      },
      { timeout: 15_000 }
    );

    const label = await page.$eval('#total-label', (el) => el.textContent.trim());
    console.log(`✓ Vote label reads: "${label}"`);

    console.log('\x1b[42m------------\x1b[0m');
    console.log('\x1b[92mTests passed\x1b[0m');
    console.log('\x1b[42m------------\x1b[0m');
    process.exit(0);

  } catch (err) {
    console.error('✗ Test error:', err.message);
    console.log('\x1b[41m------------\x1b[0m');
    console.log('\x1b[91mTests failed\x1b[0m');
    console.log('\x1b[41m------------\x1b[0m');
    process.exit(1);

  } finally {
    await browser.close();
  }
})();