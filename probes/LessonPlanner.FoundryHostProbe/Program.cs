using System.Reflection;
using Azure.AI.Extensions.OpenAI;
using LessonPlanner.FoundryHostProbe;
using Microsoft.Agents.AI.Foundry.Hosting;

if (args is [] or ["--check"])
{
#pragma warning disable MAAI001 // The hosting package deliberately exposes experimental APIs.
    var hostingVersion = typeof(FoundryHostingExtensions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
#pragma warning restore MAAI001
    Console.WriteLine($"Hosting assembly: {hostingVersion}");
    Console.WriteLine($"Project reference type: {typeof(AgentReference).FullName}");
    Console.WriteLine("Compile-only check. No host, credential, registration, network request or inference was started.");
    return 0;
}

if (args is not ["--serve"] ||
    Environment.GetEnvironmentVariable("Probe__ExecutionAcknowledgement") != "existing-project-inference-approved")
{
    Console.Error.WriteLine("Use --check for local verification. --serve requires separate Azure authorisation and an explicit execution acknowledgement.");
    return 2;
}

try
{
    await ProbeHost.RunAsync(ProbeSettings.Read());
    return 0;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("Invalid probe configuration. See the probe README; no configuration values are printed.");
    return 2;
}
