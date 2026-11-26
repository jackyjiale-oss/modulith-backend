-- Selects what is wrong after the whole ladder: the code is gone, or AddPhoneCodePurpose did not mark it as a verification code (1).
SELECT 'phone verification code lost' WHERE NOT EXISTS (SELECT 1 FROM [auth].[PhoneVerificationCodes] WHERE [Id] = 'a4000000-0000-0000-0000-000000000002');

SELECT 'phone verification code Purpose is not Verify (1)'
FROM [auth].[PhoneVerificationCodes]
WHERE [Id] = 'a4000000-0000-0000-0000-000000000002' AND [Purpose] <> 1;
