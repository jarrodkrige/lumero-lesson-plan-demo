using LessonPlanner.IntegrationTests;

var port = 5180;
if (args.Length != 0 && (args.Length != 2 || args[0] != "--port" || !int.TryParse(args[1], out port) || port is < 1024 or > 65535))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests\\LessonPlanner.LocalHarness -- [--port 5180]");
    return 2;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
await using var harness = await LocalBrowserHarness.StartAsync(port);
Console.WriteLine("LOCAL VERIFICATION ONLY: synthetic model outputs and presenter identity. Never deploy this test executable.");
Console.WriteLine("Actual local Table persistence, authenticated official MCP HTTP transport, server grading, DOCX and QR rendering.");
Console.WriteLine($"Open {new Uri(harness.Origin, "/_local")}");
Console.WriteLine("Press Ctrl+C to stop and remove this harness's local test table and keys.");
await harness.WaitForShutdownAsync(stopping.Token);
return 0;
