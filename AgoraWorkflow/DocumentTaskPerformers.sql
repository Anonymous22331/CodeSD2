-- Операнд Конструктора ролей для вида «Входящее письмо Агора».
-- Возвращает исполнителей и связи для сброса кэша роли после изменений задания.
CREATE OR ALTER PROCEDURE dbo.dvbo_Operand_Agora_DocumentTaskPerformers
    @cardIds dbo.GuidList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    -- MainInfo документа, список заданий и список групп заданий.
    WITH Documents AS (
        SELECT d.InstanceID AS CardID, d.Tasks AS ListID
        FROM dbo.[dvtable_{30eb9b87-822b-4753-9a50-a1825dca1b74}] d
        JOIN @cardIds ids ON ids.ID = d.InstanceID
        WHERE d.Kind = '9530f638-6669-48b8-867a-a135ce4232a6'
        UNION ALL
        SELECT d.InstanceID, d.Tasks
        FROM dbo.[dvtable_{30eb9b87-822b-4753-9a50-a1825dca1b74}_archive] d
        JOIN @cardIds ids ON ids.ID = d.InstanceID
        WHERE d.Kind = '9530f638-6669-48b8-867a-a135ce4232a6'
    ), Tasks AS (
        SELECT t.InstanceID AS TaskID, t.ChildTaskList AS ListID, t.CompletedUser
        FROM dbo.[dvtable_{20d21193-9f7f-4b62-8d69-272e78e1d6a8}] t
        JOIN dbo.dvsys_instances i ON i.InstanceID = t.InstanceID AND i.Deleted = 0
        UNION ALL
        SELECT InstanceID, ChildTaskList, CompletedUser
        FROM dbo.[dvtable_{20d21193-9f7f-4b62-8d69-272e78e1d6a8}_archive]
    ), TaskLinks AS (
        SELECT InstanceID AS ParentList, Task AS TaskID
        FROM dbo.[dvtable_{9bc6b0e9-a809-43f1-a27f-323a28d1b010}]
        UNION ALL
        SELECT InstanceID, Task
        FROM dbo.[dvtable_{9bc6b0e9-a809-43f1-a27f-323a28d1b010}_archive]
    ), Groups AS (
        SELECT g.InstanceID AS GroupID, g.TaskList AS ListID
        FROM dbo.[dvtable_{7a6593bb-0bdb-4082-b43b-8f26594eff91}] g
        JOIN dbo.dvsys_instances i ON i.InstanceID = g.InstanceID AND i.Deleted = 0
        UNION ALL
        SELECT InstanceID, TaskList
        FROM dbo.[dvtable_{7a6593bb-0bdb-4082-b43b-8f26594eff91}_archive]
    ), GroupLinks AS (
        SELECT InstanceID AS ParentList, TaskGroup AS GroupID
        FROM dbo.[dvtable_{4aed5aec-0ed8-4743-9c78-63d47128455e}]
        UNION ALL
        SELECT InstanceID, TaskGroup
        FROM dbo.[dvtable_{4aed5aec-0ed8-4743-9c78-63d47128455e}_archive]
    ), Edges AS (
        SELECT l.ParentList, t.ListID, t.TaskID AS LinkedCardID, t.TaskID, t.CompletedUser
        FROM TaskLinks l
        JOIN Tasks t ON t.TaskID = l.TaskID
        UNION ALL
        SELECT l.ParentList, g.ListID, g.GroupID, NULL, NULL
        FROM GroupLinks l
        JOIN Groups g ON g.GroupID = l.GroupID
    ), TaskTree AS (
        SELECT d.CardID, d.ListID, CONVERT(uniqueidentifier, NULL) AS LinkedCardID,
               CONVERT(uniqueidentifier, NULL) AS TaskID, CONVERT(uniqueidentifier, NULL) AS CompletedUser,
               CONVERT(varchar(max), '|' + CONVERT(varchar(36), d.ListID) + '|') AS ListPath
        FROM Documents d
        WHERE d.ListID IS NOT NULL
        UNION ALL
        SELECT t.CardID, e.ListID, e.LinkedCardID, e.TaskID, e.CompletedUser,
               t.ListPath + ISNULL(CONVERT(varchar(36), e.ListID) + '|', '')
        FROM TaskTree t
        JOIN Edges e ON e.ParentList = t.ListID
        WHERE e.ListID IS NULL OR CHARINDEX('|' + CONVERT(varchar(36), e.ListID) + '|', t.ListPath) = 0
    )
    SELECT DISTINCT CardID, ListID, LinkedCardID, TaskID, CompletedUser
    INTO #DocumentTasks
    FROM TaskTree
    OPTION (MAXRECURSION 0);

    -- Исполнители из карточки задания, текущие исполнители и завершивший задание.
    WITH Performers AS (
        SELECT InstanceID, Employee FROM dbo.[dvtable_{80c8f976-1f0d-4b67-82d2-a59230ecade7}]
        UNION ALL
        SELECT InstanceID, Employee FROM dbo.[dvtable_{80c8f976-1f0d-4b67-82d2-a59230ecade7}_archive]
        UNION ALL
        SELECT InstanceID, Employee FROM dbo.[dvtable_{134ea363-f5a8-4b80-b302-b21c954ce983}]
        UNION ALL
        SELECT InstanceID, Employee FROM dbo.[dvtable_{134ea363-f5a8-4b80-b302-b21c954ce983}_archive]
    )
    SELECT t.CardID, p.Employee AS [Value], 13 AS [Type]
    FROM #DocumentTasks t
    JOIN Performers p ON p.InstanceID = t.TaskID
    WHERE p.Employee IS NOT NULL
    UNION
    SELECT CardID, CompletedUser, 13
    FROM #DocumentTasks
    WHERE CompletedUser IS NOT NULL;

    SELECT CardID, LinkedCardID FROM #DocumentTasks WHERE LinkedCardID IS NOT NULL
    UNION
    SELECT CardID, ListID FROM #DocumentTasks WHERE ListID IS NOT NULL;
END;
