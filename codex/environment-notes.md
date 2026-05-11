# Environment Notes

## 2026-05-11 - Bundled Python lacks HTTP/browser helper packages

- Symptom: The bundled Python at `C:\Users\s-109\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe` does not have `requests`, `bs4`, or Playwright installed.
- Likely cause: The local Codex runtime Python is minimal and does not include convenience packages for HTTP scraping or browser automation.
- Workaround: `POC-1\invoke_copilot.py` uses only Python standard library modules: `urllib`, `http.cookiejar`, and `html.parser`.
- Follow-up: If future POCs need screenshots or full browser interaction, install/enable Playwright or use the Codex Browser plugin.
