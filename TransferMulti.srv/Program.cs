using Microsoft.AspNetCore.Server.Kestrel.Core;
using OnTransfert.srv.Hubs;

namespace TransferMulti.srv
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

#if !DEBUG
            var port = builder.Configuration.GetValue<int?>("Port") ?? 80;
            builder.WebHost.ConfigureKestrel((context, options) =>
            {
                options.ListenAnyIP(port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });
#endif

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", b =>
                {
                    b.SetIsOriginAllowed(origin => true)
                     .AllowAnyMethod()
                     .AllowAnyHeader();
                });
            });

            builder.Services.AddSignalR();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseRouting();
            app.UseCors("AllowAll");
            app.UseAuthorization();
            app.MapHub<FileTransferHub>("/file-transfer-hub").RequireCors("AllowAll");

            app.Run();
        }
    }
}
