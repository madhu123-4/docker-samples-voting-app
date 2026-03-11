"""
vote_service/app.py
Advanced Flask voting service with:
  - Cookie-based voter identity (persists across visits)
  - Vote-change support (same cookie = update, not duplicate)
  - Per-option live counters via Redis tallies
  - Vote timestamp stored in payload for analytics
  - Container hostname exposed for observability
  - /health endpoint for Docker/K8s probes
  - /stats endpoint returning live tally JSON
  - CORS-safe JSON + HTML responses depending on Accept header
"""

import os
import json
import socket
import random
import time
from flask import (
    Flask, render_template, request,
    make_response, g, jsonify
)
from redis import Redis, RedisError

# ── Configuration ────────────────────────────────────────────────────────────
OPTION_A  = os.getenv("OPTION_A", "Cats")
OPTION_B  = os.getenv("OPTION_B", "Dogs")
HOSTNAME  = socket.gethostname()
REDIS_HOST = os.getenv("REDIS_HOST", "redis")
REDIS_PORT = int(os.getenv("REDIS_PORT", 6379))

app = Flask(__name__)

# ── Redis helpers ─────────────────────────────────────────────────────────────

def get_redis() -> Redis:
    """Return a request-scoped Redis connection (lazy)."""
    if not hasattr(g, "redis"):
        g.redis = Redis(
            host=REDIS_HOST,
            port=REDIS_PORT,
            db=0,
            socket_timeout=5,
            decode_responses=True,
        )
    return g.redis


def redis_available() -> bool:
    try:
        get_redis().ping()
        return True
    except RedisError:
        return False


def get_tallies() -> dict:
    """Return fast counters for both options (never raises)."""
    try:
        r = get_redis()
        return {
            "a": int(r.get("tally:a") or 0),
            "b": int(r.get("tally:b") or 0),
        }
    except RedisError:
        return {"a": 0, "b": 0}


def get_previous_vote(voter_id: str) -> str | None:
    """Look up whether this voter already voted (stored as hash in Redis)."""
    try:
        return get_redis().hget("voters", voter_id)
    except RedisError:
        return None


# ── Routes ────────────────────────────────────────────────────────────────────

@app.route("/health")
def health():
    status = "ok" if redis_available() else "degraded"
    return jsonify({
        "status": status,
        "service": "vote-service",
        "host": HOSTNAME,
    }), 200


@app.route("/stats")
def stats():
    """JSON endpoint — used by the frontend for live counter animation."""
    tallies = get_tallies()
    total   = tallies["a"] + tallies["b"]
    pct_a   = round(tallies["a"] / total * 100, 1) if total else 0
    pct_b   = round(tallies["b"] / total * 100, 1) if total else 0
    return jsonify({
        "a":     tallies["a"],
        "b":     tallies["b"],
        "total": total,
        "pct_a": pct_a,
        "pct_b": pct_b,
    })


@app.route("/", methods=["GET", "POST"])
def index():
    # ── Voter identity ────────────────────────────────────────────────────
    voter_id = request.cookies.get("voter_id")
    if not voter_id:
        voter_id = hex(random.getrandbits(64))[2:]

    # Look up any prior vote so we can reflect it in the UI
    previous_vote = get_previous_vote(voter_id)
    vote          = previous_vote  # default: show what they already chose
    error         = None

    # ── Handle POST ───────────────────────────────────────────────────────
    if request.method == "POST":
        selected = request.form.get("vote")  # "a" or "b"

        if selected not in ("a", "b"):
            error = "Invalid vote option."
        else:
            try:
                r = get_redis()

                # If voter already voted for the other option → undo old tally
                if previous_vote and previous_vote != selected:
                    r.decr(f"tally:{previous_vote}")

                # Push full vote event to queue (worker persists to DB)
                payload = json.dumps({
                    "voter_id":  voter_id,
                    "vote":      selected,
                    "option":    OPTION_A if selected == "a" else OPTION_B,
                    "timestamp": time.time(),
                    "host":      HOSTNAME,
                })
                r.rpush("votes", payload)

                # Update fast tally only if this is a new vote (not a change)
                if previous_vote != selected:
                    r.incr(f"tally:{selected}")

                # Remember this voter's choice
                r.hset("voters", voter_id, selected)

                vote = selected

            except RedisError as exc:
                error = f"Could not reach Redis: {exc}"

    # ── Render ────────────────────────────────────────────────────────────
    tallies = get_tallies()
    total   = tallies["a"] + tallies["b"]

    resp = make_response(render_template(
        "index.html",
        option_a     = OPTION_A,
        option_b     = OPTION_B,
        hostname     = HOSTNAME,
        vote         = vote,
        tallies      = tallies,
        total        = total,
        error        = error,
    ))
    resp.set_cookie("voter_id", voter_id, max_age=365 * 24 * 3600)
    return resp


if __name__ == "__main__":
    port = int(os.getenv("PORT", 80))
    app.run(host="0.0.0.0", port=port, debug=False, threaded=True)