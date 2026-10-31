using LessonPlanner.Web;

var builder = WebApplication.CreateBuilder(args);
await using var app = WebApplicationFactory.Build(builder);
await app.RunAsync();

public partial class Program;
