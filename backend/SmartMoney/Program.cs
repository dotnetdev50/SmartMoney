using Microsoft.EntityFrameworkCore;
using SmartMoney.Application.Options;
using SmartMoney.Application.Services;
using SmartMoney.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

//Add Cors policy for development - allowing requests from the frontend running on localhost:5173
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p =>
        p.WithOrigins("http://localhost:5174", "https://localhost:7037")
         .AllowAnyHeader()
         .AllowAnyMethod());
});

// DbContext - LocalDB for now
var cs = builder.Configuration.GetConnectionString("Default")
         ?? throw new InvalidOperationException("ConnectionStrings:Default is missing.");
builder.Services.AddDbContext<SmartMoneyDbContext>(opt => opt.UseSqlServer(cs));

//NseJobOptions binding
builder.Services.Configure<NseJobOptions>(builder.Configuration.GetSection("NseJob"));

// Application services
builder.Services.AddScoped<NormalizationService>();
builder.Services.AddScoped<ParticipantScoreCalculator>();
builder.Services.AddScoped<BiasEngineService>();
builder.Services.Configure<NseOptions>(builder.Configuration.GetSection("Nse"));

//Csv Ingestion service with custom HttpClient configuration
builder.Services.AddHttpClient<CsvIngestionService>((sp, http) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NseOptions>>().Value;
    http.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
});

//Daily pipeline service
builder.Services.AddScoped<DailyPipelineService>();

//Market presentation service
builder.Services.AddScoped<MarketPresentationService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("dev");
app.MapControllers();

await app.RunAsync();