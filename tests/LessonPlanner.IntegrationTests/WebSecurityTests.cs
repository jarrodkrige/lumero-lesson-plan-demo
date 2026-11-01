using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using LessonPlanner.Web.Security;
using LessonPlanner.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace LessonPlanner.IntegrationTests;

public sealed partial class WebSecurityTests
{
    [Theory]
    [InlineData("anonymous")]
    [InlineData("missing-tenant")]
    [InlineData("missing-object")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-object")]
    [InlineData("invalid-tenant")]
    [InlineData("invalid-object")]
    public void Presenter_policy_rejects_untrusted_identity(string variation)
    {
        var claims = new List<Claim>();
        if (variation != "missing-tenant") claims.Add(new("tid", variation switch
        { "wrong-tenant" => Guid.NewGuid().ToString("D"), "invalid-tenant" => "untrusted", _ => TestData.Presenter.TenantId.ToString("D") }));
        if (variation != "missing-object") claims.Add(new("oid", variation switch
        { "wrong-object" => Guid.NewGuid().ToString("D"), "invalid-object" => "untrusted", _ => TestData.Presenter.ObjectId.ToString("D") }));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, variation == "anonymous" ? null : "test-cookie"));
        Assert.False(Policy().Allows(user));
        var access = new PresenterAccess(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } }, Policy());
        Assert.Throws<DomainException>(access.RequireIdentity);
        Assert.Throws<DomainException>(access.RequireCaller);
    }

    [Fact]
    public void Publication_caller_comes_from_the_authenticated_context_and_configuration()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", TestData.Presenter.TenantId.ToString("D")), new("oid", TestData.Presenter.ObjectId.ToString("D"))
        ], "test-cookie"));
        var access = new PresenterAccess(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } }, Policy());
        Assert.Equal(new PublicationCaller(TestData.Presenter, TestData.ClientId), access.RequireCaller());
    }

    [Theory]
    [InlineData("legacy-secret")]
    [InlineData("legacy-certificate")]
    [InlineData("nested-secret")]
    [InlineData("secret-source")]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("invalid-managed-identity")]
    [InlineData("empty-managed-identity")]
    [InlineData("production-certificate")]
    public void Entra_configuration_rejects_implicit_or_secret_credential_fallback(string variation)
    {
        var values = new Dictionary<string, string?> { ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity" };
        switch (variation)
        {
            case "legacy-secret": values["AzureAd:ClientSecret"] = "not-a-real-secret"; break;
            case "legacy-certificate": values["AzureAd:ClientCertificates:0:SourceType"] = "Path"; break;
            case "nested-secret": values["AzureAd:ClientCredentials:0:ClientSecret"] = "not-a-real-secret"; break;
            case "secret-source": values["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret"; break;
            case "missing": values.Clear(); break;
            case "multiple": values["AzureAd:ClientCredentials:1:SourceType"] = "SignedAssertionFromManagedIdentity"; break;
            case "invalid-managed-identity": values["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = "not-an-id"; break;
            case "empty-managed-identity": values["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = Guid.Empty.ToString("D"); break;
            case "production-certificate":
                values["AzureAd:ClientCredentials:0:SourceType"] = "Path";
                values["AzureAd:ClientCredentials:0:CertificateDiskPath"] = "local-development.pfx";
                break;
        }
        Assert.Throws<InvalidOperationException>(() => WebSecurityRegistration.ValidateCredentials(Configuration(values), false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_federated_managed_identity_is_accepted(bool userAssigned)
    {
        var values = new Dictionary<string, string?> { ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity" };
        if (userAssigned) values["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = TestData.ClientId;
        WebSecurityRegistration.ValidateCredentials(Configuration(values), false);
    }

    [Fact]
    public void Local_certificate_is_explicit_and_never_a_production_fallback()
    {
        var configuration = Configuration(new()
        {
            ["AzureAd:ClientCredentials:0:SourceType"] = "Path",
            ["AzureAd:ClientCredentials:0:CertificateDiskPath"] = "local-development.pfx"
        });
        WebSecurityRegistration.ValidateCredentials(configuration, true);
        Assert.Throws<InvalidOperationException>(() => WebSecurityRegistration.ValidateCredentials(configuration, false));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("other-attempt")]
    [InlineData("other-quiz")]
    [InlineData("expired")]
    [InlineData("quiz-expired")]
    [InlineData("future-issued")]
    [InlineData("oversized")]
    public void Protected_attempt_ticket_rejects_replay_outside_its_exact_binding(string variation)
    {
        var clock = new MutableClock();
        var tickets = new ParticipantTickets(new EphemeralDataProtectionProvider(), clock);
        var quizId = DomainRules.NewId();
        var issued = tickets.Issue(quizId, clock.GetUtcNow().AddMinutes(variation == "quiz-expired" ? 2 : 60));
        var token = issued.Token;
        var attempt = issued.Id;
        if (variation == "tampered") token = "changed" + token[7..];
        if (variation == "other-attempt") attempt = DomainRules.NewId();
        if (variation == "other-quiz") quizId = DomainRules.NewId();
        if (variation == "expired") clock.Advance(TimeSpan.FromMinutes(31));
        if (variation == "quiz-expired") clock.Advance(TimeSpan.FromMinutes(3));
        if (variation == "future-issued") clock.Advance(TimeSpan.FromSeconds(-1));
        if (variation == "oversized") token = new string('a', 4097);
        var error = Assert.Throws<DomainException>(() => tickets.Validate(token, attempt, quizId));
        Assert.Equal("attempt_expired", error.Code);
    }

    [Fact]
    public void Protected_attempt_ticket_survives_key_provider_restart_and_validates_against_the_same_quiz()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".local", "ticket-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var clock = new MutableClock();
            var quizId = DomainRules.NewId();
            var first = new ParticipantTickets(DataProtectionProvider.Create(new DirectoryInfo(path), keys => keys.SetApplicationName("Lumero.TestRestart")), clock);
            var issued = first.Issue(quizId, clock.GetUtcNow().AddHours(1));
            var restarted = new ParticipantTickets(DataProtectionProvider.Create(new DirectoryInfo(path), keys => keys.SetApplicationName("Lumero.TestRestart")), clock);
            restarted.Validate(issued.Token, issued.Id, quizId);
            var anotherApplication = new ParticipantTickets(DataProtectionProvider.Create(new DirectoryInfo(path), keys => keys.SetApplicationName("Lumero.AnotherApplication")), clock);
            Assert.Throws<DomainException>(() => anotherApplication.Validate(issued.Token, issued.Id, quizId));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void Model_gate_enforces_global_and_per_presenter_bounds_and_releases_once()
    {
        var gate = new ModelWorkGate();
        var first = gate.TryEnter(TestData.Presenter);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(TestData.Presenter));
        using var second = gate.TryEnter(WebTestHost.SecondPresenter);
        Assert.NotNull(second);
        var thirdPresenter = TestData.Presenter with { ObjectId = Guid.NewGuid() };
        Assert.Null(gate.TryEnter(thirdPresenter));
        first.Dispose();
        first.Dispose();
        using var third = gate.TryEnter(thirdPresenter);
        Assert.NotNull(third);
        Assert.Null(gate.TryEnter(TestData.Presenter));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("redirect", false)]
    [InlineData("not-found", false)]
    [InlineData("wrong-type", false)]
    [InlineData("missing-id", false)]
    [InlineData("wrong-id", false)]
    [InlineData("duplicate-id", false)]
    [InlineData("empty", false)]
    [InlineData("oversized", false)]
    public async Task Qr_reachability_requires_the_exact_canonical_quiz_and_a_complete_bounded_page(string variation, bool expected)
    {
        var quizId = DomainRules.NewId();
        var policy = new QuizPolicy(new Uri("https://classroom.example.org"), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 200);
        var response = new HttpResponseMessage(variation switch
        { "redirect" => HttpStatusCode.Redirect, "not-found" => HttpStatusCode.NotFound, _ => HttpStatusCode.OK })
        {
            Content = new StringContent(variation switch { "empty" => "", "oversized" => new string('a', 98_305), _ => "<html><body>Quiz</body></html>" })
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(variation == "wrong-type" ? "application/json" : "text/html");
        if (variation != "missing-id") response.Headers.Add("X-Lumero-Quiz-Id", variation == "wrong-id" ? DomainRules.NewId() : quizId);
        if (variation == "duplicate-id") response.Headers.Add("X-Lumero-Quiz-Id", quizId);
        using var handler = new FixedResponseHandler(response);
        using var http = new HttpClient(handler);
        var verifier = new QuizLinkVerifier(policy, http);
        Assert.Equal(expected, await verifier.IsReachableAsync(quizId, CancellationToken.None));
        Assert.Equal(policy.PublicUrl(quizId), handler.LastUri);
    }

    private static PresenterPolicy Policy() => new(TestData.Presenter.TenantId, new HashSet<Guid> { TestData.Presenter.ObjectId }, TestData.ClientId);
    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }
}
