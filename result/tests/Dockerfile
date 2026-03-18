# ════════════════════════════════════════════════════════════════════
#  tests/Dockerfile  —  Integration test runner
#
#  What changed from the original and WHY:
#
#  ORIGINAL:
#    FROM node
#    RUN npm install -g phantomjs
#    — PhantomJS was abandoned by its maintainer in 2018.
#      `npm install -g phantomjs` fails on Node 12+ because the
#      binary download URL returns 404. The test container can't even build.
#    — Used PhantomJS to render the result page and grep for "1 vote".
#
#  NEW:
#    — Uses Playwright (headless Chromium) — the modern standard for
#      browser automation and UI testing.
#    — test script rewritten in Node.js (tests.js) — no PhantomJS dependency.
#    — Shell script (tests.sh) still orchestrates wait → vote → assert
#      but calls `node tests.js` instead of `phantomjs render.js`.
# ════════════════════════════════════════════════════════════════════

FROM mcr.microsoft.com/playwright:v1.44.0-jammy

WORKDIR /app

COPY package.json ./
RUN npm install

COPY . .

# tests.sh waits for services, casts a vote, then runs the JS test
CMD ["/bin/sh", "/app/tests.sh"]