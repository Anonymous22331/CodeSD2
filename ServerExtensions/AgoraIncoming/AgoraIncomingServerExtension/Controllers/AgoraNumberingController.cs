using System;
using Microsoft.AspNetCore.Mvc;

namespace Agora.Web;

[Route("api/agora/incoming")]
public sealed class AgoraNumberingController(IIncomingNumbering commands) : ControllerBase
{
    [HttpPost("number")]
    [Consumes("application/json")]
    public IActionResult Number([FromBody] NumberIncomingRequest request)
    {
        if (request == null || request.CardId == Guid.Empty)
            return BadRequest("Укажите входящее письмо.");

        var result = commands.Number(request.CardId);
        return result.Outcome switch
        {
            IncomingNumberingOutcome.Assigned => Ok(new { id = result.Id, number = result.Number, timestamp = result.Timestamp }),
            IncomingNumberingOutcome.Forbidden => StatusCode(403),
            IncomingNumberingOutcome.NotFound => NotFound(),
            IncomingNumberingOutcome.Locked => Conflict("Карточка занята."),
            IncomingNumberingOutcome.RequiresPreparation => Conflict("Номер присваивается в состоянии «Подготовка»."),
            IncomingNumberingOutcome.RequiresJournal => BadRequest("Выберите журнал Агора."),
            _ => throw new InvalidOperationException("Неизвестный результат операции с входящим письмом.")
        };
    }
}

public sealed class NumberIncomingRequest
{
    public Guid CardId { get; set; }
}
