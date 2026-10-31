var builder = DistributedApplication.CreateBuilder(args);
var mcp = builder.AddProject<Projects.LessonPlanner_Mcp>("mcp");
builder.AddProject<Projects.LessonPlanner_Web>("web").WithReference(mcp);
builder.Build().Run();
