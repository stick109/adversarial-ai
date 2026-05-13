-- Insert-only seed: an operator may have tuned the loop interval at
-- runtime and we must not overwrite that on every schema re-apply.
IF NOT EXISTS (SELECT 1 FROM dbo.Parameters WHERE [Key] = N'executor-loop-minutes')
INSERT INTO dbo.Parameters ([Key], [Value]) VALUES (N'executor-loop-minutes', N'5');
