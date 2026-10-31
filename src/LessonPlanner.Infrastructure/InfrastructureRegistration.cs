using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using LessonPlanner.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace LessonPlanner.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddLessonInfrastructure(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var publicOrigin = configuration["Quiz:PublicBaseUrl"] ?? throw new InvalidOperationException("Quiz:PublicBaseUrl is required.");
        var policy = new QuizPolicy(new Uri(publicOrigin, UriKind.Absolute),
            TimeSpan.FromHours(configuration.GetValue("Quiz:LifetimeHours", 24)),
            TimeSpan.FromMinutes(configuration.GetValue("Quiz:ApprovalMinutes", 5)),
            configuration.GetValue("Quiz:MaximumAttempts", 200));
        policy.Validate(local);
        services.AddSingleton(policy);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGradingPolicy, ExactMatchGradingPolicy>();
        services.AddSingleton(_ => CreateTableClient(configuration, local));
        services.AddSingleton<AzureTableStore>();
        services.AddSingleton<ILessonStore>(sp => sp.GetRequiredService<AzureTableStore>());
        services.AddSingleton<IQuizStore>(sp => sp.GetRequiredService<AzureTableStore>());
        services.AddHealthChecks().AddCheck<TableReadiness>("table-storage", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    public static TokenCredential CreateCredential(string? managedIdentityClientId, bool local) => local
        ? new AzureCliCredential()
        : string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));

    public static TableClient CreateTableClient(IConfiguration configuration, bool local)
    {
        var tableName = configuration["Storage:TableName"] ?? "lessonplanner";
        var options = new TableClientOptions();
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Retry.MaxRetries = 3;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        var connectionString = configuration["Storage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (!local || !IsLoopbackStorageConnection(connectionString))
                throw new InvalidOperationException("Storage connection strings are permitted only for a loopback emulator in Development or Testing.");
            return new TableClient(connectionString, tableName, options);
        }
        if (!Uri.TryCreate(configuration["Storage:TableServiceUri"], UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme != "https" || !string.IsNullOrEmpty(serviceUri.UserInfo) ||
            !string.IsNullOrEmpty(serviceUri.Query) || !string.IsNullOrEmpty(serviceUri.Fragment))
            throw new InvalidOperationException("Storage:TableServiceUri must be an HTTPS Azure Table service endpoint.");
        return new TableClient(serviceUri, tableName, CreateCredential(configuration["Storage:ManagedIdentityClientId"], local), options);
    }

    public static bool IsLoopbackStorageConnection(string connectionString)
    {
        if (connectionString.Trim().TrimEnd(';').Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)) return true;
        var properties = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).ToArray();
        if (properties.Any(p => p.Length != 2) ||
            properties.Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length) return false;
        var values = properties.ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        var permitted = new HashSet<string>(["AccountName", "AccountKey", "TableEndpoint", "BlobEndpoint", "QueueEndpoint", "DefaultEndpointsProtocol"], StringComparer.OrdinalIgnoreCase);
        if (values.Keys.Any(k => !permitted.Contains(k)) || !values.ContainsKey("TableEndpoint") ||
            !values.TryGetValue("AccountName", out var name) || name != "devstoreaccount1") return false;
        return values.Where(p => p.Key.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            .All(p => Uri.TryCreate(p.Value, UriKind.Absolute, out var uri) && uri.IsLoopback &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
    }
}

internal sealed class TableReadiness(TableClient table) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var _ in table.QueryAsync<Azure.Data.Tables.TableEntity>(maxPerPage: 1,
                select: ["PartitionKey"], cancellationToken: cancellationToken)) break;
            return HealthCheckResult.Healthy();
        }
        catch (Azure.RequestFailedException) { return HealthCheckResult.Unhealthy("Storage is unavailable."); }
    }
}
