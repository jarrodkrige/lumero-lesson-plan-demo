using LessonPlanner.Mcp;

var builder = WebApplication.CreateBuilder(args);
var app = McpApplication.Build(builder);
await app.RunAsync();

public partial class Program;
