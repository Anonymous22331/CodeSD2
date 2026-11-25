using System;
using Microsoft.AspNetCore.Mvc;
using DocsVision.Platform.WebClient.Models;
using DocsVision.WebClient.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace Agora.Web;

public sealed class AgoraRoutingExtension : WebClientExtension
{
    /// <summary>Регистрирует сервисы карточек входящих писем Агора.</summary>
    public override void InitializeServiceCollection(IServiceCollection services)
    {
        services.AddScoped<IIncomingRouting, IncomingRouting>();
        services.AddScoped<IIncomingNumbering, IncomingNumbering>();
    }
}

[Route("api/agora/incoming")]
public sealed class AgoraRoutingController(IIncomingRouting commands) : ControllerBase
{
    [HttpPost("register")]
    [Consumes("application/json")]
    public IActionResult Register([FromBody] SendIncomingRequest request)
    {
        if (request == null || request.CardId == Guid.Empty || request.Timestamp <= 0)
            return BadRequest("Укажите письмо и версию карточки.");
        return commands.Register(request.CardId, request.Timestamp) switch
        {
            IncomingRoutingOutcome.Sent => Ok(CommonResponse.CreateSuccess()),
            IncomingRoutingOutcome.Forbidden => StatusCode(403),
            IncomingRoutingOutcome.NotFound => NotFound(),
            IncomingRoutingOutcome.MainFileMissing => UnprocessableEntity("ВАЖНО! Вложите файл!"),
            _ => Conflict("Письмо изменилось или занято. Обновите карточку и повторите регистрацию.")
        };
    }

    [HttpPost("send")]
    [Consumes("application/json")]
    public IActionResult Send([FromBody] SendIncomingRequest request)
    {
        if (request == null || request.CardId == Guid.Empty || request.Timestamp <= 0)
            return BadRequest("Укажите письмо и версию карточки.");

        var outcome = commands.Send(request.CardId, request.Timestamp);
        return outcome switch
        {
            IncomingRoutingOutcome.Sent => Ok(CommonResponse.CreateSuccess()),
            IncomingRoutingOutcome.Forbidden => StatusCode(403),
            IncomingRoutingOutcome.NotFound => NotFound(),
            IncomingRoutingOutcome.RequiresRegistration => Conflict("Письмо должно быть зарегистрировано."),
            IncomingRoutingOutcome.Locked => Conflict("Карточка занята. Обновите её и повторите отправку."),
            IncomingRoutingOutcome.Changed => Conflict("Карточка изменилась. Обновите её и повторите отправку."),
            IncomingRoutingOutcome.Disabled => Conflict("Маршрут Агора для этой операции отключён."),
            _ => throw new InvalidOperationException("Неизвестный результат операции с входящим письмом.")
        };
    }
}

public sealed class SendIncomingRequest
{
    public Guid CardId { get; set; }
    public long Timestamp { get; set; }
}
