# Environment Notes

## 2026-05-11 - GitLab credential helper stdin issue from PowerShell

- Symptom: Attempts to call `git credential fill` from inline PowerShell, including line-based pipeline input and a redirected .NET `ProcessStartInfo` stdin stream, failed with `fatal: refusing to work with credential missing protocol field`.
- Likely cause: The credential helper did not receive stdin in the exact byte/line format Git expects from the PowerShell invocation style used in this environment.
- Workaround: Use normal `git push` HTTPS flows so Git Credential Manager handles authentication directly. For creating the GitLab project, the first push to `https://labs.gauntletai.com/konstantinsurkov/adversarial-ai.git` successfully created the project automatically.
- Follow-up: Install and authenticate `glab` if scripted GitLab project creation or metadata updates are needed later.
