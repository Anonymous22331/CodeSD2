-- Позволяет автору сохранить своё входящее письмо в подготовке, пока регистратор пуст.
CREATE OR ALTER PROCEDURE dbo.dvbo_Operand_Agora_DraftIncomingRegistrar
    @cardIds dbo.GuidList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT d.InstanceID AS CardID, d.Author AS [Value], 13 AS [Type]
    FROM @cardIds AS ids
    JOIN dbo.[dvtable_{30eb9b87-822b-4753-9a50-a1825dca1b74}] AS d ON d.InstanceID = ids.ID
    JOIN dbo.[dvtable_{91b2c5f7-9324-4cef-9afe-a457c8310f06}] AS s ON s.InstanceID = d.InstanceID
    WHERE d.Kind = '9530f638-6669-48b8-867a-a135ce4232a6'
      AND s.State = 'de445cf0-2482-48ae-80b3-49746540c4de'
      AND (d.Registrar IS NULL OR d.Registrar = '00000000-0000-0000-0000-000000000000')
      AND d.Author IS NOT NULL;

    SELECT ID AS CardID, ID AS LinkedCardID
    FROM @cardIds WHERE 1 = 0;
END;
