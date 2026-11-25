-- Операнд ролевой модели входящих Агора для журналов ДО Саратовский и ДО Казанский.
-- Штатные параметры роли сопоставляют возвращённых сотрудников с пользователем
-- и с сотрудниками, которых он замещает постоянно или временно.
CREATE OR ALTER PROCEDURE dbo.dvbo_Operand_Agora_IncomingJournalRegistrars
    @cardIds dbo.GuidList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    WITH IncomingJournals AS (
        SELECT d.InstanceID AS CardID, fields.JournalId
        FROM @cardIds ids
        JOIN dbo.[dvtable_{30eb9b87-822b-4753-9a50-a1825dca1b74}] d ON d.InstanceID = ids.ID
        JOIN dbo.[dvtable_{e8a5eeac-be18-4227-95bf-ff2c16f020a4}] fields ON fields.InstanceID = d.InstanceID
        WHERE d.Kind = '9530f638-6669-48b8-867a-a135ce4232a6'
        UNION ALL
        SELECT d.InstanceID, fields.JournalId
        FROM @cardIds ids
        JOIN dbo.[dvtable_{30eb9b87-822b-4753-9a50-a1825dca1b74}_archive] d ON d.InstanceID = ids.ID
        JOIN dbo.[dvtable_{e8a5eeac-be18-4227-95bf-ff2c16f020a4}_archive] fields ON fields.InstanceID = d.InstanceID
        WHERE d.Kind = '9530f638-6669-48b8-867a-a135ce4232a6'
    ),
    JournalGroups AS (
        SELECT CONVERT(uniqueidentifier, 'd114b483-b990-42f0-b480-149408d6e597') AS JournalId,
               CONVERT(uniqueidentifier, '880abea9-5108-4045-9a43-a970994f9a34') AS GroupId
        UNION ALL
        SELECT CONVERT(uniqueidentifier, '52b3f106-0450-44ec-b6d5-ca914c44f255'),
               CONVERT(uniqueidentifier, 'b066d611-5906-4dd8-94d0-ec9c288ee16f')
    )
    SELECT DISTINCT d.CardID, members.EmployeeID AS [Value], 13 AS [Type]
    FROM IncomingJournals d
    JOIN JournalGroups j ON j.JournalId = d.JournalId
    JOIN dbo.[dvtable_{a960e37b-f1bd-4981-858d-ae9706e0571e}] members ON members.ParentRowID = j.GroupId
    WHERE members.EmployeeID IS NOT NULL;

    SELECT ID AS CardID, CONVERT(uniqueidentifier, '6710b92a-e148-4363-8a6f-1aa0eb18936c') AS LinkedCardID
    FROM @cardIds;
END;
