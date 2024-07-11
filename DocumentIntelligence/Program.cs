using Azure;
using Azure.AI.FormRecognizer;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var configuration = builder.Configuration;

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    c.EnableAnnotations();
});

// Register FormRecognizerClient for dependency injection
builder.Services.AddSingleton(serviceProvider =>
{
    var endpoint = Environment.GetEnvironmentVariable("FORM_RECOGNIZER_ENDPOINT") ?? configuration["FormRecognizer:Endpoint"];
    var key = Environment.GetEnvironmentVariable("FORM_RECOGNIZER_KEY") ?? configuration["FormRecognizer:Key"];
    return new FormRecognizerClient(new Uri(endpoint), new AzureKeyCredential(key));
});

// Configure CORS to allow all origins
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowAllOrigins");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
    });
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=DocumentIntelligence}/{action=Index}/{id?}");

app.MapControllers();

app.Run();