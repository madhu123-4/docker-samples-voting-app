#!/bin/sh
# tests/tests.sh
#
# What changed from the original and WHY:
#
# ORIGINAL:
#   while ! timeout 1 bash -c "echo > /dev/tcp/vote/80"; do sleep 1; done
#   — Used bash's /dev/tcp feature. The base image for tests may be sh-only
#     (no bash). Also checked only the vote service, not the result service.
#
#   if phantomjs render.js http://result | grep -q '1 vote'; then
#   — phantomjs is abandoned and its npm install fails on Node 12+.
#
# NEW:
#   — Uses `nc` (netcat) for TCP checks — works in sh and all alpine images.
#   — Waits for BOTH vote:80 and result:80 to be reachable.
#   — Casts 2 votes (one for each option) so both sides have data.
#   — Calls `node tests.js` (Playwright) instead of phantomjs.


set -e

echo "Waiting for vote service on port 80..."
until node -e "
const net = require('net');
const s = net.createConnection(80, 'vote');
s.on('connect', () => { s.destroy(); process.exit(0); });
s.on('error', () => { s.destroy(); process.exit(1); });
" 2>/dev/null; do
  sleep 1
done
echo "Vote service ready."

echo "Waiting for result service on port 80..."
until node -e "
const net = require('net');
const s = net.createConnection(80, 'result');
s.on('connect', () => { s.destroy(); process.exit(0); });
s.on('error', () => { s.destroy(); process.exit(1); });
" 2>/dev/null; do
  sleep 1
done
echo "Result service ready."

# Cast test votes
curl -sf -X POST --data "vote=a" http://vote > /dev/null
curl -sf -X POST --data "vote=b" http://vote > /dev/null
echo "Test votes cast."

# Give worker time to process votes into the database
sleep 5

# Run the Playwright test
node /app/tests.js