
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using OnTransfert.srv.Hubs;

namespace OneTransfert.srv
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
#if DEBUG

#else
            builder.WebHost.ConfigureKestrel((context, options) =>
            {
                options.ListenAnyIP(80, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                    //listenOptions.UseHttps();
                });
            });
#endif
            var corsOrigins = builder.Configuration["CorsOrigins"];
            builder.Services.AddCors(options => {

    
                options.AddPolicy("AllowAll",
                    b =>
                    {

                        //b.WithOrigins("https://pwdman.duckdns.org; https://felixyangyicheng.github.io; https://yangyichengfelix.github.io");
                        //b.WithOrigins(corsOrigins ?? throw new ArgumentException());
                        b.SetIsOriginAllowed(origin => true);
                        b.AllowAnyMethod();
                        b.AllowAnyHeader();
                        //b.AllowCredentials();
                    }
            
                       );
                    //.AllowAnyMethod()
                    //.AllowAnyHeader()
                    //.SetIsOriginAllowed(origin => true) // allow any origin
                    //.AllowCredentials()
                    //.WithExposedHeaders("X-Pagination")
                    //   );
            });
            // Add services to the container.
            //builder.Services.AddAuthorization();
            builder.Services.AddSignalR();

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            app.UseRouting();

            app.UseCors("AllowAll");
//            app.UseAuthentication();
            app.UseAuthorization();
            app.MapHub<FileTransferHub>("/file-transfer-hub").RequireCors("AllowAll");
            app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");



            app.Run();
        }
    }
}
