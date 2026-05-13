-- Seed VariabilityToggles (idempotent MERGE).
-- Priority + Description + DefaultJson are kept in sync on re-run;
-- IsEnabled is intentionally only set on INSERT so operators can
-- flip bits manually without the next deploy reverting them.
MERGE INTO dbo.VariabilityToggles AS tgt
USING (VALUES
    (N'turn.user_goal',                   1, 1, N'""',
        N'Every prompt-side attack (jailbreak, PHI extract, advice coerce).'),
    (N'turn.extra_body',                  2, 0, N'null',
        N'Arbitrary extra keys in the POST body: {patient_id:99}, {admin:true}, schema fuzz.'),
    (N'turn.intent_id',                   3, 0, N'"free_text"',
        N'Which of the 6 intents the probe targets; multiplies verifier-path coverage.'),
    (N'turn.source_id',                   4, 0, N'null',
        N'Citation drilldown handle for secondary turns.'),
    (N'turn.conversation_id_strategy',    5, 0, N'"share"',
        N'share | fresh_each_turn | literal:<id> - conversation continuity attacks.'),
    (N'turn.active_patient_context',      6, 0, N'"server-session"',
        N'Body-level patient-claim override probes.'),
    (N'bootstrap.user',                   7, 0, N'{"username":"admin","password":"pass"}',
        N'Login as a different role - front-desk vs. admin attack surface.'),
    (N'turn.headers',                     8, 0, N'{}',
        N'Extra/override request headers: X-Forwarded-User, extra APICSRFTOKEN.'),
    (N'turn.delay_ms',                    9, 0, N'0',
        N'Rate-limit and session-TTL probes.'),
    (N'bootstrap.skip_set_pid',          10, 0, N'false',
        N'Skip the demographics step - "agent with no patient context" regression.')
) AS src(FieldPath, Priority, IsEnabled, DefaultJson, Description)
ON  tgt.FieldPath = src.FieldPath
WHEN MATCHED THEN UPDATE SET
    Priority    = src.Priority,
    DefaultJson = src.DefaultJson,
    Description = src.Description
WHEN NOT MATCHED BY TARGET THEN
    INSERT (FieldPath, Priority, IsEnabled, DefaultJson, Description)
    VALUES (src.FieldPath, src.Priority, src.IsEnabled, src.DefaultJson, src.Description);
