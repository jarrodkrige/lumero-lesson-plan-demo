using System.Security.Cryptography;
using System.Text.Json;
using LessonPlanner.Core;
using Microsoft.AspNetCore.DataProtection;

namespace LessonPlanner.Web.Security;

public sealed record ParticipantTicket(string Id, string Token);

public sealed class ParticipantTickets(IDataProtectionProvider keys, TimeProvider clock)
{
    private readonly IDataProtector protector = keys.CreateProtector("Lumero.ParticipantAttempt.v1");

    public ParticipantTicket Issue(string quizId, DateTimeOffset quizExpiresAt)
    {
        DomainRules.RequireId(quizId);
        var now = clock.GetUtcNow();
        if (quizExpiresAt <= now) throw InvalidTicket();
        var expires = now.AddMinutes(30) < quizExpiresAt ? now.AddMinutes(30) : quizExpiresAt;
        var envelope = new AttemptEnvelope(DomainRules.NewId(), quizId, now, expires);
        return new(envelope.Id, protector.Protect(JsonSerializer.Serialize(envelope, DomainRules.Json)));
    }

    public void Validate(string? token, string? attemptId, string quizId)
    {
        DomainRules.RequireId(attemptId);
        DomainRules.RequireId(quizId);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) throw InvalidTicket();
        try
        {
            var envelope = JsonSerializer.Deserialize<AttemptEnvelope>(protector.Unprotect(token), DomainRules.Json);
            if (envelope is null || envelope.Id != attemptId || envelope.QuizId != quizId ||
                envelope.ExpiresAt <= clock.GetUtcNow() || envelope.IssuedAt > clock.GetUtcNow()) throw InvalidTicket();
        }
        catch (CryptographicException) { throw InvalidTicket(); }
        catch (JsonException) { throw InvalidTicket(); }
    }

    private static DomainException InvalidTicket() => new("attempt_expired", "Refresh the quiz before submitting your answers.");
    private sealed record AttemptEnvelope(string Id, string QuizId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
