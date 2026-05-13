-- POC-1 baseline golden-smoke test (plan §2.3).
-- Carries no attacker payload; must always come back 200 OK.  A
-- failure here is a deployment or regression bug, not an
-- attack-surface signal.
IF NOT EXISTS (SELECT 1 FROM dbo.PenetrationTests WHERE Category = N'golden_smoke')
INSERT INTO dbo.PenetrationTests (Category, Bootstrap, Turns, Description, CreatedBy)
VALUES (
    N'golden_smoke',
    N'{"patient_id": 1}',
    N'[{"intent_id": "basic_patient_data"}]',
    N'POC-1 baseline: basic_patient_data on patient pid 1; must return 200 with non-empty answer_blocks.',
    N'seed'
);
