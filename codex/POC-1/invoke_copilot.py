#!/usr/bin/env python3
"""Dependency-free production Clinical Co-Pilot invocation POC."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import html
import json
import os
import platform
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from dataclasses import dataclass
from html.parser import HTMLParser
from http.cookiejar import CookieJar
from pathlib import Path
from typing import Any


DEFAULT_BASE_URL = "https://openemr-web-production.up.railway.app/"
DEFAULT_PROMPT = "show basic patient data"
ZIP_COMPRESSION_LEVEL = 9


class FormCollector(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.forms: list[dict[str, Any]] = []
        self._current: dict[str, Any] | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attr_map = {key.lower(): value or "" for key, value in attrs}
        if tag.lower() == "form":
            self._current = {"attrs": attr_map, "inputs": []}
            return
        if self._current is not None and tag.lower() == "input":
            self._current["inputs"].append(attr_map)

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "form" and self._current is not None:
            self.forms.append(self._current)
            self._current = None


class AgentPanelParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.panel_attrs: dict[str, str] = {}
        self.intent_ids: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attr_map = {key.lower(): value or "" for key, value in attrs}
        if attr_map.get("data-agent-panel") == "patient-chart":
            self.panel_attrs = attr_map
        if tag.lower() == "button" and attr_map.get("data-intent-id"):
            self.intent_ids.append(attr_map["data-intent-id"])


@dataclass
class HttpResult:
    method: str
    url: str
    final_url: str
    status: int | None
    ok: bool
    headers: dict[str, str]
    body: bytes
    elapsed_ms: int
    error: str | None = None


def utc_stamp() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def iso_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def decode_body(body: bytes, content_type: str = "") -> str:
    charset_match = re.search(r"charset=([^;\s]+)", content_type, re.I)
    charset = charset_match.group(1) if charset_match else "utf-8"
    try:
        return body.decode(charset, errors="replace")
    except LookupError:
        return body.decode("utf-8", errors="replace")


def redact_text(text: str) -> str:
    replacements = [
        (r'(data-api-csrf-token=")[^"]*(")', r"\1[REDACTED]\2"),
        (r'(name="clearPass"\s+value=")[^"]*(")', r"\1[REDACTED]\2"),
        (r'(APICSRFTOKEN["\']?\s*[:=]\s*["\'])[^"\']*(["\'])', r"\1[REDACTED]\2"),
    ]
    redacted = text
    for pattern, replacement in replacements:
        redacted = re.sub(pattern, replacement, redacted, flags=re.I)
    return redacted


def sanitize_headers(headers: dict[str, str]) -> dict[str, str]:
    sanitized: dict[str, str] = {}
    for key, value in headers.items():
        lower = key.lower()
        if lower in {"set-cookie", "cookie", "authorization", "apicsrftoken"}:
            sanitized[key] = "[REDACTED]"
        else:
            sanitized[key] = value
    return sanitized


def cookie_inventory(cookie_jar: CookieJar) -> list[dict[str, Any]]:
    return [
        {
            "name": cookie.name,
            "domain": cookie.domain,
            "path": cookie.path,
            "secure": cookie.secure,
            "expires": cookie.expires,
        }
        for cookie in cookie_jar
    ]


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def pretty_json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def decode_json_body(body: bytes) -> Any | None:
    try:
        return json.loads(body.decode("utf-8"))
    except Exception:
        return None


def decode_json_text(text: str) -> Any | None:
    try:
        return json.loads(text)
    except Exception:
        return None


def next_available_archive_path(evidence_root: Path) -> Path:
    base = Path(str(evidence_root) + ".zip")
    if not base.exists():
        return base

    for index in range(2, 1000):
        candidate = evidence_root.with_name(f"{evidence_root.name}-{index}.zip")
        if not candidate.exists():
            return candidate
    raise RuntimeError(f"Could not find an available archive path for {evidence_root}")


def create_evidence_archive(evidence_root: Path, archive_path: Path) -> dict[str, Any]:
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        archive_path,
        mode="x",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=ZIP_COMPRESSION_LEVEL,
    ) as archive:
        for path in sorted(evidence_root.rglob("*")):
            if not path.is_file():
                continue
            archive_name = Path(evidence_root.name) / path.relative_to(evidence_root)
            archive.write(path, archive_name.as_posix())

    archive_hash = sha256_file(archive_path)
    checksum_path = Path(str(archive_path) + ".sha256")
    checksum_path.write_text(f"{archive_hash}  {archive_path.name}\n", encoding="utf-8")
    return {
        "path": str(archive_path),
        "sha256_path": str(checksum_path),
        "bytes": archive_path.stat().st_size,
        "sha256": archive_hash,
        "compression_method": "ZIP_DEFLATED",
        "compression_level": ZIP_COMPRESSION_LEVEL,
    }


class EvidenceWriter:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.root.mkdir(parents=True, exist_ok=True)
        self.artifacts: list[dict[str, Any]] = []

    def write_bytes(self, relative_name: str, data: bytes) -> Path:
        path = self.root / relative_name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        self.artifacts.append(
            {
                "file": relative_name,
                "bytes": len(data),
                "sha256": sha256_file(path),
            }
        )
        return path

    def write_text(self, relative_name: str, text: str) -> Path:
        return self.write_bytes(relative_name, text.encode("utf-8"))

    def write_json(self, relative_name: str, value: Any) -> Path:
        return self.write_text(relative_name, json.dumps(value, indent=2, sort_keys=True) + "\n")


class PocRun:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.base_url = normalize_base_url(args.base_url)
        self.cookie_jar = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookie_jar))
        self.evidence = EvidenceWriter(resolve_evidence_dir(args.evidence_dir))
        self.events: list[dict[str, Any]] = []
        self.summary: dict[str, Any] = {
            "started_at": iso_now(),
            "base_url": self.base_url,
            "prompt": args.prompt,
            "patient_id_supplied": bool(args.patient_id),
            "username_supplied": bool(args.username),
            "password_supplied": bool(args.password),
            "python": sys.version,
            "platform": platform.platform(),
            "result": "started",
            "artifacts": self.evidence.artifacts,
            "events": self.events,
        }
        self.archive_info: dict[str, Any] | None = None

    def add_event(self, name: str, **fields: Any) -> None:
        event = {"at": iso_now(), "name": name}
        event.update(fields)
        self.events.append(event)

    def request(
        self,
        method: str,
        url: str,
        *,
        data: bytes | None = None,
        headers: dict[str, str] | None = None,
        timeout: int = 60,
    ) -> HttpResult:
        request_headers = {
            "User-Agent": "adversarial-ai-clinical-copilot-poc/1.0",
            "Accept": "*/*",
        }
        if headers:
            request_headers.update(headers)

        req = urllib.request.Request(url, data=data, headers=request_headers, method=method)
        start = time.perf_counter()
        try:
            response = self.opener.open(req, timeout=timeout)
            body = response.read()
            elapsed = int((time.perf_counter() - start) * 1000)
            status = response.getcode()
            response_headers = dict(response.info())
            return HttpResult(
                method=method,
                url=url,
                final_url=response.geturl(),
                status=status,
                ok=200 <= status < 400,
                headers=response_headers,
                body=body,
                elapsed_ms=elapsed,
            )
        except urllib.error.HTTPError as exc:
            body = exc.read()
            elapsed = int((time.perf_counter() - start) * 1000)
            return HttpResult(
                method=method,
                url=url,
                final_url=exc.geturl(),
                status=exc.code,
                ok=False,
                headers=dict(exc.headers),
                body=body,
                elapsed_ms=elapsed,
                error=str(exc),
            )
        except Exception as exc:
            elapsed = int((time.perf_counter() - start) * 1000)
            return HttpResult(
                method=method,
                url=url,
                final_url=url,
                status=None,
                ok=False,
                headers={},
                body=b"",
                elapsed_ms=elapsed,
                error=repr(exc),
            )

    def save_response(self, prefix: str, result: HttpResult, extension: str = "html", redact: bool = False) -> None:
        content_type = result.headers.get("Content-Type", "")
        body_name = f"{prefix}.{extension}"
        should_pretty_print_json = extension.lower() == "json" or "json" in content_type.lower()
        pretty_printed_json = False

        if redact:
            body_text = decode_body(result.body, content_type)
            redacted_text = redact_text(body_text)
            parsed = decode_json_text(redacted_text) if should_pretty_print_json else None
            if parsed is not None:
                body_to_save = pretty_json_bytes(parsed)
                pretty_printed_json = True
            else:
                body_to_save = redacted_text.encode("utf-8")
        else:
            parsed = decode_json_body(result.body) if should_pretty_print_json else None
            if parsed is not None:
                body_to_save = pretty_json_bytes(parsed)
                pretty_printed_json = True
            else:
                body_to_save = result.body

        self.evidence.write_bytes(body_name, body_to_save)

        self.evidence.write_json(
            f"{prefix}.metadata.json",
            {
                "method": result.method,
                "url": result.url,
                "final_url": result.final_url,
                "status": result.status,
                "ok": result.ok,
                "elapsed_ms": result.elapsed_ms,
                "headers": sanitize_headers(result.headers),
                "body_file": body_name,
                "body_sha256": sha256_bytes(body_to_save),
                "raw_body_sha256": sha256_bytes(result.body),
                "body_was_redacted": redact,
                "json_pretty_printed": pretty_printed_json,
                "error": result.error,
                "cookies": cookie_inventory(self.cookie_jar),
            },
        )

    def write_summaries(self) -> None:
        self.summary["finished_at"] = iso_now()
        self.summary["artifacts"] = self.evidence.artifacts
        self.evidence.write_json("evidence-summary.json", self.summary)

        lines = [
            "# POC-1 Evidence Summary",
            "",
            f"- Started: {self.summary.get('started_at')}",
            f"- Finished: {self.summary.get('finished_at')}",
            f"- Base URL: {self.summary.get('base_url')}",
            f"- Prompt: {self.summary.get('prompt')}",
            f"- Result: {self.summary.get('result')}",
        ]
        if self.summary.get("failure_reason"):
            lines.append(f"- Failure reason: {self.summary.get('failure_reason')}")
        if self.summary.get("copilot_api_status") is not None:
            lines.append(f"- Co-Pilot API HTTP status: {self.summary.get('copilot_api_status')}")
        if self.summary.get("conversation_id"):
            lines.append(f"- Conversation ID: {self.summary.get('conversation_id')}")
        if self.summary.get("answer_blocks") is not None:
            lines.append(f"- Answer blocks: {self.summary.get('answer_blocks')}")
        if self.summary.get("claim_count") is not None:
            lines.append(f"- Claim count: {self.summary.get('claim_count')}")
        archive = self.summary.get("evidence_archive")
        if isinstance(archive, dict) and archive.get("path"):
            lines.append(f"- Evidence archive: {archive.get('path')}")
        lines.extend(["", "## Artifacts", ""])
        for artifact in self.evidence.artifacts:
            lines.append(f"- `{artifact['file']}` ({artifact['bytes']} bytes, sha256 `{artifact['sha256']}`)")
        self.evidence.write_text("evidence-summary.md", "\n".join(lines) + "\n")

    def finalize_evidence(self) -> dict[str, Any] | None:
        archive_path = next_available_archive_path(self.evidence.root)
        self.summary["evidence_archive"] = {
            "path": str(archive_path),
            "compression_method": "ZIP_DEFLATED",
            "compression_level": ZIP_COMPRESSION_LEVEL,
        }
        self.write_summaries()

        try:
            self.archive_info = create_evidence_archive(self.evidence.root, archive_path)
            return self.archive_info
        except Exception as exc:
            self.summary["evidence_archive_error"] = repr(exc)
            self.write_summaries()
            print(f"Evidence archive failed: {exc}", file=sys.stderr)
            return None

    def fail(self, reason: str, exit_code: int) -> int:
        self.summary["result"] = "failed"
        self.summary["failure_reason"] = reason
        archive_info = self.finalize_evidence()
        print(f"POC failed: {reason}", file=sys.stderr)
        print(f"Evidence directory: {self.evidence.root}")
        if archive_info:
            print(f"Evidence archive: {archive_info['path']}")
        return exit_code

    def run(self) -> int:
        login_page_url = urllib.parse.urljoin(self.base_url, "/")
        self.add_event("fetch_login_page", url=login_page_url)
        login_page = self.request(
            "GET",
            login_page_url,
            headers={"Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"},
        )
        self.save_response("01-login-page", login_page, "html", redact=True)
        self.summary["login_page_status"] = login_page.status
        self.summary["login_page_final_url"] = login_page.final_url

        if not login_page.ok:
            return self.fail("login page was not reachable", 10)

        login_html = decode_body(login_page.body, login_page.headers.get("Content-Type", ""))
        login_form = find_login_form(login_html)
        if not login_form:
            return self.fail("login form was not found on production login page", 11)
        self.summary["login_form_action"] = login_form["attrs"].get("action", "")
        self.summary["login_form_input_names"] = sorted(
            name
            for name in (
                field.get("name", "")
                for field in login_form.get("inputs", [])
            )
            if name
        )
        self.evidence.write_json(
            "02-login-form-parsed.json",
            {
                "action": login_form["attrs"].get("action", ""),
                "method": login_form["attrs"].get("method", "GET"),
                "input_names": self.summary["login_form_input_names"],
                "has_authUser": "authUser" in self.summary["login_form_input_names"],
                "has_clearPass": "clearPass" in self.summary["login_form_input_names"],
            },
        )

        if not self.args.username or not self.args.password:
            return self.fail(
                "missing production credentials; set OPENEMR_PROD_USERNAME and OPENEMR_PROD_PASSWORD or pass -Username/-Password",
                2,
            )

        post_fields = login_payload(login_form, self.args.username, self.args.password)
        login_action = urllib.parse.urljoin(login_page.final_url, login_form["attrs"].get("action", ""))
        encoded_login = urllib.parse.urlencode(post_fields).encode("utf-8")
        self.evidence.write_json(
            "03-login-request.redacted.json",
            {
                "method": "POST",
                "url": login_action,
                "field_names": sorted(post_fields.keys()),
                "authUser_supplied": bool(self.args.username),
                "clearPass_supplied": bool(self.args.password),
                "password_value": "[REDACTED]",
            },
        )
        self.add_event("post_login", url=login_action)
        login_result = self.request(
            "POST",
            login_action,
            data=encoded_login,
            headers={
                "Content-Type": "application/x-www-form-urlencoded",
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Referer": login_page.final_url,
            },
        )
        self.save_response("04-login-response", login_result, "html", redact=True)
        self.summary["login_response_status"] = login_result.status
        self.summary["login_response_final_url"] = login_result.final_url

        if not login_result.ok:
            return self.fail("login POST returned a non-success HTTP status", 20)

        login_response_text = decode_body(login_result.body, login_result.headers.get("Content-Type", ""))
        if looks_like_login_page(login_result.final_url, login_response_text):
            return self.fail("login response still looks like the login page; credentials may be invalid", 21)

        self.summary["login_success"] = True

        if self.args.patient_id:
            set_patient_url = urllib.parse.urljoin(
                self.base_url,
                f"/interface/patient_file/summary/demographics.php?set_pid={urllib.parse.quote(str(self.args.patient_id))}",
            )
            self.add_event("set_patient_context", url=set_patient_url)
            set_patient = self.request(
                "GET",
                set_patient_url,
                headers={
                    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    "Referer": login_result.final_url,
                },
            )
            self.save_response("05-set-patient", set_patient, "html", redact=True)
            self.summary["set_patient_status"] = set_patient.status
            self.summary["set_patient_final_url"] = set_patient.final_url
            if not set_patient.ok:
                return self.fail("patient context request failed", 30)

        agent_url = urllib.parse.urljoin(self.base_url, "/interface/patient_file/summary/agent.php")
        self.add_event("fetch_agent_panel", url=agent_url)
        agent_page = self.request(
            "GET",
            agent_url,
            headers={
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Referer": login_result.final_url,
            },
        )
        self.save_response("06-agent-panel", agent_page, "html", redact=True)
        self.summary["agent_page_status"] = agent_page.status
        self.summary["agent_page_final_url"] = agent_page.final_url

        if not agent_page.ok:
            return self.fail("Clinical Co-Pilot panel was not reachable after login", 40)

        panel = parse_agent_panel(decode_body(agent_page.body, agent_page.headers.get("Content-Type", "")))
        api_url = panel.get("data-api-url", "")
        csrf_token = panel.get("data-api-csrf-token", "")
        if not api_url or not csrf_token:
            self.evidence.write_json("07-agent-panel-parsed.json", {"panel_attrs": redact_panel(panel)})
            return self.fail("agent panel did not expose API URL and API CSRF token", 41)
        api_url = urllib.parse.urljoin(agent_page.final_url, html.unescape(api_url))
        csrf_token = html.unescape(csrf_token)
        self.summary["agent_api_url"] = api_url
        self.summary["agent_intent_ids"] = panel.get("intent_ids", [])
        self.evidence.write_json(
            "07-agent-panel-parsed.json",
            {
                "api_url": api_url,
                "api_csrf_token_present": bool(csrf_token),
                "intent_ids": panel.get("intent_ids", []),
            },
        )

        conversation_id = "poc-1-" + utc_stamp()
        payload = {
            "intent_id": "free_text",
            "conversation_id": conversation_id,
            "active_patient_context": "server-session",
            "user_goal": self.args.prompt,
        }
        payload_bytes = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.evidence.write_json(
            "08-copilot-request.redacted.json",
            {
                "method": "POST",
                "url": api_url,
                "headers": {
                    "APICSRFTOKEN": "[REDACTED]",
                    "Accept": "application/json",
                    "Content-Type": "application/json",
                },
                "body": payload,
            },
        )

        self.add_event("post_copilot_prompt", url=api_url, conversation_id=conversation_id)
        copilot_result = self.request(
            "POST",
            api_url,
            data=payload_bytes,
            headers={
                "APICSRFTOKEN": csrf_token,
                "Accept": "application/json",
                "Content-Type": "application/json",
                "Referer": agent_page.final_url,
            },
            timeout=120,
        )
        extension = "json" if "json" in copilot_result.headers.get("Content-Type", "").lower() else "txt"
        self.save_response("09-copilot-response", copilot_result, extension, redact=False)
        self.summary["conversation_id"] = conversation_id
        self.summary["copilot_api_status"] = copilot_result.status
        self.summary["copilot_api_elapsed_ms"] = copilot_result.elapsed_ms

        parsed_response = parse_json_body(copilot_result.body)
        if parsed_response is not None:
            metrics = response_metrics(parsed_response)
            self.summary.update(metrics)
            self.evidence.write_json("10-copilot-response-summary.redacted.json", metrics)

        if not copilot_result.ok:
            return self.fail("Co-Pilot API returned a non-success HTTP status", 50)
        if parsed_response is None:
            return self.fail("Co-Pilot API did not return parseable JSON", 51)
        if has_application_errors(parsed_response):
            return self.fail("Co-Pilot API returned validation or internal errors", 52)

        self.summary["result"] = "succeeded"
        archive_info = self.finalize_evidence()
        if not archive_info:
            self.summary["result"] = "failed"
            self.summary["failure_reason"] = "evidence archive could not be created"
            self.write_summaries()
            print("POC failed: evidence archive could not be created", file=sys.stderr)
            print(f"Evidence directory: {self.evidence.root}")
            return 60
        print("POC completed successfully.")
        print(f"Evidence directory: {self.evidence.root}")
        print(f"Evidence archive: {archive_info['path']}")
        return 0


def normalize_base_url(value: str) -> str:
    if not value:
        value = DEFAULT_BASE_URL
    parsed = urllib.parse.urlparse(value)
    if not parsed.scheme:
        value = "https://" + value
    return value.rstrip("/") + "/"


def resolve_evidence_dir(value: str | None) -> Path:
    if value:
        return Path(value).expanduser().resolve()
    return (Path(__file__).resolve().parent / "evidence" / utc_stamp()).resolve()


def find_login_form(page_text: str) -> dict[str, Any] | None:
    parser = FormCollector()
    parser.feed(page_text)
    for form in parser.forms:
        names = {field.get("name", "") for field in form.get("inputs", [])}
        if "authUser" in names and "clearPass" in names:
            return form
    return parser.forms[0] if parser.forms else None


def login_payload(form: dict[str, Any], username: str, password: str) -> dict[str, str]:
    fields: dict[str, str] = {}
    for field in form.get("inputs", []):
        name = field.get("name", "")
        input_type = field.get("type", "").lower()
        if not name:
            continue
        if input_type in {"submit", "button", "image", "file"}:
            continue
        fields[name] = field.get("value", "")
    fields["authUser"] = username
    fields["clearPass"] = password
    fields.setdefault("new_login_session_management", "1")
    return fields


def looks_like_login_page(url: str, body: str) -> bool:
    lowered_url = url.lower()
    lowered_body = body.lower()
    if "login.php" in lowered_url:
        return True
    return "name=\"authuser\"" in lowered_body and "name=\"clearpass\"" in lowered_body


def parse_agent_panel(page_text: str) -> dict[str, Any]:
    parser = AgentPanelParser()
    parser.feed(page_text)
    panel = dict(parser.panel_attrs)
    panel["intent_ids"] = parser.intent_ids
    return panel


def redact_panel(panel: dict[str, Any]) -> dict[str, Any]:
    redacted = dict(panel)
    if "data-api-csrf-token" in redacted:
        redacted["data-api-csrf-token"] = "[REDACTED]"
    return redacted


def parse_json_body(body: bytes) -> Any | None:
    return decode_json_body(body)


def response_metrics(response: Any) -> dict[str, Any]:
    metrics: dict[str, Any] = {
        "response_top_level_keys": sorted(response.keys()) if isinstance(response, dict) else [],
        "validation_errors_present": False,
        "internal_errors_present": False,
        "answer_blocks": 0,
        "claim_count": 0,
        "citation_count": 0,
        "checked_evidence_count": 0,
    }
    if not isinstance(response, dict):
        return metrics

    validation_errors = response.get("validationErrors")
    internal_errors = response.get("internalErrors")
    metrics["validation_errors_present"] = bool(validation_errors)
    metrics["internal_errors_present"] = bool(internal_errors)

    data = response.get("data")
    if not isinstance(data, dict):
        return metrics

    metrics["intent_id"] = data.get("intent_id")
    metrics["button_label"] = data.get("button_label")
    answer = data.get("answer")
    if isinstance(answer, dict):
        blocks = answer.get("answer_blocks")
        if isinstance(blocks, list):
            metrics["answer_blocks"] = len(blocks)
            metrics["claim_count"] = sum(
                len(block.get("claims", []))
                for block in blocks
                if isinstance(block, dict) and isinstance(block.get("claims"), list)
            )
    citations = data.get("citations")
    if isinstance(citations, list):
        metrics["citation_count"] = len(citations)
    checked_evidence = data.get("checked_evidence")
    if isinstance(checked_evidence, list):
        metrics["checked_evidence_count"] = len(checked_evidence)

    trace = data.get("trace")
    if isinstance(trace, dict):
        for key in ("sidecar_trace_id", "request_id", "latency_ms", "cost_usd"):
            if key in trace:
                metrics[f"trace_{key}"] = trace[key]
    return metrics


def has_application_errors(response: Any) -> bool:
    if not isinstance(response, dict):
        return True
    return bool(response.get("validationErrors")) or bool(response.get("internalErrors"))


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Invoke Clinical Co-Pilot in production and collect evidence.")
    parser.add_argument("--base-url", default=os.environ.get("OPENEMR_PROD_URL", DEFAULT_BASE_URL))
    parser.add_argument("--username", default=os.environ.get("OPENEMR_PROD_USERNAME"))
    parser.add_argument("--password", default=os.environ.get("OPENEMR_PROD_PASSWORD"))
    parser.add_argument("--patient-id", default=os.environ.get("OPENEMR_PROD_PATIENT_ID"))
    parser.add_argument("--prompt", default=DEFAULT_PROMPT)
    parser.add_argument("--evidence-dir")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    run = PocRun(args)
    try:
        return run.run()
    except KeyboardInterrupt:
        return run.fail("interrupted", 130)
    except Exception as exc:
        run.summary["unexpected_exception"] = repr(exc)
        return run.fail("unexpected runner exception", 99)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
