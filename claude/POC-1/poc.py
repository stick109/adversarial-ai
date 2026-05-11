"""POC-1: drive the OpenEMR Clinical Co-Pilot from a Python red-team client.

Sequence:
    1. GET  /interface/login/login.php?site=<site>           (seed session)
    2. POST /interface/main/main_screen.php?auth=login...    (authenticate)
    3. GET  /interface/patient_file/summary/agent.php?set_pid=<pid>
           (open chart, harvest APICSRFTOKEN from the rendered panel)
    4. POST /apis/<site>/api/agent/intent
           (invoke the Clinical Co-Pilot with the requested intent)

Every request and response is dumped to ``evidence/<UTC-timestamp>/`` as JSON
plus the raw chart HTML, a transcript log, and a Markdown summary.  Passwords
are redacted before being written to disk.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import pathlib
import sys
import time
from typing import Any

import httpx
from bs4 import BeautifulSoup


REDACTED_FIELDS = frozenset({"clearPass", "authPass", "password"})


def _utc_now_iso() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def _utc_now_stamp() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")


def _redact(body: Any) -> Any:
    if isinstance(body, dict):
        return {
            k: ("***REDACTED***" if k in REDACTED_FIELDS else _redact(v))
            for k, v in body.items()
        }
    if isinstance(body, list):
        return [_redact(v) for v in body]
    return body


def _write_json(path: pathlib.Path, payload: Any) -> None:
    path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False, default=str),
        encoding="utf-8",
    )


def _save_request(
    evidence_dir: pathlib.Path,
    step_name: str,
    method: str,
    url: str,
    headers: dict[str, str] | None,
    body: Any,
) -> None:
    payload = {
        "captured_at": _utc_now_iso(),
        "method": method,
        "url": url,
        "headers": dict(headers or {}),
        "body": _redact(body),
    }
    _write_json(evidence_dir / f"{step_name}.req.json", payload)


def _save_response(
    evidence_dir: pathlib.Path,
    step_name: str,
    response: httpx.Response,
    elapsed_ms: int,
) -> None:
    body_bytes = response.content
    body_repr: Any
    if len(body_bytes) > 1_000_000:
        body_repr = f"<truncated {len(body_bytes)} bytes>"
    else:
        try:
            body_repr = response.text
        except UnicodeDecodeError:
            body_repr = f"<binary {len(body_bytes)} bytes>"

    payload = {
        "captured_at": _utc_now_iso(),
        "url": str(response.url),
        "status_code": response.status_code,
        "reason": response.reason_phrase,
        "elapsed_ms": elapsed_ms,
        "http_version": response.http_version,
        "headers": dict(response.headers),
        "body_length": len(body_bytes),
        "history": [
            {"status": h.status_code, "url": str(h.url)} for h in response.history
        ],
        "body": body_repr,
    }
    _write_json(evidence_dir / f"{step_name}.resp.json", payload)


def _snapshot_cookies(client: httpx.Client) -> list[dict[str, Any]]:
    snapshot: list[dict[str, Any]] = []
    for cookie in client.cookies.jar:
        snapshot.append({
            "name": cookie.name,
            "domain": cookie.domain,
            "path": cookie.path,
            "secure": cookie.secure,
            "expires": cookie.expires,
            "value_length": len(cookie.value or ""),
        })
    return snapshot


def _looks_like_login_page(html: str) -> bool:
    lowered = html.lower()
    return 'id="login_form"' in lowered or 'name="authuser"' in lowered


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--site", default="default")
    parser.add_argument("--username", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--pid", type=int, default=1)
    parser.add_argument("--intent-id", default="basic_patient_data")
    parser.add_argument("--user-goal", default="show basic patient data")
    args = parser.parse_args()

    base = args.base_url.rstrip("/")
    stamp = _utc_now_stamp()
    script_dir = pathlib.Path(__file__).resolve().parent
    evidence_dir = script_dir / "evidence" / stamp
    evidence_dir.mkdir(parents=True, exist_ok=True)

    transcript: list[str] = []

    def log(msg: str = "") -> None:
        line = f"[{_utc_now_iso()}] {msg}" if msg else ""
        print(line)
        transcript.append(line)

    log("=== POC-1 Clinical Co-Pilot exercise ===")
    log(f"Base URL        : {base}")
    log(f"Site            : {args.site}")
    log(f"Username        : {args.username}")
    log(f"Target patient  : pid={args.pid}")
    log(f"Intent          : {args.intent_id}")
    log(f"User goal       : {args.user_goal!r}")
    log(f"Evidence dir    : {evidence_dir}")
    log("")

    exit_code = 0
    csrf_token: str | None = None
    final_answer_summary: dict[str, Any] = {}

    client_headers = {"User-Agent": "AgentForge-RedTeam-POC1/0.1"}

    with httpx.Client(
        follow_redirects=True,
        timeout=60.0,
        headers=client_headers,
    ) as http:

        # ---- Step 1: GET login page ----
        log("Step 1: GET login page (seed session cookies)")
        url = f"{base}/interface/login/login.php?site={args.site}"
        _save_request(evidence_dir, "step_01_login_get", "GET", url, client_headers, None)
        try:
            t0 = time.monotonic()
            resp = http.get(url)
            elapsed_ms = int((time.monotonic() - t0) * 1000)
            _save_response(evidence_dir, "step_01_login_get", resp, elapsed_ms)
            log(f"  -> {resp.status_code} {resp.reason_phrase}"
                f" ({elapsed_ms} ms, {len(resp.content)} bytes,"
                f" final url={resp.url})")
            log(f"  cookies after: {sorted(http.cookies.keys())}")
        except Exception as exc:
            log(f"  FAILED: {type(exc).__name__}: {exc}")
            exit_code = 2

        # ---- Step 2: POST login form ----
        log("")
        log("Step 2: POST login form")
        url = f"{base}/interface/main/main_screen.php?auth=login&site={args.site}"
        form = {
            "new_login_session_management": "1",
            "languageChoice": "1",
            "authUser": args.username,
            "clearPass": args.password,
        }
        post_headers = {
            **client_headers,
            "Content-Type": "application/x-www-form-urlencoded",
        }
        _save_request(evidence_dir, "step_02_login_post", "POST", url, post_headers, form)
        try:
            t0 = time.monotonic()
            resp = http.post(url, data=form)
            elapsed_ms = int((time.monotonic() - t0) * 1000)
            _save_response(evidence_dir, "step_02_login_post", resp, elapsed_ms)
            log(f"  -> {resp.status_code} {resp.reason_phrase}"
                f" ({elapsed_ms} ms, {len(resp.content)} bytes,"
                f" history={len(resp.history)}, final url={resp.url})")
            log(f"  cookies after: {sorted(http.cookies.keys())}")
            if _looks_like_login_page(resp.text):
                log("  WARN: response still looks like a login page;"
                    " credentials likely rejected")
                exit_code = max(exit_code, 1)
        except Exception as exc:
            log(f"  FAILED: {type(exc).__name__}: {exc}")
            exit_code = 2

        # ---- Step 3a: bind the active patient via demographics.php ----
        # Only demographics.php honours ?set_pid (it calls setpid() from
        # src/pid.inc.php which writes pid into the session).  agent.php
        # itself does not process set_pid; it reads $pid from the session.
        log("")
        log(f"Step 3a: GET demographics.php?set_pid={args.pid} (bind active patient in session)")
        url = (
            f"{base}/interface/patient_file/summary/demographics.php"
            f"?set_pid={args.pid}"
        )
        _save_request(evidence_dir, "step_03a_setpid_get", "GET", url, client_headers, None)
        try:
            t0 = time.monotonic()
            resp = http.get(url)
            elapsed_ms = int((time.monotonic() - t0) * 1000)
            _save_response(evidence_dir, "step_03a_setpid_get", resp, elapsed_ms)
            log(f"  -> {resp.status_code} {resp.reason_phrase}"
                f" ({elapsed_ms} ms, {len(resp.content)} bytes,"
                f" final url={resp.url})")
            if _looks_like_login_page(resp.text):
                log("  WARN: demographics returned the login form; session not authenticated")
                exit_code = max(exit_code, 1)
        except Exception as exc:
            log(f"  FAILED: {type(exc).__name__}: {exc}")
            exit_code = 2

        # ---- Step 3b: GET chart page and scrape CSRF token ----
        log("")
        log("Step 3b: GET chart page (agent.php) and scrape APICSRFTOKEN")
        url = f"{base}/interface/patient_file/summary/agent.php"
        _save_request(evidence_dir, "step_03b_chart_get", "GET", url, client_headers, None)
        try:
            t0 = time.monotonic()
            resp = http.get(url)
            elapsed_ms = int((time.monotonic() - t0) * 1000)
            _save_response(evidence_dir, "step_03b_chart_get", resp, elapsed_ms)
            log(f"  -> {resp.status_code} {resp.reason_phrase}"
                f" ({elapsed_ms} ms, {len(resp.content)} bytes,"
                f" history={len(resp.history)}, final url={resp.url})")
            (evidence_dir / "step_03b_chart_page.html").write_text(
                resp.text, encoding="utf-8"
            )

            if _looks_like_login_page(resp.text):
                log("  WARN: chart page returned the login form; session not authenticated")
                exit_code = max(exit_code, 1)

            soup = BeautifulSoup(resp.text, "html.parser")
            panel = soup.find(attrs={"data-api-csrf-token": True})
            if panel is not None:
                csrf_token = panel.get("data-api-csrf-token")
                api_url_attr = panel.get("data-api-url", "")
                log(f"  data-api-url    : {api_url_attr!r}")
                if csrf_token:
                    log(f"  APICSRFTOKEN    : present ({len(csrf_token)} chars)")
                else:
                    log("  WARN: data-api-csrf-token attribute was empty")
            else:
                log("  WARN: no element with data-api-csrf-token attribute found")
        except Exception as exc:
            log(f"  FAILED: {type(exc).__name__}: {exc}")
            exit_code = 2

        # ---- Step 4: POST /api/agent/intent ----
        log("")
        log("Step 4: POST /apis/<site>/api/agent/intent")
        url = f"{base}/apis/{args.site}/api/agent/intent"
        payload: dict[str, Any] = {
            "intent_id": args.intent_id,
            "conversation_id": f"poc1-{stamp}",
            "active_patient_context": "server-session",
        }
        if args.intent_id == "free_text":
            payload["user_goal"] = args.user_goal
        intent_headers = {
            **client_headers,
            "Content-Type": "application/json",
            "Accept": "application/json",
        }
        if csrf_token:
            intent_headers["APICSRFTOKEN"] = csrf_token
        else:
            log("  proceeding WITHOUT APICSRFTOKEN; expecting auth rejection")

        _save_request(evidence_dir, "step_04_intent_post", "POST", url, intent_headers, payload)
        try:
            t0 = time.monotonic()
            resp = http.post(url, json=payload, headers=intent_headers)
            elapsed_ms = int((time.monotonic() - t0) * 1000)
            _save_response(evidence_dir, "step_04_intent_post", resp, elapsed_ms)
            log(f"  -> {resp.status_code} {resp.reason_phrase}"
                f" ({elapsed_ms} ms, {len(resp.content)} bytes)")
            try:
                parsed = resp.json()
                _write_json(evidence_dir / "step_04_intent_post.parsed.json", parsed)
                data = parsed.get("data", parsed) if isinstance(parsed, dict) else {}
                answer = data.get("answer") if isinstance(data, dict) else None
                if isinstance(answer, dict):
                    blocks = answer.get("answer_blocks") or []
                    log(f"  answer_blocks   : {len(blocks)}")
                    first_text = ""
                    if blocks:
                        first_claim = (blocks[0].get("claims") or [{}])[0]
                        first_text = (first_claim.get("text") or "")[:160]
                        log(f"  first claim     : {first_text!r}")
                    final_answer_summary = {
                        "verification_status": answer.get("verification_status"),
                        "certainty": answer.get("certainty"),
                        "answer_block_count": len(blocks),
                        "first_claim_preview": first_text,
                        "trace_id": answer.get("trace_id"),
                    }
                else:
                    log("  body is JSON but no .data.answer present")
            except json.JSONDecodeError:
                log("  body is not JSON")
            if resp.status_code >= 400:
                exit_code = max(exit_code, 1)
        except Exception as exc:
            log(f"  FAILED: {type(exc).__name__}: {exc}")
            exit_code = 2

        # ---- final evidence: cookies + transcript + summary ----
        _write_json(evidence_dir / "cookies.json", _snapshot_cookies(http))

    (evidence_dir / "transcript.log").write_text(
        "\n".join(transcript) + "\n", encoding="utf-8"
    )

    summary_lines = [
        "# POC-1 Run Summary",
        "",
        f"- Captured at: `{stamp}`",
        f"- Base URL: `{base}`",
        f"- Site: `{args.site}`",
        f"- Username: `{args.username}`",
        f"- Target patient: pid=`{args.pid}`",
        f"- Intent: `{args.intent_id}`",
        f"- User goal: `{args.user_goal}`",
        f"- APICSRFTOKEN scraped: `{bool(csrf_token)}`",
        f"- Exit code: `{exit_code}`",
        "",
        "## Answer summary",
        "",
        "```json",
        json.dumps(final_answer_summary, indent=2, default=str),
        "```",
        "",
        "## Evidence files",
        "",
    ]
    for entry in sorted(evidence_dir.iterdir()):
        if entry.is_file():
            summary_lines.append(f"- `{entry.name}` ({entry.stat().st_size} bytes)")
    (evidence_dir / "summary.md").write_text(
        "\n".join(summary_lines) + "\n", encoding="utf-8"
    )

    print()
    print(f"Evidence saved to: {evidence_dir}")
    print(f"Exit code: {exit_code}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
