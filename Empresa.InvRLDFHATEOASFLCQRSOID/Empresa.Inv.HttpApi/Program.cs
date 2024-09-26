using Empresa.Inv.Application.Shared;
using Empresa.Inv.Application;
 
using Empresa.Inv.EntityFrameworkCore;
using Empresa.Inv.EntityFrameworkCore.Repositories;
 
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Security.Cryptography;
using Microsoft.OpenApi.Models;
 
using Empresa.Inv.Application.Shared.Entities.Dtos;
using Empresa.Inv.Application.Shared.Entities;
using Empresa.Inv.HttpApi.Services;
using Empresa.Inv.Infraestructure;
using WebAppApiArq.Data;
using Empresa.Inv.Dtos;
using Empresa.Inv.Application.Validators;
using FluentValidation.AspNetCore;
using FluentValidation;
using System.Reflection;

namespace Empresa.Inv.Web.Host
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);




            // Configuraci�n CORS
            var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>();
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowSpecificOrigin", builder =>
                {
                    builder.WithOrigins(allowedOrigins)
                           .AllowAnyMethod()
                           .AllowAnyHeader();
                });
            });

            // Configuraci�n JWT
            var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettingsFile>();

            if (string.IsNullOrEmpty(jwtSettings.PrivateKeyPath) || string.IsNullOrEmpty(jwtSettings.PublicKeyPath))
            {
                throw new InvalidOperationException("Las rutas de las claves p�blicas o privadas no est�n configuradas.");
            }

            // Configuraci�n de TwoFactorSettings
            var twoFactorSettings = builder.Configuration.GetSection("TwoFactorAuthentication").Get<TwoFactorSettings>();
            builder.Services.Configure<TwoFactorSettings>(builder.Configuration.GetSection("TwoFactorAuthentication"));

            // Registro de servicios de configuraci�n
            builder.Services.Configure<JwtSettingsFile>(builder.Configuration.GetSection("JwtSettings"));
            builder.Services.AddSingleton(jwtSettings);


            #region Monitoreo

            // A�ade Application Insights a la aplicaci�n
            builder.Services.AddApplicationInsightsTelemetry();

            #endregion


            // Cargar configuraci�n de appsettings.json
            var emailSettings = builder.Configuration.GetSection("EmailSettings").Get<EmailSettings>();

            // Registrar EmailSettings
            builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));




            // Registro de servicios
            builder.Services.AddScoped<JwtTokenService>();
            builder.Services.AddTransient<IEmailSender, EmailSender>(); // Servicio para enviar correos electr�nicos

            // Registro de autorizaci�n personalizada
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("PermissionPolicy", policy =>
                {
                    policy.Requirements.Add(new PermissionRequirement("")); // Se establecer� en el atributo
                });
            });

            // Autenticaci�n JWT
            var privateKeyContent = ReadPemFile(jwtSettings.PrivateKeyPath);
            var publicKeyContent = ReadPemFile(jwtSettings.PublicKeyPath);

            RSA rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyContent.ToCharArray());
            var rsaSecurityKey = new RsaSecurityKey(rsa);

            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidAudience = jwtSettings.Audience,
                    IssuerSigningKey = rsaSecurityKey,
                    ClockSkew = TimeSpan.Zero // Elimina el margen de 5 minutos en la expiraci�n de tokens
                };
            });

            // Configuraci�n de Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .CreateLogger();

            // Agrega un registro de prueba
            Log.Information("Aplicaci�n iniciada.");

            try
            {
                // Usa Serilog como el logger
                builder.Host.UseSerilog();

                // Agregar controladores
                builder.Services.AddControllers();

                #region Validaciones con FluentValidation

                builder.Services.AddFluentValidationAutoValidation();
                builder.Services.AddTransient<IValidator<ProductDTO>, ProductValidator>();

                #endregion

                // Configuraci�n del contexto de EF
                builder.Services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
                        .AddInterceptors(new CustomDbCommandInterceptor())
                        .AddInterceptors(new PerformanceInterceptor()));

                // Registro de repositorios y servicios
                builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));


                #region Manejadores CQRS    MediatR parte 1


                builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(
                    Assembly.GetExecutingAssembly(),
                    typeof(Empresa.Inv.Application.Entidades.ProductEntity.Handlers.GetProductByIdQueryHandler).Assembly
                ));

                #endregion


                builder.Services.AddScoped<IProductCustomRepository, ProductCustomRepository>();
                builder.Services.AddScoped<IInvAppService, InvAppService>();
                builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();


                builder.Services.AddTransient<IEmailSender, EmailSender>();
                // Registrar TwoFactorSettings en el contenedor de servicios
                builder.Services.Configure<TwoFactorSettings>(builder.Configuration.GetSection("TwoFactorAuthentication"));


                // Configuraci�n de Swagger
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v1", new OpenApiInfo { Title = "JwtExample API", Version = "v1" });
                });


                #region MeditR parte 2
                // Configurar MediatR
                builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

                #endregion


                // Configuraci�n de AutoMapper
                builder.Services.AddAutoMapper(typeof(MappingProfile));

                // Configuraci�n del servicio de cach�
                builder.Services.AddMemoryCache();
                builder.Services.AddSingleton<CacheService>();

                var app = builder.Build();


                // Registrar el middleware personalizado para captura de la respuesta
                app.UseMiddleware<ResponseLoggingMiddleware>();


                // Middleware de manejo de excepciones
                app.UseMiddleware<ExceptionHandlingMiddleware>();

                // Configuraci�n de Swagger
                if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI(c =>
                    {
                        c.SwaggerEndpoint("/swagger/v1/swagger.json", "JwtExample API v1");
                    });
                }

                // Utilizaci�n de routing
                app.UseRouting();

                // Implementaci�n de pol�tica CORS
                app.UseCors("AllowSpecificOrigin");

                // Middleware de autenticaci�n y autorizaci�n
                app.UseAuthentication();
                app.UseAuthorization();

                // Configuraci�n de HTTPS
                app.UseHttpsRedirection();

                // Mapeo de controladores
                app.MapControllers();

                app.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "La aplicaci�n fall� al iniciar.");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static string ReadPemFile(string path)
        {
            return File.ReadAllText(path);
        }
    }
}
