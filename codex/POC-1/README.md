# POC-1 Clinical Co-Pilot Production Invocation

Runs a production login flow against `https://openemr-web-production.up.railway.app/`, opens the Clinical Co-Pilot panel, and posts the prompt `show basic patient data`.

## Required inputs

Provide credentials through parameters or environment variables:

```powershell
$env:OPENEMR_PROD_USERNAME = "..."
$env:OPENEMR_PROD_PASSWORD = "..."
$env:OPENEMR_PROD_PATIENT_ID = "1" # required for successful patient-scoped Co-Pilot calls
.\run-copilot-poc.ps1
```

The runner writes evidence under `POC-1\evidence\<timestamp>\`. Evidence is intentionally ignored by git because a successful run can contain patient data and session-adjacent artifacts.

Without a patient id, production still accepts login but rejects the Co-Pilot API call with `Agent access requires exactly one current patient.`
